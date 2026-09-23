using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Stores;
using KristofferStrube.ActivityStreams;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// S68 diagnostic — verifies what concrete type a federated Question (poll) deserializes to
/// when a Create activity is received over the wire, and whether the object store's
/// ListByActorAsync can see it.
/// </summary>
public sealed class S68QuestionDeserializationTests
{
    private readonly InMemoryPersistenceProvider _persistence = new();

    private static Iri AuthorIri => new("https://a.domain.local/ap/v1/u/bob");

    [Fact]
    public void Question_DeserializesToCorrectType()
    {
        // The exact wire shape ActivityPubClient.PostQuestionAsync produces (embedded in a Create).
        var json = """
        {
          "type": "Create",
          "actor": "https://a.domain.local/ap/v1/u/bob",
          "object": {
            "id": "https://a.domain.local/ap/v1/objects/poll-1",
            "type": "Question",
            "content": "S68 poll test",
            "attributedTo": "https://a.domain.local/ap/v1/u/bob",
            "poll": {
              "options": [{"title":"Red","votesCount":0},{"title":"Blue","votesCount":0}],
              "expired": false,
              "multiple": false,
              "totalVotes": 0
            }
          }
        }
        """;

        var create = ActivityJson.Deserialize<Create>(json);
        Assert.NotNull(create);
        Assert.NotNull(create!.Object);
        Assert.Single(create.Object!);

        var obj = create.Object!.First();
        // Diagnostic: what type did the library deserialize to?
        Assert.True(obj is IObject, $"Expected IObject, got {obj.GetType().Name}");
        Assert.True(obj is Question, $"Expected Question, got {obj.GetType().Name}");

        // The object store cast path: (obj as KristofferStrube.ActivityStreams.Object)
        var asObject = obj as KristofferStrube.ActivityStreams.Object;
        Assert.NotNull(asObject);
        Assert.NotNull(asObject!.AttributedTo);
        Assert.Single(asObject.AttributedTo!);
    }

    [Fact]
    public async Task FederatedQuestion_IsVisibleToHomeFeed()
    {
        // Simulate the full federated path: a Create arrives, the embedded Question is
        // extracted and stored via PutObjectAsync, then ListByActorAsync must return it.
        var json = """
        {
          "type": "Create",
          "actor": "https://a.domain.local/ap/v1/u/bob",
          "id": "https://a.domain.local/ap/v1/u/bob/outbox/create-1",
          "object": {
            "id": "https://a.domain.local/ap/v1/objects/poll-1",
            "type": "Question",
            "content": "S68 federated poll",
            "attributedTo": "https://a.domain.local/ap/v1/u/bob",
            "poll": {
              "options": [{"title":"Red","votesCount":0},{"title":"Blue","votesCount":0}],
              "expired": false,
              "multiple": false,
              "totalVotes": 0
            }
          }
        }
        """;

        var create = ActivityJson.Deserialize<Create>(json);
        Assert.NotNull(create);

        // Extract the embedded object (mirrors CreateActivityHandler.StoreEmbeddedObjectAsync).
        var embedded = create!.ExtractEmbeddedObject();
        Assert.NotNull(embedded);

        // Store it (mirrors _persistence.Objects.PutObjectAsync(embedded)).
        await _persistence.Objects.PutObjectAsync(embedded!);

        // ListByActorAsync must return it (mirrors FeedService.GetDeliveredContentAsync).
        var results = await _persistence.Objects.ListByActorAsync(AuthorIri);
        var poll = results.FirstOrDefault(o => o.Id == "https://a.domain.local/ap/v1/objects/poll-1");
        Assert.NotNull(poll);
        Assert.Contains("S68 federated poll", poll!.Content ?? []);
    }

    [Fact]
    public async Task FederatedQuestion_IsOwnContentItem()
    {
        // Verify that the deserialized Question passes the IsOwnContentItem filter
        // (the home feed's own-outbox filter checks `o is Note || o is Article || o is Question || o is Page`).
        var json = """
        {
          "type": "Create",
          "actor": "https://a.domain.local/ap/v1/u/bob",
          "object": {
            "id": "https://a.domain.local/ap/v1/objects/poll-1",
            "type": "Question",
            "content": "S68 poll",
            "attributedTo": "https://a.domain.local/ap/v1/u/bob"
          }
        }
        """;

        var create = ActivityJson.Deserialize<Create>(json);
        Assert.NotNull(create);
        Assert.NotNull(create!.Object);

        var obj = create.Object!.First();
        // The home feed filter: o is Note || o is Article || o is Question || o is Page
        Assert.True(
            obj is Note || obj is Article || obj is Question || obj is Page,
            $"Object type {obj.GetType().Name} is not Note/Article/Question/Page");
    }

    [Fact]
    public async Task RewriteAttributedToToAdvertisedBase_RewritesDialBaseToAdvertised()
    {
        // S68: an embedded object with a dial-base attributedTo must be rewritten to the
        // advertised-base IRI so the home feed's ListByActorAsync query matches.
        const string advertisedBase = "https://dev2-iris-a.luit.ink";

        var json = """
        {
          "id": "https://localhost:20081/ap/v1/objects/poll-1",
          "type": "Question",
          "content": "S68 dial-base poll",
          "attributedTo": "http://localhost:20081/ap/v1/u/bob"
        }
        """;

        var obj = ActivityJson.Deserialize<KristofferStrube.ActivityStreams.Object>(json);
        Assert.NotNull(obj);

        var rewritten = await obj.RewriteAttributedToToAdvertisedBaseAsync(
            advertisedBase, "localhost:20081", CancellationToken.None);
        Assert.NotNull(rewritten);

        var newAttrIri = rewritten!.AttributedTo!.FirstOrDefault()?.ResolveObjectIri();
        Assert.NotNull(newAttrIri);
        Assert.Equal($"{advertisedBase}/ap/v1/u/bob", newAttrIri!.ToString());
    }

    [Fact]
    public async Task RewriteAttributedToToAdvertisedBase_NoOpForAlreadyCanonical()
    {
        // An object with an already-canonical advertised-base attributedTo is not rewritten.
        const string advertisedBase = "https://dev2-iris-a.luit.ink";

        var json = """
        {
          "id": "https://dev2-iris-a.luit.ink/ap/v1/objects/poll-1",
          "type": "Question",
          "content": "S68 canonical poll",
          "attributedTo": "https://dev2-iris-a.luit.ink/ap/v1/u/bob"
        }
        """;

        var obj = ActivityJson.Deserialize<KristofferStrube.ActivityStreams.Object>(json);
        Assert.NotNull(obj);

        var rewritten = await obj.RewriteAttributedToToAdvertisedBaseAsync(
            advertisedBase, "localhost:20081", CancellationToken.None);
        Assert.Null(rewritten);
    }

    [Fact]
    public async Task RewriteAttributedToToAdvertisedBase_NoOpForRemoteHost()
    {
        // An object with a remote-host attributedTo is not rewritten (genuinely foreign author).
        const string advertisedBase = "https://dev2-iris-a.luit.ink";

        var json = """
        {
          "id": "https://remote-instance.com/ap/v1/objects/poll-1",
          "type": "Question",
          "content": "S68 remote poll",
          "attributedTo": "https://remote-instance.com/ap/v1/u/remote-bob"
        }
        """;

        var obj = ActivityJson.Deserialize<KristofferStrube.ActivityStreams.Object>(json);
        Assert.NotNull(obj);

        var rewritten = await obj.RewriteAttributedToToAdvertisedBaseAsync(
            advertisedBase, "localhost:20081", CancellationToken.None);
        Assert.Null(rewritten);
    }
}
