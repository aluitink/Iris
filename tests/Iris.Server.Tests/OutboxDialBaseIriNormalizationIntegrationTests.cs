using System.Net;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 19.6 integration test: <em>Docker-only-routable IRI normalization</em> on the outbox publish
/// write path. The authoring client dials the instance on a host-published base (e.g.
/// <c>http://localhost:8081</c>) and carries that base in the activity's object reference (a
/// <see cref="Follow"/> target), but the instance stores its local actors under the <em>advertised</em>
/// base (e.g. <c>https://iris-dev1.luit.ink</c>). Without normalization the local-actor check (an
/// exact-IRI store lookup) misses the actor — the instance treats its own actor as remote and attempts a
/// cross-instance delivery that cannot route (the dial base is unreachable from inside the instance's
/// network), surfacing as a 500. The outbox handler rewrites the object to the advertised base when the
/// target is a local actor/community reached via a different base, so the edge is recorded under the
/// canonical IRI and no cross-instance hop is attempted.
/// </summary>
public sealed class OutboxDialBaseIriNormalizationIntegrationTests : IDisposable
{
    // The advertised base (the actor IRIs the instance stores + serves documents under).
    private const string AdvertiseHost = "iris-dev1.luit.ink";
    // The dial base the client actually dials (a host-published port, NOT the advertised host). The
    // client carries this base in the activity's object reference.
    private const string DialBase = "http://localhost:8081";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobAdvertisedIri;

    public OutboxDialBaseIriNormalizationIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        var aliceSeeded = TestSeeder.SeedPersonWithKey(_persistence, AdvertiseHost, Alice);
        _aliceKey = aliceSeeded.Key;
        _aliceActorIri = aliceSeeded.ActorIri;

        var bobSeeded = TestSeeder.SeedPersonWithKey(_persistence, AdvertiseHost, Bob);
        _bobAdvertisedIri = bobSeeded.ActorIri;

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AdvertiseHost,
            Handle = Alice,
            Persistence = _persistence,
            IdentityKeys = BuildIdentity(aliceSeeded.Key, aliceSeeded.ActorIri),
            // bob is a second local actor: register his key so the host can sign as him if it ever
            // (incorrectly) attempted an outbound delivery as bob. The point of the test is that NO
            // outbound delivery is attempted at all (bob is local, reached via the dial base).
            ExtraLocalActors = [bobSeeded.ActorIri],
            // The delivery transport is a throwing handler: if the server ever attempts a cross-instance
            // delivery (treating bob as remote), the test fails fast with a clear signal instead of
            // hanging on a real network call.
            DeliveryTransport = () => new ThrowingHandler(),
            // The inbound key resolver fetches the signing actor's document (to read its publicKey) via
            // the IActorDocumentFetcher; route that fetch to this instance's own TestServer so the local
            // actor's document (carrying the seeded publicKeyPem) resolves without a real network call.
            Fetcher = BuildFetcherFor(AdvertiseHost, Alice, aliceSeeded.Key, new LazyHandler(() => _server!.CreateHandler())),
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- A Follow whose target carries the dial base (not the advertised base) is recorded under the
    //     advertised IRI, with NO cross-instance delivery (the target is local) ------------------------

