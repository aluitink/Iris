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

        var status = await client.DeliverAsync(new Iri(InboxIri), follow);

        Assert.Equal(202, status);
        Assert.Equal(HttpMethod.Post, fake.LastRequest!.Method);
        Assert.Equal(InboxIri, fake.LastUri!.ToString());
        var contentType = fake.LastRequest.Content!.Headers.ContentType!.MediaType;
        Assert.Equal(ActivityJson.ActivityJsonContentType, contentType);
        var body = Encoding.UTF8.GetString(fake.LastBody);
        Assert.Contains("\"Follow\"", body);
    }

    [Fact]
    public async Task DeliverAsync_NonActivity_Throws()
    {
        var client = new ActivityPubClient(new HttpClient(new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK))));
        var person = new Person { Id = ActorIri };

        await Assert.ThrowsAsync<ArgumentException>(() => client.DeliverAsync(new Iri(InboxIri), person));
    }
}
