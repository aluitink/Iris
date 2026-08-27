using System.Net;
using System.Text;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Tests;

/// <summary>
/// Unit tests for <see cref="IActivityPubClientFactory"/> / <see cref="ActivityPubClientFactory"/>:
/// it composes a signing pipeline (<see cref="SigningHandler"/> over the transport) and the produced
/// client is usable end-to-end.
/// </summary>
public class ActivityPubClientFactoryTests
{
    private const string ActorIri = "https://b.domain.local/u/bob";
    private const string KeyId = "https://b.domain.local/u/bob#main-key";
    private const string PersonJson =
        """
        {"id":"https://b.domain.local/u/bob","type":"Person","name":"Bob","preferredUsername":"bob","inbox":"https://b.domain.local/u/bob/inbox"}
        """;

    /// <summary>
    /// A key store + provider + factory wired to a single actor, kept alive for the test.
    /// </summary>
    private sealed record Fixture(IKeyStore Store, Iri ActorIri, ActivityPubClientFactory Factory)
    {
        public ActivityPubClientOptions Options { get; } = new() { ActorId = ActorIri };
    }

    private static Fixture CreateFixture()
    {
        var store = new InMemoryKeyStore();
        store.PutKey(KeyPairGenerator.GenerateRsa(new Iri(KeyId)));

        var actorIri = new Iri(ActorIri);
        var provider = new InMemoryKeyProvider(store);
        provider.RegisterKey(actorIri, new Iri(KeyId));

        return new Fixture(store, actorIri, new ActivityPubClientFactory(store, provider));
    }

    [Fact]
    public void Create_ReturnsIActivityPubClient()
    {
        var fixture = CreateFixture();

        IActivityPubClient client = fixture.Factory.Create(fixture.Options,
            new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)));

        Assert.NotNull(client);
        Assert.IsType<ActivityPubClient>(client);
    }

    [Fact]
    public void Create_MissingActorId_Throws()
    {
        var fixture = CreateFixture();

        Assert.Throws<ArgumentException>(() => fixture.Factory.Create(new ActivityPubClientOptions(),
            new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK))));
    }

    [Fact]
    public async Task Create_GetObject_SignsRequestAndResolvesObject()
    {
        var fixture = CreateFixture();

        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(PersonJson, Encoding.UTF8, ActivityJson.ActivityJsonContentType),
        });
        using var client = fixture.Factory.Create(fixture.Options, fake);

        var @object = await client.GetObjectAsync(fixture.ActorIri);

        Assert.NotNull(@object);
        Assert.True(fake.LastRequest!.Headers.Contains(Signatures.SignatureHeaderName));
        Assert.True(fake.LastRequest.Headers.Contains(Signatures.DateHeaderName));
        Assert.Equal(ActorIri, @object!.Id);
    }

    [Fact]
    public async Task Create_GetActor_ReturnsActorWithInbox()
    {
        var fixture = CreateFixture();

        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(PersonJson, Encoding.UTF8, ActivityJson.ActivityJsonContentType),
        });
        using var client = fixture.Factory.Create(fixture.Options, fake);

        var actor = await client.GetActorAsync(fixture.ActorIri);

        Assert.NotNull(actor);
        Assert.NotNull(actor!.Inbox);
        Assert.Equal("https://b.domain.local/u/bob/inbox", actor.Inbox!.Href!.ToString());
    }

    [Fact]
    public async Task Create_GetActor_NonActor_ReturnsNull()
    {
        var fixture = CreateFixture();

        const string noteJson = """{"id":"https://b.domain.local/n/1","type":"Note","content":"hi"}""";
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(noteJson, Encoding.UTF8, ActivityJson.ActivityJsonContentType),
        });
        using var client = fixture.Factory.Create(fixture.Options, fake);

        var actor = await client.GetActorAsync(fixture.ActorIri);

        Assert.Null(actor);
    }

    [Fact]
    public async Task Create_GetObject_SignatureVerifiesAgainstRegisteredKey()
    {
        var fixture = CreateFixture();

        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(PersonJson, Encoding.UTF8, ActivityJson.ActivityJsonContentType),
        });
        using var client = fixture.Factory.Create(fixture.Options, fake);

        await client.GetObjectAsync(fixture.ActorIri);

        // Rebuild the metadata from the recorded request and verify the signature using the SAME
        // key store that produced the key — proves the factory wired a real, correct signer.
        var request = fake.LastRequest!;
        var uri = request.RequestUri!;
        var date = request.Headers.GetValues(Signatures.DateHeaderName).Single();
        var signatureHeader = request.Headers.GetValues(Signatures.SignatureHeaderName).Single();

        var metadata = new HttpRequestMetadata(
            request.Method.Method.ToUpperInvariant(),
            uri.PathAndQuery,
            uri.Authority,
            date,
            null,
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Signatures.HostHeaderName] = uri.Authority,
                [Signatures.DateHeaderName] = date,
            });

        var verifier = new HttpSignatureVerifier(fixture.Store);

        Assert.True(verifier.Verify(metadata, signatureHeader));
    }
}
