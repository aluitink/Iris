using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Iris.Client;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Server;
using Iris.Server.Delivery;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 19.6 slice 19.6.4 — the signature identity of the server's outbound requests: the key IRI in the
/// <c>Signature</c> header matches the <em>acting</em> actor's <c>publicKey</c> id, resolvable from the
/// actor document. Both outbound paths Iris signs with a per-actor key are covered:
/// </summary>
/// <list type="bullet">
/// <item><em>Outbound delivery</em> (decision 029): the <see cref="DeliveryWorker"/> signs as the acting
/// actor carried in the <c>X-Iris-Actor</c> override — <strong>not</strong> the instance actor the shared
/// client is created as. When the acting actor is distinct from the instance actor, the <c>Signature</c>
/// <c>keyid</c> is the acting actor's key IRI (<c>{actingActor}#key-1</c>), not the instance actor's.</item>
/// <item><em>Proxy re-sign</em> (decision 037): the gated proxy endpoint re-signs the browser's request as
/// the authenticated (acting) actor. When that actor is distinct from the instance actor, the re-signed
/// request's <c>keyid</c> is the acting actor's key IRI, not the instance actor's.</item>
/// </list>
/// <remarks>
/// This pins the invariant that the acting actor's own key (the one served in its actor document's
/// <c>publicKey</c> extension) is what signs — a peer resolving the <c>keyid</c> from the actor document
/// verifies the request. No production change: the behavior is already implemented; these tests capture the
/// real signed outbound <c>Signature</c> header and assert the identity. The raw-inspector (Blazor) half of
/// the roadmap item — reading the rendered <c>keyid</c> from the inspector UI — requires the two-instance
/// Docker env and is a live-verification item.
/// </remarks>
public sealed class OutboundSignatureIdentityIntegrationTests : IDisposable
{
    private const string SigHost = "sig.example";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Carol = "carol";
    private const string Password = "s3cret!";

    private static readonly Iri AliceIri = new($"https://{SigHost}/ap/v1/u/{Alice}");
    private static readonly Iri BobIri = new($"https://{SigHost}/ap/v1/u/{Bob}");
    private static readonly Iri CarolIri = new($"https://{SigHost}/ap/v1/u/{Carol}");

    private readonly InMemoryKeyStore _keyStore;
    private readonly CapturingHandler _capture;
    private readonly IHost _host;

