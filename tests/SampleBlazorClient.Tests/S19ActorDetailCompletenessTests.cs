using Iris.Client;
using Iris.Client.Collections;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Samples.SampleBlazorClient.Explorer;
using Iris.Samples.SampleBlazorClient.Pages;
using Iris.Samples.SampleServer;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 19.8.3 tests: the <c>ActorDetail</c> page now renders the actor's **followers** and
/// **following** collections (paged, clickable) plus a **raw inspector** (the actor document as formatted
/// JSON). These tests exercise the exact client calls the page makes —
/// <see cref="IActivityPubClient.GetCollectionAsync(Iri, CollectionQuery, CancellationToken)"/> against
/// <c>{actor}/followers</c> and <c>{actor}/following</c> — and verify the raw-JSON serialization the
/// inspector displays.
/// </summary>
public sealed class S19ActorDetailCompletenessTests
{
    private static Uri DialBase => new("http://localhost");

    private static TestServer StartHost()
    {
        const string dialBase = "http://localhost";
        var persistence = new InMemoryPersistenceProvider();
        var aliceIri = new Iri($"{dialBase}/ap/v1/u/alice");
        var aliceKeyId = new Iri($"{aliceIri.Value}#key-1");
        var aliceKey = KeyPairGenerator.GenerateRsa(aliceKeyId);
        persistence.Keys.PutKey(aliceKey);
        var alice = new Person
        {
            Id = aliceIri.Value,
            PreferredUsername = "alice",
            Name = ["alice"],
        };
        alice.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["publicKey"] = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                id = aliceKeyId.Value,
                owner = aliceIri.Value,
                publicKeyPem = aliceKey.ExportPublicKeyPem(),
            }),
        };
        persistence.ActorStore.PutActorAsync(alice).GetAwaiter().GetResult();

        var bobIri = new Iri($"{dialBase}/ap/v1/u/bob");
        var bobKeyId = new Iri($"{bobIri.Value}#key-1");
        var bobKey = KeyPairGenerator.GenerateRsa(bobKeyId);
        persistence.Keys.PutKey(bobKey);
        persistence.ActorStore.PutActorAsync(new Person
        {
            Id = bobIri.Value,
            PreferredUsername = "bob",
            Name = ["bob"],
        }).GetAwaiter().GetResult();

        var builder = new WebHostBuilder()
            .ConfigureLogging(l => { l.ClearProviders(); l.SetMinimumLevel(LogLevel.None); })
            .ConfigureServices(s =>
            {
                s.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
                s.AddRouting();
                s.AddActivityPubServer(opts =>
                {
                    opts.BaseUri = new Iri(dialBase);
                    opts.InstanceName = "iris-a";
                    opts.InstanceActorId = aliceIri;
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);
                s.AddSingleton<IKeyStore>(persistence.Keys);
                s.AddSingleton<IActorDocumentFetcher>(new PersistenceActorFetcher(persistence));
                s.AddSingleton<IActorCredentialValidator>(new BasicAuthCredentialValidator(
                    (_, username, password) =>
                    {
                        var valid = username == "alice"
                            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                                System.Text.Encoding.UTF8.GetBytes(password),
                                System.Text.Encoding.UTF8.GetBytes(SampleServer.SampleServer.Password));
                        return new ValueTask<bool>(valid);
                    }));
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        return new TestServer(builder);
    }

    private sealed class PersistenceActorFetcher(IPersistenceProvider persistence) : IActorDocumentFetcher
    {
        private readonly IPersistenceProvider _persistence = persistence;

        public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => await _persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct) ? actor : null;
    }

    private static async Task<(TestServer Server, IActivityPubClient Client, Iri Alice, Iri Bob)> LogOnAsync()
    {
        var server = StartHost();
        var session = new ExplorerSession(() => server.CreateHandler());
        var ok = await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase);
        Assert.True(ok, "logon to the in-process instance must succeed");
        var client = session.GetClient();
        var alice = new Iri("http://localhost/ap/v1/u/alice");
        var bob = new Iri("http://localhost/ap/v1/u/bob");
        return (server, client, alice, bob);
    }

    /// <summary>
    /// The page's followers read: <c>GetCollectionAsync(actorIri.FollowersOf())</c> returns the actors
    /// that follow this actor. After bob follows alice, alice's followers collection contains bob.
    /// </summary>
    [Fact]
    public async Task ActorDetail_FollowersCollection_ContainsFollowers()
    {
        var (server, client, alice, bob) = await LogOnAsync();
        using var _ = server;

        // Bob follows alice (so alice has a follower).
        var followResult = await client.FollowAsync(bob, alice);
        Assert.True(followResult.IsSuccess, $"bob's follow of alice must succeed: {followResult.StatusCode}");

        // The page's exact read: GetCollectionAsync against {actor}/followers.
        var followers = new List<IObjectOrLink>();
        await foreach (var page in client.GetCollectionAsync(alice.FollowersOf(), null, CancellationToken.None))
        {
            followers.AddRange(page.Items);
        }

        Assert.NotEmpty(followers);
        // Followers are served as ILink items (bare actor IRIs), not full actor objects.
        var bobItem = followers.First(f => f is ILink { Href: { } href } && href.ToString() == bob.Value);
        Assert.IsAssignableFrom<ILink>(bobItem);
    }

    /// <summary>
    /// The page's following read: <c>GetCollectionAsync(actorIri.FollowingOf())</c> returns the actors
    /// this actor follows. After alice follows bob, alice's following collection contains bob.
    /// </summary>
    [Fact]
    public async Task ActorDetail_FollowingCollection_ContainsFollowed()
    {
        var (server, client, alice, bob) = await LogOnAsync();
        using var _ = server;

        // Alice follows bob (so alice follows someone).
        var followResult = await client.FollowAsync(alice, bob);
        Assert.True(followResult.IsSuccess, $"alice's follow of bob must succeed: {followResult.StatusCode}");

        // The page's exact read: GetCollectionAsync against {actor}/following.
        var following = new List<IObjectOrLink>();
        await foreach (var page in client.GetCollectionAsync(alice.FollowingOf(), null, CancellationToken.None))
        {
            following.AddRange(page.Items);
        }

        Assert.NotEmpty(following);
        // Following items are also served as ILink (bare actor IRIs).
        var bobItem = following.First(f => f is ILink { Href: { } href } && href.ToString() == bob.Value);
        Assert.IsAssignableFrom<ILink>(bobItem);
    }

    /// <summary>
    /// The raw inspector's input: the actor document serializes to valid, parseable JSON carrying the
    /// actor's identity (id, preferredUsername). This is the exact serialization the page's
    /// <c>RawJson</c> property produces.
    /// </summary>
    [Fact]
    public async Task ActorDetail_RawJson_SerializesActorDocument()
    {
        var (server, client, alice, _) = await LogOnAsync();
        using var _ = server;

        // The page's exact read: GetActorAsync (typed).
        var actor = await client.GetActorAsync(alice);
        Assert.True(actor is not null, "GetActorAsync must return the actor");

        // The page's exact serialization: JsonSerializer.Serialize with WriteIndented.
        var rawJson = System.Text.Json.JsonSerializer.Serialize(
            actor,
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

        Assert.Contains(alice.Value, rawJson);
        Assert.Contains("alice", rawJson);

        // The JSON must be parseable (valid JSON).
        var parsed = System.Text.Json.JsonDocument.Parse(rawJson);
        Assert.Equal(alice.Value, parsed.RootElement.GetProperty("id").GetString());
        Assert.Equal("alice", parsed.RootElement.GetProperty("preferredUsername").GetString());
    }

    /// <summary>
    /// The followers/following collections are empty (no items) when the actor has no followers/following
    /// — the page renders "No followers recorded." / "Not following anyone." in that case.
    /// </summary>
    [Fact]
    public async Task ActorDetail_FollowersFollowing_EmptyByDefault()
    {
        var (server, client, alice, _) = await LogOnAsync();
        using var _ = server;

        // No follows have been made, so both collections are empty.
        var followers = new List<IObjectOrLink>();
        await foreach (var page in client.GetCollectionAsync(alice.FollowersOf(), null, CancellationToken.None))
        {
            followers.AddRange(page.Items);
        }
        Assert.Empty(followers);

        var following = new List<IObjectOrLink>();
        await foreach (var page in client.GetCollectionAsync(alice.FollowingOf(), null, CancellationToken.None))
        {
            following.AddRange(page.Items);
        }
        Assert.Empty(following);
    }

    // --- 19.8.1 minor UX fix: the #handle field accepts a full IRI (not just a username) ---

    private static readonly Uri TestDialBase = new("http://localhost");

    /// <summary>
    /// A plain username is resolved to the current instance's user IRI ({dial-base}/ap/v1/u/{username}).
    /// </summary>
    [Fact]
    public void ResolveActorIri_PlainUsername_ResolvesToUserIriOnDialBase()
    {
        var iri = ActorDetail.ResolveActorIri("alice", TestDialBase);

        Assert.Equal("http://localhost/ap/v1/u/alice", iri.Value);
    }

    /// <summary>
    /// A username with surrounding whitespace is trimmed before resolution.
    /// </summary>
    [Fact]
    public void ResolveActorIri_WhitespaceUsername_Trimmed()
    {
        var iri = ActorDetail.ResolveActorIri("  alice  ", TestDialBase);

        Assert.Equal("http://localhost/ap/v1/u/alice", iri.Value);
    }

    /// <summary>
    /// A full IRI (http) is used directly — it names the actor on its own instance (no double-path).
    /// </summary>
    [Fact]
    public void ResolveActorIri_FullHttpIri_UsedDirectly()
    {
        var iri = ActorDetail.ResolveActorIri("http://remote.example/ap/v1/u/bob", TestDialBase);

        Assert.Equal("http://remote.example/ap/v1/u/bob", iri.Value);
    }

    /// <summary>
    /// A full IRI (https) is used directly.
    /// </summary>
    [Fact]
    public void ResolveActorIri_FullHttpsIri_UsedDirectly()
    {
        var iri = ActorDetail.ResolveActorIri("https://remote.example/@bob", TestDialBase);

        Assert.Equal("https://remote.example/@bob", iri.Value);
    }

    /// <summary>
    /// An IRI-looking input that is not a valid absolute URL (http prefix but malformed) throws — the
    /// page surfaces the message in its error field rather than a confusing double-path "Not an actor".
    /// </summary>
    [Fact]
    public void ResolveActorIri_MalformedIri_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => ActorDetail.ResolveActorIri("http://", TestDialBase));
    }

    /// <summary>
    /// An empty (or whitespace-only) input throws — the page surfaces "Enter a username or an actor IRI."
    /// </summary>
    [Fact]
    public void ResolveActorIri_EmptyInput_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => ActorDetail.ResolveActorIri("   ", TestDialBase));
    }
}
