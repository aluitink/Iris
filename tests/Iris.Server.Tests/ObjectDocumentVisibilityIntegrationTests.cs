using System.Net;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Integration tests for the 139.2-s5a object-document visibility gate: a non-public (followers-only
/// or direct) post authored by a LOCAL actor is only served to a named recipient or the author.
/// A 404 hides the object's existence (the standard ActivityPub privacy convention).
/// </summary>
public sealed class ObjectDocumentVisibilityIntegrationTests : IDisposable
{
    private const string Host = "vis-obj.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Carol = "carol";

    private readonly TestServer _server;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly HttpClient _http;
    private readonly IActivityPubClient _aliceClient;
    private readonly IActivityPubClient _carolClient;
    private readonly Iri _alice;
    private readonly Iri _bob;

    public ObjectDocumentVisibilityIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        var (aliceKey, aliceIri, _) = TestSeeder.SeedPersonWithKey(_persistence, Host, Alice);
        var (bobKey, bobIri, _) = TestSeeder.SeedPersonWithKey(_persistence, Host, Bob);
        var (carolKey, carolIri, _) = TestSeeder.SeedPersonWithKey(_persistence, Host, Carol);
        _alice = aliceIri;
        _bob = bobIri;

        // Seed three notes authored by alice:
        // 1. Public (to: as:Public) — visible to everyone.
        // 2. DM (to: bob) — visible only to bob and alice.
        // 3. Followers-only (cc: followers IRI) — visible to alice and named recipients.
        SeedNote("public-1", to: [Iri.Public]);
        SeedNote("dm-1", to: [_bob]);
        SeedNote("followers-1", cc: [new Iri("https://www.w3.org/ns/activitystreams#Followers")]);

        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(aliceKey);
        keyStore.PutKey(bobKey);
        keyStore.PutKey(carolKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(_alice, aliceKey.KeyId);
        keyProvider.RegisterKey(_bob, bobKey.KeyId);
        keyProvider.RegisterKey(carolIri, carolKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);

        TestServer? serverRef = null;
        var selfFetcherHandler = () => serverRef!.CreateHandler();

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Alice,
            Persistence = _persistence,
            RegisterLocalKey = false,
            Fetcher = new IrisActorDocumentFetcher(
                factory.Create(
                    new ActivityPubClientOptions { ActorId = _alice, EnableRetry = false },
                    new LazyHandler(selfFetcherHandler)),
                new RemoteActorCache()),
        });
        serverRef = _server;

        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
        _aliceClient = factory.Create(
            new ActivityPubClientOptions { ActorId = _alice, EnableRetry = false },
            _server.CreateHandler());
        _carolClient = factory.Create(
            new ActivityPubClientOptions { ActorId = carolIri, EnableRetry = false },
            _server.CreateHandler());
    }

    private void SeedNote(string suffix, IEnumerable<Iri>? to = null, IEnumerable<Iri>? cc = null)
    {
        var noteIri = $"{_alice.Value}/notes/{suffix}";
        var note = new Note
        {
            Id = noteIri,
            Content = [$"note {suffix}"],
            AttributedTo = [new Link { Href = new Uri(_alice.Value) }],
            To = to?.Select(iri => (IObjectOrLink)new Link { Href = new Uri(iri.Value) }).ToList(),
            Cc = cc?.Select(iri => (IObjectOrLink)new Link { Href = new Uri(iri.Value) }).ToList(),
        };
        _persistence.Objects.PutObjectAsync(note).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _aliceClient.Dispose();
        _carolClient.Dispose();
        _http.Dispose();
        _server.Dispose();
    }

    private string NoteUrl(string suffix) => $"https://{Host}/ap/v1/u/{Alice}/notes/{suffix}";

    // --- Public content: visible to everyone -----------------------------------------

    [Fact]
    public async Task PublicNote_Anonymous_Returns200()
    {
        var resp = await _http.GetAsync(NoteUrl("public-1"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task PublicNote_OtherActor_Returns200()
    {
        var obj = await _carolClient.GetObjectAsync(new Iri(NoteUrl("public-1")));
        Assert.NotNull(obj);
    }

    // --- DM: invisible to non-recipients ---------------------------------------------

    [Fact]
    public async Task Dm_Anonymous_Returns404()
    {
        var resp = await _http.GetAsync(NoteUrl("dm-1"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Dm_NonRecipient_Returns404()
    {
        var obj = await _carolClient.GetObjectAsync(new Iri(NoteUrl("dm-1")));
        Assert.Null(obj);
    }

    [Fact]
    public async Task Dm_Author_Returns200()
    {
        var obj = await _aliceClient.GetObjectAsync(new Iri(NoteUrl("dm-1")));
        Assert.NotNull(obj);
    }

    // --- Followers-only: invisible to anonymous and non-named-recipients -------------

    [Fact]
    public async Task FollowersOnly_Anonymous_Returns404()
    {
        var resp = await _http.GetAsync(NoteUrl("followers-1"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task FollowersOnly_NonRecipient_Returns404()
    {
        var obj = await _carolClient.GetObjectAsync(new Iri(NoteUrl("followers-1")));
        Assert.Null(obj);
    }

    [Fact]
    public async Task FollowersOnly_Author_Returns200()
    {
        var obj = await _aliceClient.GetObjectAsync(new Iri(NoteUrl("followers-1")));
        Assert.NotNull(obj);
    }

    // --- Tombstone: always served (no visibility gate) --------------------------------

    [Fact]
    public async Task Tombstone_Anonymous_Returns200()
    {
        // Tombstones are always served (they carry no audience information).
        var noteIri = $"{_alice.Value}/notes/deleted-1";
        var tombstone = new Tombstone
        {
            Id = noteIri,
            FormerType = ["Note"],
        };
        await _persistence.Objects.PutObjectAsync(tombstone);

        var resp = await _http.GetAsync(NoteUrl("deleted-1"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
