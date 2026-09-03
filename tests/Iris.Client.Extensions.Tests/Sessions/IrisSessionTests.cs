using Iris.Client;
using Iris.Core;
using KristofferStrube.ActivityStreams;
using IrisSession = Iris.Client.Extensions.Sessions.IrisSession;

namespace Iris.Client.Extensions.Tests.Sessions;

/// <summary>
/// Unit tests for <see cref="IrisSession"/>: login (key stored + identity registered), identity
/// switching (previous key evicted), and logout/dispose (key removed, identity forgotten). The
/// <see cref="IClientAuthenticator"/> is faked so the tests are deterministic and do not touch the
/// network.
/// </summary>
public sealed class IrisSessionTests
{
    private const string Alice = "https://a.domain.local/ap/v1/u/alice";
    private const string Bob = "https://b.domain.local/ap/v1/u/bob";
    private const string AliceKey = "https://a.domain.local/ap/v1/u/alice#key-1";
    private const string BobKey = "https://b.domain.local/ap/v1/u/bob#key-1";

    private static (IrisSession Session, IKeyStore Store, IKeyProvider Provider, FakeAuthenticator Auth) New()
    {
        var store = new InMemoryKeyStore();
        var provider = new InMemoryKeyProvider(store);
        var auth = new FakeAuthenticator();
        var session = new IrisSession(auth, store, provider);
        return (session, store, provider, auth);
    }

    [Fact]
    public void NewSession_IsNotAuthenticated()
    {
        var (session, _, _, _) = New();

        Assert.False(session.IsAuthenticated);
        Assert.Null(session.CurrentActor);
        Assert.Null(session.CurrentActorIri);
    }

    [Fact]
    public async Task Login_StoresKey_RegistersIdentity_SetsCurrentActor()
    {
        var (session, store, provider, auth) = New();
        var key = KeyPairGenerator.GenerateEcP256(new Iri(AliceKey));
        auth.Result = new AuthenticatedActor(ActorDoc(Alice), key);

        var actor = await session.LoginAsync(new Iri(Alice));

        Assert.NotNull(actor);
        Assert.Equal(Alice, actor!.Id);
        Assert.True(session.IsAuthenticated);
        Assert.Equal(Alice, session.CurrentActorIri?.Value);
        Assert.Equal(actor, session.CurrentActor);

        // The key is in the session store (so the client factory can sign with it) and the actor's
        // signing identity is registered.
        Assert.True(store.TryGetKey(new Iri(AliceKey), out var stored));
        Assert.Same(key, stored);
        Assert.True(provider.TryGetIdentity(new Iri(Alice), out var identity));
        Assert.Equal(Alice, identity?.ActorId.Value);
        Assert.Equal(AliceKey, identity?.KeyId.Value);
    }

    [Fact]
    public async Task Login_AuthenticationFails_ReturnsNull_LeavesSessionUnauthenticated()
    {
        var (session, store, _, auth) = New();
        auth.Result = null; // e.g. the server rejected the credentials.

        var actor = await session.LoginAsync(new Iri(Alice));

        Assert.Null(actor);
        Assert.False(session.IsAuthenticated);
        Assert.Null(session.CurrentActor);
        Assert.False(store.TryGetKey(new Iri(AliceKey), out _));
    }

    [Fact]
    public async Task SwitchIdentity_ReplacesPreviousKey()
    {
        var (session, store, _, auth) = New();
        var aliceKey = KeyPairGenerator.GenerateEcP256(new Iri(AliceKey));
        auth.Result = new AuthenticatedActor(ActorDoc(Alice), aliceKey);
        await session.LoginAsync(new Iri(Alice));

        var bobKey = KeyPairGenerator.GenerateEcP256(new Iri(BobKey));
        auth.Result = new AuthenticatedActor(ActorDoc(Bob), bobKey);
        var actor = await session.SwitchIdentityAsync(new Iri(Bob));

        Assert.Equal(Bob, actor!.Id);
        Assert.Equal(Bob, session.CurrentActorIri?.Value);
        // The previous (alice) key is evicted; only bob's key remains.
        Assert.False(store.TryGetKey(new Iri(AliceKey), out _));
        Assert.True(store.TryGetKey(new Iri(BobKey), out var stored));
        Assert.Same(bobKey, stored);
    }

    [Fact]
    public async Task Logout_RemovesKey_ForgetsIdentity()
    {
        var (session, store, provider, auth) = New();
        var key = KeyPairGenerator.GenerateEcP256(new Iri(AliceKey));
        auth.Result = new AuthenticatedActor(ActorDoc(Alice), key);
        await session.LoginAsync(new Iri(Alice));

        session.Logout();

        Assert.False(session.IsAuthenticated);
        Assert.Null(session.CurrentActor);
        Assert.Null(session.CurrentActorIri);
        Assert.False(store.TryGetKey(new Iri(AliceKey), out _));
        Assert.False(provider.TryGetIdentity(new Iri(Alice), out _));
    }

    [Fact]
    public async Task Dispose_LogsOut()
    {
        var (session, store, _, auth) = New();
        var key = KeyPairGenerator.GenerateEcP256(new Iri(AliceKey));
        auth.Result = new AuthenticatedActor(ActorDoc(Alice), key);
        await session.LoginAsync(new Iri(Alice));

        session.Dispose();

        Assert.False(session.IsAuthenticated);
        Assert.False(store.TryGetKey(new Iri(AliceKey), out _));
    }

    [Fact]
    public async Task Logout_WithNoLogin_IsNoOp()
    {
        var (session, _, _, auth) = New();

        // Must not throw.
        session.Logout();

        Assert.False(session.IsAuthenticated);

        // The session is still usable after a no-op logout.
        var key = KeyPairGenerator.GenerateEcP256(new Iri(AliceKey));
        auth.Result = new AuthenticatedActor(ActorDoc(Alice), key);
        var actor = await session.LoginAsync(new Iri(Alice));
        Assert.NotNull(actor);
    }

    private static Actor ActorDoc(string id) => new() { Id = id, PreferredUsername = id[(id.LastIndexOf('/') + 1)..] };

    /// <summary>
    /// A fake <see cref="IClientAuthenticator"/> that returns a canned
    /// <see cref="AuthenticatedActor"/> (or null) without touching the network.
    /// </summary>
    private sealed class FakeAuthenticator : IClientAuthenticator
    {
        public AuthenticatedActor? Result { get; set; }

        public Task<AuthenticatedActor?> AuthenticateAsync(Iri actorId, CancellationToken ct = default)
            => Task.FromResult(Result);
    }
}
