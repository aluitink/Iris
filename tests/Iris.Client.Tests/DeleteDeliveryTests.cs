using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Tests;

/// <summary>
/// Verifies the client's delete (the inverse of a post) builds the correct
/// <see cref="KristofferStrube.ActivityStreams.Delete"/> — referencing the object being deleted by IRI —
/// and publishes it to the actor's own outbox (the author deletes their own content; a content object has
/// no inbox of its own). The server routes the <see cref="Delete"/> to its
/// <see cref="Iris.Server.Inbox.DeleteActivityHandler"/> (tombstone + reply-edge cleanup + propagation).
/// </summary>
public class DeleteDeliveryTests
{
    private const string ActorIri = "https://a.domain.local/ap/v1/u/alice";
    private const string ObjectIri = "https://a.domain.local/ap/v1/u/alice/notes/abc123";

    [Fact]
    public async Task DeleteAsync_PostsADeleteOfTheObjectToTheActorsOwnOutbox()
    {
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = new ActivityPubClient(new HttpClient(fake));
        var actor = new Iri(ActorIri);
        var objectIri = new Iri(ObjectIri);

        var result = await client.DeleteAsync(actor, objectIri);

        Assert.Equal(202, result.StatusCode);
        Assert.True(result.IsSuccess);

        // The request is a POST to the actor's OWN outbox (the author deletes their own content).
        Assert.Equal(HttpMethod.Post, fake.LastRequest!.Method);
        Assert.EndsWith("/ap/v1/u/alice/outbox", fake.LastUri!.AbsolutePath);

        // The body is a Delete referencing the object being deleted by its IRI (a bare link — the common
        // case, mirroring the server's DeleteActivityHandler). (ActivityJson serializes a single-element
        // activity `object` as a bare IRI string.)
        var body = Encoding.UTF8.GetString(fake.LastBody);
        var doc = JsonNode.Parse(body)!.AsObject();
        Assert.Equal("Delete", (string?)doc["type"]);

        // The Delete's object is the object being deleted, by IRI (the server resolves it in its store).
        Assert.Equal(ObjectIri, (string?)doc["object"]);

        // Decision 055: the client sends the Delete's shape only — no id (the server mints the Delete's
        // id and returns it in the 2xx body).
        Assert.False(doc.ContainsKey("id"), "the client must not set the Delete's id (server is the authority)");
    }

    [Fact]
    public async Task DeleteAsync_ReferenceIsThePostedNoteIri()
    {
        // Decision 055: PostNoteAsync mints the Create's id AND the embedded note's id server-side and
        // returns the created Create in the 2xx body. The caller learns the note's IRI from the returned
        // body's `object` (the embedded note's minted id) and passes it to DeleteAsync. Simulate the
        // round-trip: the post's fake returns a 202 body carrying the created Create (with the embedded
        // note's minted id); the caller learns the note IRI and deletes by it.
        var mintedNoteIri = $"{ActorIri}/notes/{Guid.NewGuid():N}";
        var mintedCreateIri = $"{ActorIri}/creates/{Guid.NewGuid():N}";
        var createdCreateJson = new JsonObject
        {
            ["id"] = mintedCreateIri,
            ["type"] = "Create",
            ["object"] = new JsonObject
            {
                ["id"] = mintedNoteIri,
                ["type"] = "Note",
                ["content"] = "to be deleted",
            },
        }.ToJsonString();
        var postFake = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent(createdCreateJson, Encoding.UTF8, ActivityJson.ActivityJsonContentType),
        });
        var deleteFake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var actor = new Iri(ActorIri);

        var postClient = new ActivityPubClient(new HttpClient(postFake));
        var deleteClient = new ActivityPubClient(new HttpClient(deleteFake));

        // Post a note; learn the note's IRI from the returned Create's embedded object (the 2xx body).
        var postResult = await postClient.PostNoteAsync(actor, "to be deleted");
        Assert.NotNull(postResult.MintedId);
        using var createDoc = System.Text.Json.JsonDocument.Parse(postResult.Body);
        var noteIri = new Iri(createDoc.RootElement.GetProperty("object").GetProperty("id").GetString()!);

        _ = await deleteClient.DeleteAsync(actor, noteIri);
        var deleteBody = JsonNode.Parse(Encoding.UTF8.GetString(deleteFake.LastBody)!)!.AsObject();
        var deleteObjectIri = (string?)deleteBody["object"];

        // The Delete references the SAME (server-minted) note IRI the post created.
        Assert.Equal(mintedNoteIri, deleteObjectIri);
    }
}
