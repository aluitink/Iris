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
        var target = new Iri(ObjectIri);

        var result = await client.UnlikeAsync(actor, target);

        Assert.Equal(202, result.StatusCode);
        Assert.True(result.IsSuccess);

        // The request is a POST to the actor's OWN outbox (the like's author undoes their own like).
        Assert.Equal(HttpMethod.Post, fake.LastRequest!.Method);
        Assert.EndsWith("/ap/v1/u/alice/outbox", fake.LastUri!.AbsolutePath);

        // The body is an Undo whose object references the original Like by its deterministic IRI.
        // (ActivityJson serializes a single-element activity `object` as a bare IRI string.)
        var body = Encoding.UTF8.GetString(fake.LastBody);
        var doc = JsonNode.Parse(body)!.AsObject();
        Assert.Equal("Undo", (string?)doc["type"]);
        Assert.Equal($"{ActorIri}/unlikes/{ObjectIri}", (string?)doc["id"]);

        // The Undo's object is the original Like's IRI ({actor}/likes/{object}), so the receiver
        // resolves exactly the like that was recorded.
        Assert.Equal($"{ActorIri}/likes/{ObjectIri}", (string?)doc["object"]);
    }

    [Fact]
    public async Task UnlikeAsync_MatchesLikeAsyncsDeterministicLikeIri()
    {
        // Unlike must reference the SAME like IRI that LikeAsync mints, so the receiver can find the
        // recorded like. Capture both and assert the reference matches.
        var likeFake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var unlikeFake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var actor = new Iri(ActorIri);
        var target = new Iri(ObjectIri);

        var likeClient = new ActivityPubClient(new HttpClient(likeFake));
        var unlikeClient = new ActivityPubClient(new HttpClient(unlikeFake));

        _ = await likeClient.LikeAsync(actor, target);
        _ = await unlikeClient.UnlikeAsync(actor, target);

        var likeBody = JsonNode.Parse(Encoding.UTF8.GetString(likeFake.LastBody)!)!.AsObject();
        var likeId = (string?)likeBody["id"];

        var undoBody = JsonNode.Parse(Encoding.UTF8.GetString(unlikeFake.LastBody)!)!.AsObject();
        var undoObjectIri = (string?)undoBody["object"];

        Assert.Equal(likeId, undoObjectIri);
    }
}
