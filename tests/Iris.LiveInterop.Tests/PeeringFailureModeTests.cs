using System.Net;
using Iris.Client;
using Iris.Client.Auth;
using Iris.Server;
using Iris.Server.Delivery;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iris.LiveInterop.Tests;

/// <summary>
/// Phase 138.8: peering failure-mode drill. Two tests against the real Lemmy container (gated on the
/// container being up): (1) a delivery to the Lemmy community's shared inbox succeeds (the happy path
/// works against a real non-Iris peer), and (2) a delivery to a deliberately unreachable inbox is
/// dead-lettered with <see cref="DeadLetterFailureKind.TransportError"/> after the configured retry
/// budget is exhausted — the exact path that fires when a peer goes down.
/// </summary>
/// <remarks>
/// The unreachable-inbox test uses <c>http://localhost:9999/inbox</c> (a port nothing listens on)
/// rather than stopping the Lemmy container: it exercises the same <see cref="DeliveryWorker"/> +
/// <see cref="IDeliveryDeadLetterStore"/> path (connection-refused → <see cref="DeadLetterFailureKind.TransportError"/>
/// → retry with exponential backoff → dead-letter) without the fragility and time cost of a
/// Docker-orchestration test. The Lemmy-specific give-up window is documented in the change doc.
/// </remarks>
public sealed class PeeringFailureModeTests
{
    private const string LemmyBaseUri = "http://localhost:8091";
    private const string LemmySharedInboxUri = "http://localhost:8091/inbox";
    private const string LemmyCommunityIri = "https://lemmy.luit.ink/c/interop";
    private const string LemmyCommunityKeyId = "https://lemmy.luit.ink/c/interop#main-key";
    private const string UnreachableInboxUri = "http://localhost:9999/inbox";
    private const string IrisActorIri = "https://iris.test/ap/v1/u/alice";
    private const string IrisActorKeyId = "https://iris.test/ap/v1/u/alice#main-key";

    private static KeyPair? _lemmyKey;
    private static Exception? _lemmyKeyError;
    private static bool _lemmyKeyAttempted;

    /// <summary>
    /// Fetches the Lemmy interop community's public key exactly once (static cache).
    /// Returns null when the Lemmy container is not reachable.
    /// </summary>
    private static KeyPair? GetLemmyKey()
    {
        if (_lemmyKeyAttempted)
        {
            return _lemmyKey;
        }

        _lemmyKeyAttempted = true;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var request = new HttpRequestMessage(HttpMethod.Get, LemmyBaseUri + "/c/interop");
            request.Headers.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/activity+json"));
            var response = http.SendAsync(request).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                _lemmyKeyError = new InvalidOperationException(
                    $"Lemmy container returned {(int)response.StatusCode} for /c/interop");
                return null;
            }

