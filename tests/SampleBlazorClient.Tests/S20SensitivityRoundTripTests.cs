using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Samples.SampleBlazorClient.Explorer;
using Iris.Samples.SampleServer;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 20.4 (sensitivity) integration test: a content-sensitive note (the ActivityStreams
/// <c>sensitive</c> term + <c>summary</c>) posted to the in-process instance survives the store/serve
/// round-trip — the server records it in the outbox and serves it back with <c>sensitive</c> and
/// <c>summary</c> intact, so <see cref="IriExtensions.IsSensitive(IObject)"/> and
/// <see cref="IriExtensions.GetSummary(IObject)"/> report it on the fetched object. This proves the
/// read-side sensitivity slice has a real, served object to render behind its notice.
/// </summary>
public sealed class S20SensitivityRoundTripTests
{
    private static Uri DialBase => new("http://localhost");

    /// <summary>
    /// Hosts a real <see cref="Iris.Server"/> ActivityPub pipeline (in-memory) with <c>alice</c>
    /// seeded at the dial base. Mirrors <see cref="S20OutboxPagingTests.StartHost"/>.
    /// </summary>
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
        alice.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["publicKey"] = JsonSerializer.SerializeToElement(new
            {
                id = aliceKeyId.Value,
                owner = aliceIri.Value,
                publicKeyPem = aliceKey.ExportPublicKeyPem(),
            }),
        };
        persistence.ActorStore.PutActorAsync(alice).GetAwaiter().GetResult();

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

    private static async Task<(TestServer Server, IActivityPubClient Client, Iri AliceIri)> LogOnAsync()
    {
        var server = StartHost();
        var session = new ExplorerSession(() => server.CreateHandler());
        var ok = await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase);
        Assert.True(ok, "logon to the in-process instance must succeed");
        return (server, session.GetClient(), new Iri("http://localhost/ap/v1/u/alice"));
    }

    /// <summary>
    /// Extracts the embedded note's id from a 202-body <c>Create</c> (the note's own id, carried in the
    /// Create's <c>object</c>) — distinct from the Create's top-level activity id.
    /// </summary>
    private static string? ExtractEmbeddedNoteId(IObjectOrLink? created)
        => created is Create { Object: { } objs }
            ? objs.OfType<IObject>().Select(o => o.Id).FirstOrDefault(i => !string.IsNullOrEmpty(i))
            : null;

    /// <summary>
    /// Posts a content-sensitive note (a <c>Create</c> wrapping a <c>Note</c> carrying the
    /// <c>sensitive</c> term and a <c>summary</c>) to the actor's outbox — the same path as
    /// <c>PostNoteAsync</c>, but with the sensitivity extension attached — and returns the
    /// embedded note's id (learned from the 202 body, per decision 055).
    /// </summary>
    private static async Task<Iri> PostSensitiveNoteAsync(
        IActivityPubClient client,
        Iri actor,
        string content,
        string summary)
    {
        var note = new Note
        {
            Content = [content],
            AttributedTo = [new Link { Href = actor.Uri }],
            Summary = [summary],
        };
        note.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["sensitive"] = JsonDocument.Parse("true").RootElement.Clone(),
        };

        var create = new Create
        {
            Actor = [new Link { Href = actor.Uri }],
            Object = [note],
        };

        var result = await client.DeliverAsync(actor.OutboxOf(), create, CancellationToken.None);
        Assert.Equal(202, result.StatusCode);

        // The 202 body is the created Create (decision 055); the embedded `object` (the Note) carries
        // the server-minted note id. (The Create's own top-level id is the activity id, not the note's.)
        var created = ActivityJson.Deserialize<IObjectOrLink>(result.Body);
        var noteId = ExtractEmbeddedNoteId(created);
        Assert.NotNull(noteId);
        return new Iri(noteId!);
    }

    [Fact]
    public async Task SensitiveNote_Posted_ServedBack_WithSensitivityIntact()
    {
        var (server, client, alice) = await LogOnAsync();
        using var _ = server;

        var noteId = await PostSensitiveNoteAsync(
            client, alice, "<p>the actual secret content</p>", "A secret photo");

        var fetched = await client.GetObjectAsync(noteId, CancellationToken.None);
        Assert.NotNull(fetched);

        // The served object carries the sensitivity term and the summary intact.
        Assert.True(fetched!.IsSensitive(), "a served sensitive note must report IsSensitive");
        Assert.Equal("A secret photo", fetched.GetSummary());
    }

    [Fact]
    public async Task OrdinaryNote_Posted_ServedBack_NotSensitive()
    {
        var (server, client, alice) = await LogOnAsync();
        using var _ = server;

        // A note posted the ordinary way (no sensitivity extension) must not report sensitive.
        var result = await client.PostNoteAsync(alice, "<p>an ordinary note</p>", ct: CancellationToken.None);
        Assert.Equal(202, result.StatusCode);

        var created = ActivityJson.Deserialize<IObjectOrLink>(result.Body);
        var embeddedId = ExtractEmbeddedNoteId(created);
        Assert.NotNull(embeddedId);

        var fetched = await client.GetObjectAsync(new Iri(embeddedId!), CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.False(fetched!.IsSensitive(), "an ordinary note must not report sensitive");
        Assert.Null(fetched.GetSummary());
    }
}