    [Fact]
    public async Task OutboxPublish_Follow_LocalTargetViaDialBase_RewritesToAdvertisedBase_NoDelivery()
    {
        // The client authors a Follow whose object is bob reached via the DIAL base (what the browser
        // dials), not the advertised base. This is exactly the Docker-only-routable mismatch the fix
        // addresses.
        var follow = new Follow
        {
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [new Link { Href = new Uri($"{DialBase}/ap/v1/u/{Bob}") }],
        };

        using var request = SignedRequest(_aliceActorIri, _aliceKey, follow, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _http.SendAsync(request);

        // The write succeeds (202) — before the fix this 500'd (the instance treated its own actor as
        // remote and the cross-instance delivery to the dial base was refused).
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The follow edge is recorded under the ADVERTISED IRI (the canonical form), not the dial-base
        // IRI — proof the normalization rewrote the object before recording the edge.
        Assert.True(
            await _persistence.Follows.IsFollowingAsync(_aliceActorIri, _bobAdvertisedIri),
            "the follow edge must be recorded under the advertised IRI (alice → bob)");

        // And it is NOT recorded under the dial-base IRI (the edge is canonical, not duplicated).
        Assert.False(
            await _persistence.Follows.IsFollowingAsync(_aliceActorIri, new Iri($"{DialBase}/ap/v1/u/{Bob}")),
            "the follow edge must not be recorded under the dial-base IRI (the object is rewritten)");

        // bob's inbox (on this single instance) received nothing: the target is local, so no
        // cross-instance delivery was attempted (the ThrowingHandler would have failed the write if one
        // had been).
        Assert.Empty(await _persistence.Activities.GetInboxAsync(_bobAdvertisedIri));
    }

    // --- A Follow whose target is already on the advertised base is left untouched (no rewrite) -----

    [Fact]
    public async Task OutboxPublish_Follow_LocalTargetViaAdvertisedBase_Untouched()
    {
        // The canonical case: the object already carries the advertised base. The normalization is a
        // no-op (it only rewrites when the base differs), and the edge is recorded as-is.
        var follow = new Follow
        {
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [new Link { Href = new Uri(_bobAdvertisedIri.Value) }],
        };

        using var request = SignedRequest(_aliceActorIri, _aliceKey, follow, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(
            await _persistence.Follows.IsFollowingAsync(_aliceActorIri, _bobAdvertisedIri),
            "the follow edge must be recorded (alice → bob, advertised base)");
    }

    // --- A Follow of a genuinely REMOTE actor (a different host, not local) is NOT rewritten ---------

    [Fact]
    public async Task OutboxPublish_Follow_RemoteTarget_NotRewritten()
    {
        // A follow of an actor on a genuinely foreign host (not this instance) must NOT be rewritten to
        // the advertised base — the normalization only applies to local actors/communities. The target
        // stays on its foreign host (the instance will attempt a real cross-instance delivery, which the
        // ThrowingHandler blocks; the write itself still records the local outbox entry + the follow
        // edge for the remote target and returns 202).
        const string remoteHost = "remote.example";
        var remoteActorIri = new Iri($"https://{remoteHost}/ap/v1/u/carol");

        var follow = new Follow
        {
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [new Link { Href = new Uri(remoteActorIri.Value) }],
        };

        using var request = SignedRequest(_aliceActorIri, _aliceKey, follow, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _http.SendAsync(request);

        // The write is accepted (the local record + outbox entry succeed; the cross-instance delivery is
        // best-effort and its failure does not fail the write).
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The follow edge is recorded under the REMOTE actor's IRI (untouched — not rewritten to the
        // advertised base).
        Assert.True(
            await _persistence.Follows.IsFollowingAsync(_aliceActorIri, remoteActorIri),
            "the follow edge must be recorded under the remote actor's IRI (not rewritten)");

        // It is NOT recorded under the advertised base (the remote target is not local).
        Assert.False(
            await _persistence.Follows.IsFollowingAsync(_aliceActorIri, new Iri($"https://{AdvertiseHost}/ap/v1/u/carol")),
            "the follow edge must not be rewritten to the advertised base for a remote target");
    }

    // --- A Follow of a REMOTE actor that shares a handle with a LOCAL actor is NOT rewritten --------

    [Fact]
    public async Task OutboxPublish_Follow_RemoteTarget_SharedHandle_NotRewritten()
    {
        // Regression: a follow of alice on a DIFFERENT instance (iris-dev2) must not be rewritten to
        // alice on THIS instance (iris-dev1) just because the handle matches. The target's host is a
        // genuinely foreign public hostname, not a dial base for this instance.
        const string remoteHost = "iris-dev2.luit.ink";
        var remoteAliceIri = new Iri($"https://{remoteHost}/ap/v1/u/{Alice}");

        var follow = new Follow
        {
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [new Link { Href = new Uri(remoteAliceIri.Value) }],
        };

        using var request = SignedRequest(_aliceActorIri, _aliceKey, follow, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The follow edge is recorded under the REMOTE alice's IRI (iris-dev2), NOT the local alice's
        // IRI (iris-dev1).
        Assert.True(
            await _persistence.Follows.IsFollowingAsync(_aliceActorIri, remoteAliceIri),
            "the follow edge must be recorded under the remote alice's IRI (iris-dev2)");
        Assert.False(
            await _persistence.Follows.IsFollowingAsync(_aliceActorIri, _aliceActorIri),
            "the follow edge must NOT be rewritten to the local alice's IRI (iris-dev1)");
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private static IdentityKeys BuildIdentity(KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        return new IdentityKeys(keyStore, keyProvider, signer);
    }

    private HttpRequestMessage SignedRequest(Iri actorIri, KeyPair key, Activity activity, string path)
    {
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();
        using (var client = BuildClient(actorIri, key, capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            var response = client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"https://{AdvertiseHost}{path}")
                    {
                        Content = signedContent,
                    },
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            response.Dispose();
        }

        var captured = capture.Captured!;
        var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{AdvertiseHost}{path}")
        {
            Content = content,
        };
        foreach (var (name, values) in captured.Headers)
        {
            if (string.Equals(name, "content-type", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "date", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in values)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (captured.Headers.TryGetValue("date", out var dateValues))
        {
            foreach (var value in dateValues)
            {
                request.Headers.TryAddWithoutValidation("date", value);
            }
        }

        return request;
    }

    private static IActivityPubClient BuildClient(Iri actorIri, KeyPair key, HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            handler);
    }

    /// <summary>
    /// A delivery transport that throws on any use: if the server ever attempts a cross-instance
    /// delivery (treating a local actor as remote), the test fails fast with a clear signal instead of
    /// hanging on a real network call.
    /// </summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                $"Unexpected cross-instance delivery to {request.RequestUri} (the target was local; " +
                "no delivery should have been attempted).");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public CapturedRequest? Captured { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers)
            {
                headers[name] = values.ToList();
            }

            if (request.Content is not null)
            {
                foreach (var (name, values) in request.Content.Headers)
                {
                    if (headers.TryGetValue(name, out var existing))
                    {
                        existing.AddRange(values);
                    }
                    else
                    {
                        headers[name] = values.ToList();
                    }
                }
            }

            Captured = new CapturedRequest(body, headers);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            };
            return Task.FromResult(response);
        }
    }

    private sealed record CapturedRequest(byte[] Body, Dictionary<string, List<string>> Headers);

    /// <summary>
    /// Builds an <see cref="IActorDocumentFetcher"/> that routes actor-document fetches to this
    /// instance's own <see cref="TestServer"/> (the local actor's document, served from the store,
    /// carries the seeded <c>publicKeyPem</c> the inbound key resolver reads to validate the signature).
    /// </summary>
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
}
