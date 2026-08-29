using System.Text;
using Iris.Client;
using Iris.Client.Extensions;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Client.Extensions.Tests;

/// <summary>
/// Phase 11 Slice 11.4 end-to-end test (gap J-9 — the client's "follow" API): a real
/// <see cref="TestServer"/> runs the Iris ActivityPub server. A local actor authenticates (Basic auth →
/// PEM private key), then — using the client's one-call <see cref="IActivityPubClient.FollowAsync"/> —
/// follows a second seeded actor. The request is signed through the full pipeline and accepted by the
/// server's inbox, which records the follow edge. This proves the handle→IRI step (Slice 11.3) and the
/// follow step are reachable through the client as a user would drive them.
/// </summary>
public sealed class FollowIntegrationTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string Follower = "alice";
    private const string Target = "bob";
    private const string Password = "correct-horse-battery";
    private const string FollowerIri = $"https://{Host}/ap/v1/u/{Follower}";
    private const string TargetIri = $"https://{Host}/ap/v1/u/{Target}";
    private const string FollowerKeyIri = $"{FollowerIri}#key-1";

    private readonly TestServer _server;
    private readonly InMemoryPersistenceProvider _persistence;

    public FollowIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        // The follower gets a real signing key (embedded as a JWK in the actor doc) so the server's
        // SignatureValidationMiddleware can verify the signed follow. The target is a plain person.
        var (followerKey, _, _) = TestSeeder.SeedPersonWithKey(_persistence, Host, Follower);
        TestSeeder.SeedPerson(_persistence, Host, Target);

        // The server's inbound key resolver must fetch the follower's actor doc to verify the follow's
        // signature. In a single-instance test that doc lives on THIS server, so the fetcher is wired to
        // reach the in-process TestServer. The TestServer is created by ActivityPubHostFactory.Create
        // (below), which is the very call that wires the fetcher — a chicken-and-egg. The LazyHandler
        // therefore captures a Func<TestServer> (deferred to first use) rather than a server reference,
        // because _server is still null while the object initializer that assigns it is running.
        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Follower,
            Persistence = _persistence,
            CredentialValidator = new BasicAuthCredentialValidator((_, username, password) =>
            {
                var valid = username == Follower &&
                    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(Password));
                return new ValueTask<bool>(valid);
            }),
            Fetcher = BuildSelfFetcher(followerKey, () => _server!),
        });
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Session_Login_ThenFollowAsync_RecordsFollowEdge()
    {
        // Authenticate as the follower (Basic auth → owner-only doc + PEM key).
        var authenticator = new BasicAuthClientAuthenticator(
            _server.CreateClient(), new Iri(FollowerIri), Follower, Password);

        var options = new IrisClientOptions
        {
            ServerBaseUri = new Uri($"https://{Host}"),
            UseProxyFallback = false,
            // The in-process TestServer transport does not clone the request between sends, so a
            // retried follow (RetryHandler) would re-send the same HttpRequestMessage and be
            // rejected. Real deployments use a socket transport (which clones internally); disable
            // retry here to keep the single-attempt follow on the in-process wire.
            EnableRetry = false,
        };
        using var bundle = IrisClientBuilder.Create(options)
            .WithAuthenticator(authenticator)
            .Build();

        var actor = await bundle.Session.LoginAsync(new Iri(FollowerIri));
        Assert.NotNull(actor);
        Assert.True(bundle.Session.KeyStore.TryGetKey(new Iri(FollowerKeyIri), out _),
            "the authenticated key should be in the session key store");

        // Build a signed client routed to the in-process server and follow the target. The client
        // derives the target's inbox from the target IRI and signs the Follow as the follower.
        using var client = bundle.CreateClient(new Iri(FollowerIri), _server.CreateHandler());
        var status = await client.FollowAsync(new Iri(FollowerIri), new Iri(TargetIri));

        Assert.Equal(202, status);

        // The server's inbox recorded the follow edge: the follower now follows the target.
        var following = await _persistence.Follows.IsFollowingAsync(new Iri(FollowerIri), new Iri(TargetIri));
        Assert.True(following, "the follow edge should be recorded after the signed Follow is accepted");
    }

    [Fact]
    public async Task Session_Login_ThenUndoFollow_RemovesFollowEdge()
    {
        // Authenticate as the follower (Basic auth → owner-only doc + PEM key).
        var authenticator = new BasicAuthClientAuthenticator(
            _server.CreateClient(), new Iri(FollowerIri), Follower, Password);

        var options = new IrisClientOptions
        {
            ServerBaseUri = new Uri($"https://{Host}"),
            UseProxyFallback = false,
            EnableRetry = false,
        };
        using var bundle = IrisClientBuilder.Create(options)
            .WithAuthenticator(authenticator)
            .Build();

        var actor = await bundle.Session.LoginAsync(new Iri(FollowerIri));
        Assert.NotNull(actor);

        using var client = bundle.CreateClient(new Iri(FollowerIri), _server.CreateHandler());

        // Step 1: follow the target. The signed Follow is delivered to the target's inbox; the server
        // records the follow edge and stores the Follow (deduping on its deterministic IRI).
        var followStatus = await client.FollowAsync(new Iri(FollowerIri), new Iri(TargetIri));
        Assert.Equal(202, followStatus);
        Assert.True(await _persistence.Follows.IsFollowingAsync(new Iri(FollowerIri), new Iri(TargetIri)),
            "the follow edge should be recorded after the signed Follow is accepted");

        // Step 2: un-follow. The Undo is delivered to the FOLLOWER's own inbox (the recipient of the
        // delivery is the follower, who made the follow). The Undo references the original Follow by its
        // deterministic IRI, which the server resolved the follow edge from.
        var followIri = new Iri($"{FollowerIri}/follows/{TargetIri}");
        var undo = new KristofferStrube.ActivityStreams.Undo
        {
            Id = $"{FollowerIri}/undoes/{followIri}",
            Actor = [new KristofferStrube.ActivityStreams.Link { Href = new Uri(FollowerIri) }],
            Object = [new KristofferStrube.ActivityStreams.Link { Href = followIri.Uri }],
        };
        var undoStatus = await client.DeliverAsync(new Iri(FollowerIri).InboxOf(), undo);
        Assert.Equal(202, undoStatus);

        // The server's UndoActivityHandler removed the follow edge.
        Assert.False(await _persistence.Follows.IsFollowingAsync(new Iri(FollowerIri), new Iri(TargetIri)),
            "the follow edge should be removed after the signed Undo is accepted");
    }

    [Fact]
    public async Task Session_Login_SelfFollow_ThenReject_RemovesFollowEdge()
    {
        // Slice 11.10 / J-10 end-to-end: the full Reject lifecycle through the client. The follower
        // (alice) follows her own actor (a local actor) — the server records the follow edge. Because a
        // self-follow is not a real remote request, there is no remote Accept to finalize it; instead the
        // operator (alice, the owner of the followed actor) responds with an explicit Reject, signed as
        // the followed actor. The server's RejectActivityHandler removes the follow edge.
        var authenticator = new BasicAuthClientAuthenticator(
            _server.CreateClient(), new Iri(FollowerIri), Follower, Password);

        var options = new IrisClientOptions
        {
            ServerBaseUri = new Uri($"https://{Host}"),
            UseProxyFallback = false,
            EnableRetry = false,
        };
        using var bundle = IrisClientBuilder.Create(options)
            .WithAuthenticator(authenticator)
            .Build();

        var actor = await bundle.Session.LoginAsync(new Iri(FollowerIri));
        Assert.NotNull(actor);

        using var client = bundle.CreateClient(new Iri(FollowerIri), _server.CreateHandler());

        // Step 1: alice follows herself (a local actor). The server records the follow edge.
        var followStatus = await client.FollowAsync(new Iri(FollowerIri), new Iri(FollowerIri));
        Assert.Equal(202, followStatus);
        Assert.True(await _persistence.Follows.IsFollowingAsync(new Iri(FollowerIri), new Iri(FollowerIri)),
            "the follow edge should be recorded after the signed self-Follow is accepted");

        // Step 2: the operator rejects the follow. The Reject's actor is the followed actor (alice — the
        // owner of the followed actor), so the client signs it as alice; the server resolves alice's
        // public key from the self-fetcher. The Reject references the original Follow by its
        // deterministic IRI, which the server stores (the Follow was stored when it was accepted).
        var followIri = new Iri($"{FollowerIri}/follows/{FollowerIri}");
        var reject = new KristofferStrube.ActivityStreams.Reject
        {
            Id = $"{FollowerIri}/rejects/{followIri}",
            Actor = [new KristofferStrube.ActivityStreams.Link { Href = new Uri(FollowerIri) }],
            Object = [new KristofferStrube.ActivityStreams.Link { Href = followIri.Uri }],
        };
        var rejectStatus = await client.DeliverAsync(new Iri(FollowerIri).InboxOf(), reject);
        Assert.Equal(202, rejectStatus);

        // The server's RejectActivityHandler removed the follow edge.
        Assert.False(await _persistence.Follows.IsFollowingAsync(new Iri(FollowerIri), new Iri(FollowerIri)),
            "the follow edge should be removed after the signed Reject is accepted");
    }

    // --- Helpers ----------------------------------------------------------------------

    /// <summary>
    /// Builds the server's <see cref="IActorDocumentFetcher"/> so it fetches actor documents through
    /// the in-process <see cref="TestServer"/> (the same host that owns the actors), signed with the
    /// follower's key. The <see cref="LazyHandler"/> defers the transport to the server's
    /// <see cref="TestServer.CreateHandler()"/> until first use, so the fetcher can be built before the
    /// <see cref="TestServer"/> exists. The server is captured by a <see cref="Func{TResult}"/> (rather
    /// than a reference) because the fetcher is wired by the very <c>ActivityPubHostFactory.Create</c>
    /// call that assigns the field the server reference.
    /// </summary>
    private static IActorDocumentFetcher BuildSelfFetcher(KeyPair followerKey, Func<TestServer> server)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(followerKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(new Iri(FollowerIri), followerKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = new Iri(FollowerIri), EnableRetry = false },
            new LazyHandler(server));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

}
