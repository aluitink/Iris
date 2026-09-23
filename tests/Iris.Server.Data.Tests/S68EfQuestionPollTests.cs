using Iris.Core;
using Iris.Server.Data;
using KristofferStrube.ActivityStreams;
using Xunit;

namespace Iris.Server.Data.Tests;

/// <summary>
/// S68 — a federated poll (an AS2.0 <c>Question</c> object) deserialized from the wire must be
/// visible to the EF store's <c>ListByActorAsync</c> so the home feed can surface it. This test
/// exercises the EF (PostgreSQL) store specifically, because the in-memory store keys
/// <c>ListByActorAsync</c> on the live CLR object's <c>attributedTo</c> property, while the EF
/// store keys it on the relational <c>AttributedTo</c> column populated at
/// <c>PutObjectAsync</c> time.
/// </summary>
public sealed class S68EfQuestionPollTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public S68EfQuestionPollTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private IPersistenceProvider NewProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Iris:ConnectionString"] = _fixture.ConnectionString,
                ["Iris:MediaBlobDir"] = Path.Combine(_fixture.BlobRoot, Guid.NewGuid().ToString("N")),
            })
            .Build();
        var services = new ServiceCollection();
        services.AddEntityFrameworkPersistence(config);
        var provider = services.BuildServiceProvider().GetRequiredService<IPersistenceProvider>();
        return provider;
    }

    private static Iri AuthorIri => new($"https://a.domain.local/ap/v1/u/bob-{Guid.NewGuid():N}");

    [Fact]
    public async Task EfStore_ListByActorAsync_ReturnsDeserializedQuestionPoll()
    {
        var p = NewProvider();
        var authorIri = AuthorIri;

        // The exact wire shape ActivityPubClient.PostQuestionAsync produces (embedded in a Create).
        // Deserializing from JSON simulates the federated inbound path: the remote instance's
        // CreateActivityHandler deserializes the Create, extracts the embedded Question, and stores
        // it via PutObjectAsync.
        var json = """
        {
          "type": "Create",
          "actor": "AUTHOR_IRI",
          "id": "https://a.domain.local/ap/v1/u/bob/outbox/create-1",
          "object": {
            "id": "OBJECT_IRI",
            "type": "Question",
            "content": "S68 EF federated poll",
            "attributedTo": "AUTHOR_IRI",
            "poll": {
              "options": [{"title":"Red","votesCount":0},{"title":"Blue","votesCount":0}],
              "expired": false,
              "multiple": false,
              "totalVotes": 0
            }
          }
        }
        """.Replace("AUTHOR_IRI", authorIri.Value);

        var create = ActivityJson.Deserialize<Create>(json);
        Assert.NotNull(create);

        // Extract the embedded object (mirrors CreateActivityHandler.StoreEmbeddedObjectAsync).
        var embedded = create!.ExtractEmbeddedObject();
        Assert.NotNull(embedded);

        // Verify the deserialized object's type and attributedTo.
        var asObject = embedded! as KristofferStrube.ActivityStreams.Object;
        Assert.NotNull(asObject);
        Assert.NotNull(asObject.AttributedTo);
        Assert.Single(asObject.AttributedTo!);
        var attrIri = asObject.AttributedTo!.FirstOrDefault()?.ResolveObjectIri();
        Assert.NotNull(attrIri);
        Assert.Equal(authorIri, attrIri!);

        // Store it (mirrors _persistence.Objects.PutObjectAsync(embedded)).
        await p.Objects.PutObjectAsync(embedded!);

        // ListByActorAsync must return it (mirrors FeedService.GetDeliveredContentAsync).
        var results = await p.Objects.ListByActorAsync(authorIri);
        var pollIriString = embedded!.ResolveObjectIri()?.ToString() ?? string.Empty;
        var poll = results.FirstOrDefault(o => o.Id == pollIriString);
        Assert.NotNull(poll);
        Assert.Contains("S68 EF federated poll", poll!.Content ?? []);

        // Search must also find it (the QA repro showed search finds the poll even when the
        // home feed does not).
        var searchResults = await p.Objects.SearchObjectsAsync("S68 EF federated poll", 10, 0);
        Assert.Contains(searchResults, o => o.Id == pollIriString);
    }

    [Fact]
    public async Task EfStore_ListByActorAsync_ReturnsNoteRegression()
    {
        // Regression: a regular Note must still be visible to ListByActorAsync.
        var p = NewProvider();
        var authorIri = AuthorIri;

        var note = new Note
        {
            Id = $"https://a.domain.local/ap/v1/objects/note-{Guid.NewGuid():N}",
            Content = ["S68 EF regression note"],
            AttributedTo = [new Link { Href = new Uri(authorIri.Value) }],
        };

        await p.Objects.PutObjectAsync(note);

        var results = await p.Objects.ListByActorAsync(authorIri);
        var found = results.FirstOrDefault(o => o.Id == note.Id);
        Assert.NotNull(found);
    }
}