            var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("publicKey", out var publicKey)
                || !publicKey.TryGetProperty("publicKeyPem", out var pemElement)
                || pemElement.GetString() is not { Length: > 0 } pem)
            {
                _lemmyKeyError = new InvalidOperationException("Lemmy community document has no publicKeyPem");
                return null;
            }

            _lemmyKey = KeyPair.FromPem(pem, KeyAlgorithm.Rsa, new Iri(LemmyCommunityKeyId));
        }
        catch (Exception ex)
        {
            _lemmyKeyError = ex;
            return null;
        }

        return _lemmyKey;
    }

    /// <summary>
    /// Fails the test when the Lemmy container is not reachable (surfaces the missing dependency
    /// explicitly on CI boxes without Docker).
    /// </summary>
    private static KeyPair RequireLemmy()
    {
        var key = GetLemmyKey();
        if (key is null)
        {
            Assert.Fail($"Lemmy container not reachable: {_lemmyKeyError?.Message ?? "unknown error"}");
        }

        return key;
    }

    private static (DeliveryWorker Worker, InMemoryDeliveryQueue Queue, InMemoryDeliveryDeadLetterStore DeadLetter)
        BuildWorker(
            DeliveryRetryOptions retryOptions,
            Func<HttpMessageHandler> transportFactory)
    {
        var irisKey = KeyPairGenerator.GenerateRsa(new Iri(IrisActorKeyId));
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(irisKey);

        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(new Iri(IrisActorIri), new Iri(IrisActorKeyId));

        var signer = new Iris.Core.Signing.HttpSignatureSigner(keyStore);
        var clientFactory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var queue = new InMemoryDeliveryQueue();
        var deadLetter = new InMemoryDeliveryDeadLetterStore();

        var worker = new DeliveryWorker(
            queue,
            clientFactory,
            transportFactory,
            Options.Create(new ActivityPubServerOptions
            {
                InstanceActorId = new Iri(IrisActorIri),
            }),
            NullLogger<DeliveryWorker>.Instance,
            retryOptions,
            deadLetter,
            1,
            null,
            null,
            null);

        return (worker, queue, deadLetter);
    }

    private static Create BuildProbeActivity(string noteId, string content)
        => new()
        {
            Id = $"{noteId}#create",
            Actor = [new Link { Href = new Uri(IrisActorIri) }],
            Object =
            [
                new Note
                {
                    Id = noteId,
                    Content = [content],
                },
            ],
        };

    // --- Happy path: delivery to the real Lemmy shared inbox completes (no transport error) ---

    [Fact(Skip = "The pseudo-production Lemmy/Mastodon servers (localhost:8091 / *.luit.ink) are no longer live.")]
    public async Task Delivery_ToLemmySharedInbox_Completes_NoTransportError()
    {
        RequireLemmy();

        var (worker, queue, deadLetter) = BuildWorker(
            new DeliveryRetryOptions { MaxAttempts = 3, BaseDelay = TimeSpan.FromMilliseconds(100) },
            () => new HttpClientHandler());

        var job = new DeliveryJob(
            new Iri(LemmySharedInboxUri),
            BuildProbeActivity("https://iris.test/notes/1388-happy", "138.8 failure-mode drill: happy-path probe"),
            new Iri(IrisActorIri));
        await queue.EnqueueAsync(job);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.StartAsync(cts.Token);

        // Wait for the delivery to complete (either delivered or dead-lettered). The Lemmy shared
        // inbox will likely reject the probe (unknown actor), so the job may be dead-lettered with
        // NonSuccessStatus after 3 attempts (100ms + 200ms backoff ≈ 300ms total). Wait up to 15s.
        await TestFederation.WaitForAsync(
            async () => (await deadLetter.ListAsync()).Count > 0 || queue.Count == 0,
            TimeSpan.FromSeconds(15));

        // Stop the worker now that the delivery has completed (the queue is empty or dead-lettered).
        await queue.CompleteAsync();
        await worker.StopAsync(cts.Token);

        // The delivery reached Lemmy (the HTTP round trip completed). If Lemmy rejected the activity
        // (likely — it's a malformed Create from an unknown actor), the job is dead-lettered with
        // NonSuccessStatus. The key assertion: it is NOT a TransportError (which would mean the wire
        // path is broken).
        var entries = await deadLetter.ListAsync();
        foreach (var entry in entries)
        {
            Assert.NotEqual(DeadLetterFailureKind.TransportError, entry.FailureKind);
        }
    }

    // --- Failure mode: unreachable peer → dead-letter with TransportError after retry budget ---

    [Fact(Skip = "The pseudo-production Lemmy/Mastodon servers (localhost:8091 / *.luit.ink) are no longer live.")]
    public async Task Delivery_ToUnreachableInbox_DeadLettersWithTransportError()
    {
        // Gated on the Lemmy container for consistency with the rest of the class, but the test
        // itself targets a deliberately unreachable port (nothing listens on 9999).
        RequireLemmy();

        const int MaxAttempts = 3;
        var (worker, queue, deadLetter) = BuildWorker(
            new DeliveryRetryOptions { MaxAttempts = MaxAttempts, BaseDelay = TimeSpan.FromMilliseconds(50) },
            () => new HttpClientHandler());

        var job = new DeliveryJob(
            new Iri(UnreachableInboxUri),
            BuildProbeActivity("https://iris.test/notes/1388-fail", "138.8 failure-mode drill: unreachable peer probe"),
            new Iri(IrisActorIri));
        await queue.EnqueueAsync(job);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.StartAsync(cts.Token);

        // Wait for the dead-letter entry. With 3 attempts and 50ms base delay (50ms + 100ms backoff),
        // the full retry cycle takes ≈ 150ms. Allow 10s for headroom.
        await TestFederation.WaitForAsync(
            () => Task.FromResult(deadLetter.Count == 1),
            TimeSpan.FromSeconds(10));

        // Stop the worker now that the delivery has completed and been dead-lettered.
        await queue.CompleteAsync();
        await worker.StopAsync(cts.Token);

        // The job must have been dead-lettered with TransportError after exactly MaxAttempts attempts.
        Assert.Equal(1, deadLetter.Count);
        var entries = await deadLetter.ListAsync();
        var entry = Assert.Single(entries);
        Assert.Equal(DeadLetterFailureKind.TransportError, entry.FailureKind);
        Assert.Equal(MaxAttempts, entry.Attempts);
        Assert.Equal(new Iri(UnreachableInboxUri), entry.InboxIri);
        Assert.NotNull(entry.FailureDetail);
    }
}