    public OutboundSignatureIdentityIntegrationTests()
    {
        // Two DISTINCT local actors on the same instance: alice is the instance actor (the host's
        // InstanceActorId, the actor the shared delivery client is created as); bob is a second local
        // actor that can act on its own (e.g. a relay/announce it authored). Both keys live in the store
        // and are registered with the provider at the #key-1 convention (their publicKey.id).
        var aliceKey = KeyPairGenerator.GenerateRsa(new Iri($"{AliceIri}#key-1"));
        var bobKey = KeyPairGenerator.GenerateRsa(new Iri($"{BobIri}#key-1"));

        _keyStore = new InMemoryKeyStore();
        _keyStore.PutKey(aliceKey);
        _keyStore.PutKey(bobKey);

        var keyProvider = new InMemoryKeyProvider(_keyStore);
        keyProvider.RegisterKey(AliceIri, aliceKey.KeyId);
        keyProvider.RegisterKey(BobIri, bobKey.KeyId);

        var signer = new HttpSignatureSigner(_keyStore);
        var factory = new ActivityPubClientFactory(_keyStore, keyProvider, signer);

        _capture = new CapturingHandler();
        var options = Options.Create(new ActivityPubServerOptions { InstanceActorId = AliceIri });
        var queue = new InMemoryDeliveryQueue();
        ILoggerFactory loggerFactory = NullLoggerFactory.Instance;
        var worker = new DeliveryWorker(
            queue, factory, () => _capture, options, loggerFactory.CreateLogger<DeliveryWorker>());

        // The DeliveryWorker is a BackgroundService; expose its queue for enqueueing by capturing the
        // queue reference (the worker pumps it over the capturing transport).
        _workerQueue = queue;

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(s => s.AddHostedService<DeliveryWorker>(_ => worker))
            .Build();
        _host.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private readonly InMemoryDeliveryQueue _workerQueue;

    public void Dispose()
    {
        _host.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _host.Dispose();
        _keyStore.Dispose();
    }

    // --- Outbound delivery (decision 029): keyid == the acting actor's key, not the instance's -------
    //
    // The DeliveryWorker's shared client is created as the instance actor (alice), but a DeliveryJob that
    // carries a distinct acting actor (bob) sets the X-Iris-Actor override, and the SigningHandler resolves
    // the signing identity from that override — so the request is signed with bob's key, not alice's.
    // Capturing the signed request and asserting its keyid is bob's #key-1 (and not alice's #key-1) proves
    // the acting actor's own key signs.

    [Fact]
    public async Task OutboundDelivery_SignsAsActingActor_NotInstanceActor()
    {
        var activity = BuildCreate();
        await _workerQueue.EnqueueAsync(
            new DeliveryJob(BobIri.InboxOf(), activity, BobIri), CancellationToken.None);

        await WaitForAsync(() => _capture.LastRequest is not null, timeout: TimeSpan.FromSeconds(5));
        var request = _capture.LastRequest;
        Assert.NotNull(request);

        // The X-Iris-Actor override carries the acting actor (bob), distinct from the instance actor (alice).
        var actorHeader = Assert.Single(request!.Headers.GetValues("X-Iris-Actor"));
        Assert.Equal(BobIri.Value, actorHeader);

        // The Signature header's keyid is the acting actor's key IRI (bob's #key-1) — the key served in
        // bob's actor document's publicKey extension — NOT the instance actor's (alice's #key-1).
        var signatureHeader = Assert.Single(request.Headers.GetValues(Signatures.SignatureHeaderName));
        Assert.True(SignatureHeader.TryParse(signatureHeader, out var parsed), "the Signature header must parse");
        Assert.NotNull(parsed);
        Assert.Equal($"{BobIri}#key-1", parsed!.KeyId);
        Assert.True(
            parsed.KeyId != $"{AliceIri}#key-1",
            "the keyid must be the acting actor's key, not the instance actor's");
    }

    // --- Proxy re-sign (decision 037): the re-signed keyid == the acting actor's key, not the instance's -
    //
    // A (alice = instance actor) hosts a second local actor (carol). A browser authenticated as carol posts
    // a proxied GET; the endpoint re-signs it as carol via the X-Iris-Actor override. Capturing the re-signed
    // request and asserting its keyid is carol's #key-1 (and not alice's #key-1) proves the proxy re-signs as
    // the acting (authenticated) actor, distinct from the instance actor.

    [Fact]
    public async Task Proxy_ResignsAsActingActor_NotInstanceActor()
    {
        // A's persistence seeds alice (the instance actor) and carol (a second local actor). The host
        // factory registers both with the IKeyProvider at #key-1 (the publicKey.id convention).
        var aPersistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPersonWithKey(aPersistence, SigHost, Alice);
        TestSeeder.SeedPersonWithKey(aPersistence, SigHost, Carol);

        // B is the target (serves bob's actor doc; the proxied GET reads it).
        var bPersistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPersonWithKey(bPersistence, SigHost, Bob);
        var bobTarget = new Iri($"https://{SigHost}/ap/v1/u/{Bob}");

        // A's outbound transport captures the re-signed request and returns 200 (so the proxy relays it).
        var proxyCapture = new CapturingHandler(HttpStatusCode.OK);

        var a = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = SigHost,
            Handle = Alice,
            Persistence = aPersistence,
            ExtraLocalActors = [CarolIri],
            // Authenticate only carol (the acting actor), distinct from the instance actor (alice).
            CredentialValidator = new BasicAuthCredentialValidator((_, username, password) =>
            {
                var valid = username == Carol &&
                    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(Password));
                return new ValueTask<bool>(valid);
            }),
            DeliveryTransport = () => proxyCapture,
        });
        using var b = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = SigHost,
            Handle = Bob,
            Persistence = bPersistence,
            // B performs no outbound delivery in this test; skip its key registration.
            RegisterLocalKey = false,
        });
        using var scope = new DisposeBoth(a, b);

        // The browser (authenticated as carol) posts the proxied GET to bob's actor doc.
        var http = a.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/ap/v1/proxy/{bobTarget.Value}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/activity+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Carol}:{Password}")));

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The re-signed proxied GET A emitted: its X-Iris-Actor override is carol (the acting actor), and
        // its Signature keyid is carol's #key-1 (carol's publicKey.id) — NOT alice's #key-1 (the instance).
        var captured = proxyCapture.LastRequest;
        Assert.NotNull(captured);
        var actorHeader = Assert.Single(captured!.Headers.GetValues("X-Iris-Actor"));
        Assert.Equal(CarolIri.Value, actorHeader);

        var signatureHeader = Assert.Single(captured.Headers.GetValues(Signatures.SignatureHeaderName));
        Assert.True(SignatureHeader.TryParse(signatureHeader, out var parsed), "the Signature header must parse");
        Assert.NotNull(parsed);
        Assert.Equal($"{CarolIri}#key-1", parsed!.KeyId);
        Assert.True(
            parsed.KeyId != $"{AliceIri}#key-1",
            "the keyid must be the acting (authenticated) actor's key, not the instance actor's");
    }

    // --- Helpers --------------------------------------------------------------------

    private static Create BuildCreate()
    {
        var noteIri = $"https://{SigHost}/objects/note-{Guid.NewGuid():N}";
        return new Create
        {
            Id = $"https://{SigHost}/activities/create-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(BobIri.Value) }],
            Object =
            [
                new Note { Id = noteIri, Content = ["signed by the acting actor"] },
            ],
        };
    }

    private static async Task WaitForAsync(Func<bool> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe())
            {
                return;
            }

            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Records the last outbound request (after the <see cref="Iris.Client.Pipeline.SigningHandler"/> has
    /// signed it) and returns a canned success so the caller treats the send as complete.
    /// </summary>
    private sealed class CapturingHandler(HttpStatusCode successStatus = HttpStatusCode.Accepted) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private HttpRequestMessage? _last;

        public HttpRequestMessage? LastRequest
        {
            get
            {
                lock (_gate)
                {
                    return _last is { } r ? Clone(r) : null;
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _last = Clone(request);
            }

            return Task.FromResult(new HttpResponseMessage(successStatus)
            {
                Content = new StringContent("ok", Encoding.UTF8, "application/activity+json"),
            });
        }

        private static HttpRequestMessage Clone(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
            };
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is { } content)
            {
                var body = content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                clone.Content = new ByteArrayContent(body);
                foreach (var header in content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }
    }

    /// <summary>Disposes two <see cref="TestServer"/> instances.</summary>
    private sealed class DisposeBoth(TestServer one, TestServer two) : IDisposable
    {
        public void Dispose()
        {
            one.Dispose();
            two.Dispose();
        }
    }
}
