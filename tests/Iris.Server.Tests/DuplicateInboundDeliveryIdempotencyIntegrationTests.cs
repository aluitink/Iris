using System.Net;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 24.3 integration test: idempotent handling of <em>duplicate inbound</em> deliveries. A real
/// federation peer may redeliver the same signed activity (on a delivery timeout, a retry, or a network
/// retransmission), so the receiving instance must treat a repeat as a no-op: the recorded edge appears
/// <strong>exactly once</strong>, the activity is stored exactly once, the handler is not re-run (no
/// duplicate side effect, e.g. a second <see cref="Accept"/>), and nothing errors.
/// </summary>
/// <remarks>
/// The mechanism under test is the <see cref="Iris.Server.Inbox.IInboxProcessor"/> idempotency guard
/// (C-07): it stores an inbound activity add-if-absent by its IRI, and — when the activity was already
/// stored (a re-delivery) — does NOT re-dispatch it to a handler. This slice locks that guard for an
/// <em>edge-recording</em> activity (a <see cref="Block"/> / a <see cref="Follow"/>), where the durable
/// effect is a store edge and a handler side effect (not merely an outbox entry).
/// <para>
/// Two complementary facts:
/// <list type="bullet">
/// <item>The <see cref="Block"/> redelivered to the blocked actor's inbox records the block edge exactly
/// once (the moderation store is idempotent, but the guard is what prevents the handler from re-running
/// on the repeat), and the second delivery is accepted as a no-op (202), not an error (500).</item>
/// <item>The <see cref="Follow"/> redelivered to the followed actor's inbox records the follow edge
/// exactly once <em>and</em> the followed instance emits exactly one <see cref="Accept"/> to the
/// follower — the <see cref="Iris.Server.Inbox.FollowActivityHandler"/> mints a fresh <see cref="Accept"/>
/// id on every dispatch, so without the guard the repeat would mint a second, distinct <see cref="Accept"/>
/// that lands in the follower's inbox. The "exactly one Accept" assertion is the non-vacuous proof that
/// the handler runs exactly once (the idempotent store edge alone would stay one even if the handler ran
/// twice, so it cannot by itself prove the guard is load-bearing).</item>
/// </list>
/// This complements <see cref="MutualFollowDeliveryLoopIntegrationTests"/> (which pins the same guard for
/// a <see cref="Create"/> — proving a re-delivery is not re-fan-out).
/// </para>
/// Topology: instance A (dup-a.domain.local, <c>alice</c>, the blocker/follower) and instance B
/// (dup-b.domain.local, <c>bob</c>, the blocked/followed target). The same signed activity is delivered
/// twice to bob's inbox on B (simulating a peer redelivery); B must record the edge exactly once. The
/// follow case routes B's outbound delivery to A's inbox (so B's emitted Accept lands on A) so the
/// "exactly one Accept" assertion is observable.
/// </remarks>
public sealed class DuplicateInboundDeliveryIdempotencyIntegrationTests : IDisposable
{
    private const string AHost = "dup-a.domain.local";
    private const string BHost = "dup-b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;

    public DuplicateInboundDeliveryIdempotencyIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobActorIri = bSeeded.ActorIri;

