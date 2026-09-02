using System.Net;
using System.Text;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Tests;

/// <summary>
/// Unit tests for <see cref="Iris.Client.ActivityPubClient"/>: it deserializes fetched objects
/// into <see cref="IObject"/> (via <see cref="IObjectOrLink"/>) and delivers activities as signed
/// <c>application/activity+json</c> POSTs.
/// </summary>
public class ActivityPubClientTests
{
    private const string ActorIri = "https://b.domain.local/u/bob";
    private const string InboxIri = "https://b.domain.local/u/bob/inbox";

    private const string PersonJson =
        """
        {"id":"https://b.domain.local/u/bob","type":"Person","name":"Bob","preferredUsername":"bob"}
        """;

    [Fact]
    public async Task GetObjectAsync_DeserializesIObjectOrLink_ReturnsIObject()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(PersonJson, Encoding.UTF8, ActivityJson.ActivityJsonContentType),
        };
        var client = new ActivityPubClient(new HttpClient(new FakeHttpHandler(response)));

        var @object = await client.GetObjectAsync(new Iri(ActorIri));

        Assert.NotNull(@object);
        Assert.Equal(ActorIri, @object!.Id);
    }

    [Fact]
    public async Task GetObjectAsync_NonSuccess_ReturnsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotFound);
        var client = new ActivityPubClient(new HttpClient(new FakeHttpHandler(response)));

        Assert.Null(await client.GetObjectAsync(new Iri(ActorIri)));
    }

    [Fact]
    public async Task GetActorAsync_ActorDocument_ReturnsActor()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(PersonJson, Encoding.UTF8, ActivityJson.ActivityJsonContentType),
        };
        var client = new ActivityPubClient(new HttpClient(new FakeHttpHandler(response)));

        var actor = await client.GetActorAsync(new Iri(ActorIri));

        Assert.NotNull(actor);
        Assert.Equal(ActorIri, actor!.Id);
    }

    [Fact]
    public async Task GetActorAsync_NoteDocument_ReturnsNull()
    {
        const string noteJson = """{"id":"https://b.domain.local/n/1","type":"Note","content":"hi"}""";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(noteJson, Encoding.UTF8, ActivityJson.ActivityJsonContentType),
        };
        var client = new ActivityPubClient(new HttpClient(new FakeHttpHandler(response)));

        Assert.Null(await client.GetActorAsync(new Iri("https://b.domain.local/n/1")));
    }

    [Fact]
    public async Task DeliverAsync_PostsActivityWithActivityJsonContentType_ReturnsStatusCode()
    {
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = new ActivityPubClient(new HttpClient(fake));

        var actorUri = new Uri(ActorIri);
        var follow = new Follow
        {
            Actor = [new Link { Href = actorUri }],
            Object = [new Link { Href = actorUri }],
        };

        var result = await client.DeliverAsync(new Iri(InboxIri), follow);

        Assert.Equal(202, result.StatusCode);
        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Post, fake.LastRequest!.Method);
        Assert.Equal(InboxIri, fake.LastUri!.ToString());
        var contentType = fake.LastRequest.Content!.Headers.ContentType!.MediaType;
        Assert.Equal(ActivityJson.ActivityJsonContentType, contentType);
        var body = Encoding.UTF8.GetString(fake.LastBody);
        Assert.Contains("\"Follow\"", body);
    }

    [Fact]
    public async Task DeliverAsync_401_ReturnsUnsuccessfulResult()
    {
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Unauthorized"),
        });
        var client = new ActivityPubClient(new HttpClient(fake));

        var actorUri = new Uri(ActorIri);
        var follow = new Follow
        {
            Actor = [new Link { Href = actorUri }],
            Object = [new Link { Href = actorUri }],
        };

        var result = await client.DeliverAsync(new Iri(InboxIri), follow);

        Assert.Equal(401, result.StatusCode);
        Assert.False(result.IsSuccess);
        Assert.Equal("Unauthorized", result.Body);
    }

    [Fact]
    public async Task DeliverAsync_202WithBody_ReturnsSuccessResultWithBody()
    {
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("Accepted"),
        });
        var client = new ActivityPubClient(new HttpClient(fake));

        var actorUri = new Uri(ActorIri);
        var follow = new Follow
        {
            Actor = [new Link { Href = actorUri }],
            Object = [new Link { Href = actorUri }],
        };

        var result = await client.DeliverAsync(new Iri(InboxIri), follow);

        Assert.Equal(202, result.StatusCode);
        Assert.True(result.IsSuccess);
        Assert.Equal("Accepted", result.Body);
    }

    [Fact]
    public async Task DeliverAsync_NonActivity_Throws()
    {
        var client = new ActivityPubClient(new HttpClient(new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK))));
        var person = new Person { Id = ActorIri };

        await Assert.ThrowsAsync<ArgumentException>(() => client.DeliverAsync(new Iri(InboxIri), person));
    }

    // --- FollowAsync (J-9): the client's one-call "follow" ---------------------------

    [Fact]
    public async Task FollowAsync_PostsFollowToTargetInbox_WithActivityJson()
    {
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = new ActivityPubClient(new HttpClient(fake));

        var follower = new Iri("https://a.domain.local/u/alice");
        var result = await client.FollowAsync(follower, new Iri(ActorIri));

        Assert.Equal(202, result.StatusCode);
        // The follow is published to the *follower's own* outbox (the write surface for the activities an
        // actor authors — the client never addresses a recipient's inbox).
        Assert.Equal(HttpMethod.Post, fake.LastRequest!.Method);
        Assert.Equal("https://a.domain.local/u/alice/outbox", fake.LastUri!.ToString());
        Assert.Equal(ActivityJson.ActivityJsonContentType, fake.LastRequest.Content!.Headers.ContentType!.MediaType);

        var body = Encoding.UTF8.GetString(fake.LastBody);
        // A Follow activity: actor = follower, object = target. A Link in a multi-valued slot
        // serializes as its bare IRI (a string), not an {"href":...} object.
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("Follow", root.GetProperty("type").GetString());
        Assert.Equal(follower.Value, root.GetProperty("actor").GetString());
        Assert.Equal(ActorIri, root.GetProperty("object").GetString());
        // Decision 055: the client sends the follow's shape only — no id (the server mints it and returns
        // it in the 2xx body).
        Assert.False(root.TryGetProperty("id", out _), "the client must not set the follow's id (server is the authority)");
    }

    [Fact]
    public async Task FollowAsync_CommunityTarget_DerivesCommunityInbox()
    {
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = new ActivityPubClient(new HttpClient(fake));

        var community = new Iri("https://b.domain.local/c/iris");
        var result = await client.FollowAsync(new Iri("https://a.domain.local/u/alice"), community);

        Assert.Equal(202, result.StatusCode);
        // Following a community is still published to the *follower's own* outbox (the write surface for
        // the activities an actor authors); the object is the community (the server resolves the recipient).
        Assert.Equal("https://a.domain.local/u/alice/outbox", fake.LastUri!.ToString());
        using var doc = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(fake.LastBody));
        Assert.Equal(community.Value, doc.RootElement.GetProperty("object").GetString());
    }

    [Fact]
    public async Task FollowAsync_InboxReturnsNotAccepted_PropagatesStatusCode()
    {
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.BadRequest));
        var client = new ActivityPubClient(new HttpClient(fake));

        var result = await client.FollowAsync(new Iri("https://a.domain.local/u/alice"), new Iri(ActorIri));

        // The raw delivery status is surfaced so the caller can react (e.g. 400 malformed follow).
        Assert.Equal(400, result.StatusCode);
    }

    // --- AcceptAsync / RejectAsync (19.0b AP-native rework): the followed side's decision ---
    //
    // An Accept/Reject is the FOLLOWED actor's response to an inbound Follow. It is published to the
    // followed actor's OWN outbox (the write surface for the activities an actor authors) and signed as
    // that actor; the server records it, applies the local follow-edge effect, and delivers it to the
    // follower's inbox (the server owns the recipient hop). The object references the original Follow by
    // IRI, and the deterministic id ({actor}/accepts/{follow} or {actor}/rejects/{follow}) dedupes retries.

    [Fact]
    public async Task AcceptAsync_PostsAcceptToFollowedActorOutbox_WithFollowAsObject()
    {
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = new ActivityPubClient(new HttpClient(fake));

        var actor = new Iri(ActorIri);
        var followIri = new Iri("https://a.domain.local/activities/follow-123");
        var result = await client.AcceptAsync(actor, followIri);

        Assert.Equal(202, result.StatusCode);
        // The Accept is published to the *followed actor's own* outbox (the actorId is the one deciding).
        Assert.Equal(HttpMethod.Post, fake.LastRequest!.Method);
        Assert.Equal($"{ActorIri}/outbox", fake.LastUri!.ToString());
        Assert.Equal(ActivityJson.ActivityJsonContentType, fake.LastRequest.Content!.Headers.ContentType!.MediaType);

        var body = Encoding.UTF8.GetString(fake.LastBody);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("Accept", root.GetProperty("type").GetString());
        Assert.Equal(ActorIri, root.GetProperty("actor").GetString());
        // The object references the original Follow by IRI (a Link serializes as its bare IRI string).
        Assert.Equal(followIri.Value, root.GetProperty("object").GetString());
        // Decision 055: no client-set id (the server mints the Accept's id).
        Assert.False(root.TryGetProperty("id", out _), "the client must not set the Accept's id (server is the authority)");
    }

    [Fact]
    public async Task RejectAsync_PostsRejectToFollowedActorOutbox_WithFollowAsObject()
    {
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = new ActivityPubClient(new HttpClient(fake));

        var actor = new Iri(ActorIri);
        var followIri = new Iri("https://a.domain.local/activities/follow-123");
        var result = await client.RejectAsync(actor, followIri);

        Assert.Equal(202, result.StatusCode);
        Assert.Equal(HttpMethod.Post, fake.LastRequest!.Method);
        Assert.Equal($"{ActorIri}/outbox", fake.LastUri!.ToString());

        var body = Encoding.UTF8.GetString(fake.LastBody);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("Reject", root.GetProperty("type").GetString());
        Assert.Equal(ActorIri, root.GetProperty("actor").GetString());
        Assert.Equal(followIri.Value, root.GetProperty("object").GetString());
        // Decision 055: no client-set id (the server mints the Reject's id).
        Assert.False(root.TryGetProperty("id", out _), "the client must not set the Reject's id (server is the authority)");
    }

    [Fact]
    public async Task AcceptAsync_CommunityFollowedActor_PostsToCommunityOutbox()
    {
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = new ActivityPubClient(new HttpClient(fake));

        // A community (Group) being followed is decided on the same way: the Accept is published to the
        // community's own outbox (the community is the actor deciding).
        var community = new Iri("https://b.domain.local/c/iris");
        var followIri = new Iri("https://a.domain.local/activities/follow-456");
        var result = await client.AcceptAsync(community, followIri);

        Assert.Equal(202, result.StatusCode);
        Assert.Equal($"{community.Value}/outbox", fake.LastUri!.ToString());
        using var doc = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(fake.LastBody));
        Assert.Equal(community.Value, doc.RootElement.GetProperty("actor").GetString());
        Assert.Equal(followIri.Value, doc.RootElement.GetProperty("object").GetString());
    }

    // --- PostNoteAsync (J-6): the client's one-call "post a note" ----------------------

    [Fact]
    public async Task PostNoteAsync_PostsCreateToAuthorInbox_WithEmbeddedNote()
    {
        // A fresh response per call: DeliverAsync disposes the response it receives, so a shared
        // response instance would be disposed after the first call (the dedupe re-posts below).
        var fake = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = new ActivityPubClient(new HttpClient(fake));

        var author = new Iri("https://a.domain.local/u/alice");
        var result = await client.PostNoteAsync(author, "hello world");

        Assert.Equal(202, result.StatusCode);
        // The post is published to the *author's own* outbox (the "local post" path — the outbox is the
        // write surface for the activities an actor authors).
        Assert.Equal(HttpMethod.Post, fake.LastRequest!.Method);
        Assert.Equal("https://a.domain.local/u/alice/outbox", fake.LastUri!.ToString());
        Assert.Equal(ActivityJson.ActivityJsonContentType, fake.LastRequest.Content!.Headers.ContentType!.MediaType);

        var body = Encoding.UTF8.GetString(fake.LastBody);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("Create", root.GetProperty("type").GetString());
        // A Link in a multi-valued slot serializes as its bare IRI (a string).
        Assert.Equal(author.Value, root.GetProperty("actor").GetString());
        // The embedded note is a full object (not a link) so the receiver stores the content without
        // a second fetch.
        var note = root.GetProperty("object");
        Assert.Equal("Note", note.GetProperty("type").GetString());
        Assert.Equal("hello world", note.GetProperty("content").GetString());
        // The note is attributed to the author.
        Assert.Equal(author.Value, note.GetProperty("attributedTo").GetString());
        // Decision 055: the client sends the post's shape only — no Create id, no embedded-note id. The
        // server mints both (unguessable ULIDs) and returns the created Create in the 2xx body; the
        // caller reads <see cref="DeliveryResult.MintedId"/> (the Create's id) and the returned object's
        // id (the note's id).
        Assert.False(root.TryGetProperty("id", out _), "the client must not set the Create's id (server is the authority)");
        Assert.False(note.TryGetProperty("id", out _), "the client must not set the embedded note's id (server is the authority)");
    }

    [Fact]
    public async Task PostNoteAsync_WithAudience_SetsNoteTo()
    {
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = new ActivityPubClient(new HttpClient(fake));

        var author = new Iri("https://a.domain.local/u/alice");
        var publicIri = new Iri("https://www.w3.org/ns/activitystreams#Public");
        await client.PostNoteAsync(author, "public post", to: [publicIri]);

        using var doc = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(fake.LastBody));
        var note = doc.RootElement.GetProperty("object");
        Assert.Equal(publicIri.Value, note.GetProperty("to").GetString());
    }

    [Fact]
    public async Task PostNoteAsync_InboxReturnsNotAccepted_PropagatesStatusCode()
    {
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.BadRequest));
        var client = new ActivityPubClient(new HttpClient(fake));

        var result = await client.PostNoteAsync(new Iri("https://a.domain.local/u/alice"), "hello");

        // The raw delivery status is surfaced so the caller can react (e.g. 400 malformed post).
        Assert.Equal(400, result.StatusCode);
    }
}
