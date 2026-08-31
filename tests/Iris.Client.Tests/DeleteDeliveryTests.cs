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
        // The Delete gets a deterministic, unique-per-(actor,object) IRI (deletes/{suffix}).
        Assert.Equal($"{ActorIri}/deletes/abc123", (string?)doc["id"]);

        // The Delete's object is the object being deleted, by IRI (the server resolves it in its store).
        Assert.Equal(ObjectIri, (string?)doc["object"]);
    }

    [Fact]
    public async Task DeleteAsync_ReferenceIsThePostedNoteIri()
    {
        // Delete must reference the SAME note IRI that PostNoteAsync created, so the server can find and
        // tombstone the stored object. Post a note (capture the note's IRI from the Create), then delete
        // by that IRI and assert the Delete's object reference matches.
        var postFake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var deleteFake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var actor = new Iri(ActorIri);

        var postClient = new ActivityPubClient(new HttpClient(postFake));
        var deleteClient = new ActivityPubClient(new HttpClient(deleteFake));

        // Post a note; the Create's embedded note has id {actor}/notes/{suffix} and the Create's id is
        // {actor}/creates/{suffix} (the same suffix).
        _ = await postClient.PostNoteAsync(actor, "to be deleted");
        var createBody = JsonNode.Parse(Encoding.UTF8.GetString(postFake.LastBody)!)!.AsObject();
        // The note's IRI is the Create's sibling under /notes/ (same suffix).
        var createIri = (string?)createBody["id"];
        var noteSuffix = createIri!.Substring(createIri.LastIndexOf('/') + 1);
        var noteIri = new Iri($"{ActorIri}/notes/{noteSuffix}");

        _ = await deleteClient.DeleteAsync(actor, noteIri);
        var deleteBody = JsonNode.Parse(Encoding.UTF8.GetString(deleteFake.LastBody)!)!.AsObject();
        var deleteObjectIri = (string?)deleteBody["object"];

        Assert.Equal(noteIri.Value, deleteObjectIri);
    }
}