        _a = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _aPersistence,
            IdentityKeys = BuildIdentity(aSeeded.Key, aSeeded.ActorIri),
            // A must be able to fetch B's actor document (where bob's public key lives) to validate the
            // signature of B's emitted Accept (the cross-instance fetcher routes by host).
            Fetcher = new RoutingFetcher(
                AHost, new LazyHandler(() => _a!.CreateHandler()),
                BHost, new LazyHandler(() => _b!.CreateHandler()),
                aSeeded.Key),
        });
        _b = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _bPersistence,
            IdentityKeys = BuildIdentity(bSeeded.Key, bSeeded.ActorIri),
            // B's fetcher routes to A's own actor document (where alice's public key lives) so B can
            // validate the signature of the redelivered activity (signed as alice).
            Fetcher = BuildFetcherFor(AHost, Alice, aSeeded.Key, new LazyHandler(() => _a!.CreateHandler())),
            // B's outbound delivery (the Accept it emits in response to the Follow) routes to A's inbox,
            // so B's Accept lands in A's inbox and the "exactly one Accept" assertion is observable.
            DeliveryTransport = () => new LazyHandler(() => _a!.CreateHandler()),
        });
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --- A redelivered Block is recorded exactly once (no error, no duplicate edge) ------------

    [Fact]
    public async Task RedeliveredBlock_IsRecordedExactlyOnce_NoError()
    {
        // A redelivered inbound activity carries the originator's id verbatim (inbound federation keeps
        // the originator's id — decision 055); the inbox requires it, so build the Block with an id.
        var blockIri = $"https://{AHost}/activities/dupblock-{Guid.NewGuid():N}";
        var block = new Block
        {
            Id = blockIri,
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [new Link { Href = new Uri(_bobActorIri.Value) }],
        };

        // Deliver the SAME Block to bob's inbox on B twice (simulating a peer redelivery on
        // timeout/retry). B's inbox-Id dedup guard (C-07) stores it once and skips the handler on the
        // second delivery; the block edge must end up recorded exactly once, and the second delivery
        // must be accepted (no error).
        var inbox = InboxOf(_bobActorIri);
        var first = await DeliverDirectly(_aliceActorIri, _aliceKey, inbox, block, target: () => _b!);
        var second = await DeliverDirectly(_aliceActorIri, _aliceKey, inbox, block, target: () => _b!);
        await Task.Delay(TimeSpan.FromSeconds(4));

        // Both deliveries were accepted (a re-delivery is a no-op, not an error).
        Assert.True(first, "the first Block delivery should have been accepted (202).");
        Assert.True(second, "a redelivered Block should be accepted as a no-op (202), not error (500).");

        // B stored the Block exactly once (the activity store is keyed by IRI; a re-delivery is a no-op).
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(blockIri), out _),
            "B should have stored the Block.");

        // THE IDEMPOTENCY ASSERTION: the alice → bob block edge is recorded exactly once — bob has exactly
        // one blocker (alice).
        var blockers = await _bPersistence.Moderation.GetBlockersAsync(_bobActorIri);
        var aliceBlockerCount = blockers.Count(b => b == _aliceActorIri);
        Assert.True(
            aliceBlockerCount == 1,
            $"bob should have exactly one blocker (alice); a redelivered Block must record the edge " +
            $"exactly once (got {aliceBlockerCount} occurrences of alice in bob's blockers).");

        Assert.True(
            await _bPersistence.Moderation.IsBlockedAsync(_aliceActorIri, _bobActorIri),
            "B should have the alice → bob block edge after the (redelivered) Block.");
    }

    // --- A redelivered Follow is recorded exactly once and emits exactly one Accept -----------

    [Fact]
    public async Task RedeliveredFollow_IsRecordedExactlyOnce_ExactlyOneAccept()
    {
        var followIri = $"https://{AHost}/activities/dupfollow-{Guid.NewGuid():N}";
        var follow = new Follow
        {
            Id = followIri,
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [new Link { Href = new Uri(_bobActorIri.Value) }],
        };

        // Deliver the SAME Follow to bob's inbox on B twice (simulating a peer redelivery). The follow
        // edge must end up recorded exactly once, and B must emit exactly one Accept to alice.
        var inbox = InboxOf(_bobActorIri);
        var first = await DeliverDirectly(_aliceActorIri, _aliceKey, inbox, follow, target: () => _b!);
        var second = await DeliverDirectly(_aliceActorIri, _aliceKey, inbox, follow, target: () => _b!);
        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.True(first, "the first Follow delivery should have been accepted (202).");
        Assert.True(second, "a redelivered Follow should be accepted as a no-op (202), not error (500).");

        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(followIri), out _),
            "B should have stored the Follow.");

        // THE IDEMPOTENCY ASSERTION (edge): bob has exactly one follower (alice).
        var followers = await _bPersistence.Follows.GetFollowersAsync(_bobActorIri);
        var aliceFollowerCount = followers.Count(f => f == _aliceActorIri);
        Assert.True(
            aliceFollowerCount == 1,
            $"bob should have exactly one follower (alice); a redelivered Follow must record the edge " +
            $"exactly once (got {aliceFollowerCount} occurrences of alice in bob's followers).");

        Assert.True(
            await _bPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            "B should have the alice → bob follow edge after the (redelivered) Follow.");

        // THE NON-VACUOUS ASSERTION (handler ran exactly once): B emitted exactly one Accept to alice.
        // The FollowActivityHandler mints a fresh Accept id on every dispatch, so a re-dispatch (guard
        // disabled) would mint a SECOND, distinct Accept that also lands in alice's inbox. The idempotent
        // store edge above would stay "one" even if the handler ran twice, so it cannot by itself prove
        // the guard is load-bearing — the "exactly one Accept" assertion is the proof.
        var aliceInbox = await _aPersistence.Activities.GetInboxAsync(_aliceActorIri);
        var acceptsForThisFollow = aliceInbox.Count(o => IsAcceptOfFollow(o, followIri));
        Assert.True(
            acceptsForThisFollow == 1,
            $"alice's inbox should contain exactly one Accept for this Follow (a redelivered Follow must " +
            $"not re-run the handler and emit a second Accept); got {acceptsForThisFollow}.");
    }

    // --- Helpers ---------------------------------------------------------------------------

    private static IdentityKeys BuildIdentity(KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        return new IdentityKeys(keyStore, keyProvider, signer);
    }

    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair key, HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            handler);
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    /// <summary>
    /// The inbox IRI for a person actor (the convention <c>{actorIri}/inbox</c>).
    /// </summary>
    private static Iri InboxOf(Iri actorIri) => new($"{actorIri.Value.TrimEnd('/')}/inbox");

    /// <summary>
    /// True when <paramref name="item"/> is an <see cref="Accept"/> whose object is the Follow with IRI
    /// <paramref name="followIri"/> (i.e. the Accept that the followed actor emits in response to that
    /// Follow). Used to count how many Accepts a (redelivered) Follow produced.
    /// </summary>
    private static bool IsAcceptOfFollow(IObjectOrLink item, string followIri)
    {
        if (item is not Accept accept)
        {
            return false;
        }

        var target = accept.Object?.FirstOrDefault()?.ResolveObjectIri();
        return target.HasValue && target.Value.Value == followIri;
    }

    /// <summary>
    /// Delivers <paramref name="activity"/> directly to <paramref name="inbox"/> (signed as
    /// <paramref name="actorIri"/>) through a hosted delivery worker routing to a capturing handler that
    /// forwards to <paramref name="target"/> and records the response status. Returns whether the delivery
    /// was accepted (2xx). Called twice with the same activity to simulate a duplicate delivery of an
    /// activity already stored on the target instance.
    /// </summary>
    private static async Task<bool> DeliverDirectly(
        Iri actorIri, KeyPair key, Iri inbox, Activity activity, Func<TestServer> target)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var loggerFactory = NullLoggerFactory.Instance;
        var capture = new StatusCapturingHandler(() => target().CreateHandler());

        var queue = new InMemoryDeliveryQueue();
        var service = new Iris.Server.Delivery.DeliveryService(
            queue, loggerFactory.CreateLogger<Iris.Server.Delivery.DeliveryService>());
        var worker = new Iris.Server.Delivery.DeliveryWorker(
            queue, factory,
            () => capture,
            Microsoft.Extensions.Options.Options.Create(
                new ActivityPubServerOptions { InstanceActorId = actorIri }),
            loggerFactory.CreateLogger<Iris.Server.Delivery.DeliveryWorker>());

        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(s => s.AddHostedService(_ => worker))
            .Build();

        await host.StartAsync(CancellationToken.None);
        try
        {
            await service.DeliverAsync(inbox, activity);
            // Let the (single) delivery settle before returning.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        return capture.LastStatus is { } status && (int)status >= 200 && (int)status < 300;
    }

    /// <summary>
    /// A lazy, status-recording <see cref="HttpMessageHandler"/> that forwards to a (deferred) inner
    /// handler factory and captures the most recent response status code, so a test can assert the inbox
    /// accepted (202) rather than errored (500) a redelivered activity.
    /// </summary>
    private sealed class StatusCapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpMessageHandler> _innerFactory;
        private HttpMessageHandler? _inner;
        private HttpClient? _client;

        public HttpStatusCode? LastStatus { get; private set; }

        public StatusCapturingHandler(Func<HttpMessageHandler> innerFactory)
        {
            _innerFactory = innerFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _client ??= new HttpClient(_inner ??= _innerFactory(), disposeHandler: false);
            var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is { } content)
            {
                clone.Content = new ByteArrayContent(
                    content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                foreach (var header in content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            var response = _client.SendAsync(clone, cancellationToken).GetAwaiter().GetResult();
            LastStatus = response.StatusCode;
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _client?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance's actor documents based
    /// on the actor IRI's host (so an instance can fetch the peer's actor document to validate the
    /// peer's signature).
    /// </summary>
    private sealed class RoutingFetcher : IActorDocumentFetcher
    {
        private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

        public RoutingFetcher(
            string aHost, HttpMessageHandler aHandler,
            string bHost, HttpMessageHandler bHandler,
            KeyPair signingKey)
        {
            _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
            {
                [aHost] = BuildFetcherFor(aHost, "local", signingKey, aHandler),
                [bHost] = BuildFetcherFor(bHost, "local", signingKey, bHandler),
            };
        }

        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        {
            var host = new Uri(actorIri.Value).Host;
            if (_fetchers.TryGetValue(host, out var fetcher))
            {
                return fetcher.GetActorAsync(actorIri, ct);
            }

            return Task.FromResult<Actor?>(null);
        }
    }

    /// <summary>
    /// A lazy <see cref="HttpMessageHandler"/> that defers to a (deferred) inner handler factory, so a
    /// server's transport/fetcher can reference a TestServer that is created after the option is wired.
    /// </summary>
    private sealed class LazyHandler : HttpMessageHandler
    {
        private readonly Func<HttpMessageHandler> _innerFactory;
        private HttpMessageHandler? _inner;
        private HttpClient? _client;

        public LazyHandler(Func<HttpMessageHandler> innerFactory)
        {
            _innerFactory = innerFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _client ??= new HttpClient(_inner ??= _innerFactory(), disposeHandler: false);
            var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is { } content)
            {
                clone.Content = new ByteArrayContent(
                    content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                foreach (var header in content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return _client.SendAsync(clone, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _client?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
