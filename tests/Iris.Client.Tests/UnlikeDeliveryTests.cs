using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Tests;

/// <summary>
/// Verifies the client's unlike (the inverse of <see cref="ActivityPubClient.LikeAsync"/>) builds the
/// correct <see cref="KristofferStrube.ActivityStreams.Undo"/> — referencing the original
/// <see cref="KristofferStrube.ActivityStreams.Like"/> by its deterministic IRI — and publishes it to the
/// actor's own outbox (the party that made the like undoes it; a content object has no inbox of its own).
/// </summary>
public class UnlikeDeliveryTests
{
    private const string ActorIri = "https://a.domain.local/ap/v1/u/alice";
    private const string ObjectIri = "https://a.domain.local/ap/v1/u/bob/notes/1";

    [Fact]
    public async Task UnlikeAsync_PostsAnUndoOfTheLikeToTheActorsOwnOutbox()
    {
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = new ActivityPubClient(new HttpClient(fake));
        var actor = new Iri(ActorIri);
        // Decision 055 (learned-id references): the unlike references the id the SERVER minted for the
        // original like (learned from LikeAsync's DeliveryResult.MintedId) — the client never recomputes
        // the server's ids.
        var mintedLikeId = new Iri($"{ActorIri}/likes/{Guid.NewGuid():N}");

        var result = await client.UnlikeAsync(actor, mintedLikeId);

        Assert.Equal(202, result.StatusCode);
        Assert.True(result.IsSuccess);

        // The request is a POST to the actor's OWN outbox (the like's author undoes their own like).
        Assert.Equal(HttpMethod.Post, fake.LastRequest!.Method);
        Assert.EndsWith("/ap/v1/u/alice/outbox", fake.LastUri!.AbsolutePath);

        // The body is an Undo whose object references the original (server-minted) Like's id.
        // (ActivityJson serializes a single-element activity `object` as a bare IRI string.)
        var body = Encoding.UTF8.GetString(fake.LastBody);
        var doc = JsonNode.Parse(body)!.AsObject();
        Assert.Equal("Undo", (string?)doc["type"]);

        // The Undo's object is the learned (server-minted) like id, so the receiver resolves exactly the
        // like that was recorded.
        Assert.Equal(mintedLikeId.Value, (string?)doc["object"]);

        // Decision 055: the client sends the Undo's shape only — no id (the server mints the Undo's id).
        Assert.False(doc.ContainsKey("id"), "the client must not set the Undo's id (server is the authority)");
    }

    [Fact]
    public async Task UnlikeAsync_ReferencesTheMintedLikeIdLearnedFromLikeAsync()
    {
        // Decision 055: the client never recomputes the server's ids. LikeAsync mints the Like's id
        // server-side and returns it in the 2xx body (DeliveryResult.MintedId); UnlikeAsync references
        // that learned id. Simulate the round-trip: the like's fake returns a 202 body carrying the
        // created (minted) like; the caller learns it and passes it to UnlikeAsync.
        var mintedLikeIri = $"{ActorIri}/likes/{Guid.NewGuid():N}";
        var likeFake = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent(
                $"{{\"id\":\"{mintedLikeIri}\",\"type\":\"Like\"}}", Encoding.UTF8, ActivityJson.ActivityJsonContentType),
        });
        var unlikeFake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var actor = new Iri(ActorIri);
        var target = new Iri(ObjectIri);

        var likeClient = new ActivityPubClient(new HttpClient(likeFake));
        var unlikeClient = new ActivityPubClient(new HttpClient(unlikeFake));

        var likeResult = await likeClient.LikeAsync(actor, target);
        Assert.NotNull(likeResult.MintedId);
        _ = await unlikeClient.UnlikeAsync(actor, new Iri(likeResult.MintedId!));

        var undoBody = JsonNode.Parse(Encoding.UTF8.GetString(unlikeFake.LastBody)!)!.AsObject();
        var undoObjectIri = (string?)undoBody["object"];

        // The Undo references the same id the server minted for the like.
        Assert.Equal(likeResult.MintedId, undoObjectIri);
    }
}
