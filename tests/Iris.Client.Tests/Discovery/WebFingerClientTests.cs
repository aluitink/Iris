using System.Net;
using System.Text;
using Iris.Core;

namespace Iris.Client.Tests.Discovery;

/// <summary>
/// Unit tests for <see cref="Iris.Client.Discovery.WebFingerClient"/> (implements
/// <see cref="Iris.Client.Discovery.IDiscoveryService"/> via
/// <see cref="Iris.Client.Discovery.WebFingerDiscoveryService"/>).
/// </summary>
public class WebFingerClientTests
{
    private const string ActorIri = "https://b.domain.local/u/bob";

    private static string SelfDocument(string href, string? type = "application/activity+json")
    {
        var typeField = type is null ? string.Empty : $$""" "type": "{{type}}", """;
        return $$"""
                {
                  "subject": "acct:bob@b.domain.local",
                  "links": [
                    { "rel": "self", {{typeField}} "href": "{{href}}" }
                  ]
                }
                """;
    }

    private static async Task<Iri?> ResolveAsync(string account, HttpResponseMessage response)
    {
        var client = new WebFingerClient(new HttpClient(new FakeHttpHandler(response)));
        return await client.ResolveActorAsync(account);
    }

    [Fact]
    public void NormalizeSubject_AtPrefixed_ReturnsAcctUri()
        => Assert.Equal("acct:bob@b.domain.local", WebFingerClient.NormalizeSubject("@bob@b.domain.local"));

    [Fact]
    public void NormalizeSubject_Bare_ReturnsAcctUri()
        => Assert.Equal("acct:bob@b.domain.local", WebFingerClient.NormalizeSubject("bob@b.domain.local"));

    [Fact]
    public void NormalizeSubject_AcctPrefixed_IsUnchanged()
        => Assert.Equal("acct:bob@b.domain.local", WebFingerClient.NormalizeSubject("acct:bob@b.domain.local"));

    [Fact]
    public void NormalizeSubject_NoAtSign_Throws()
        => Assert.Throws<ArgumentException>(() => WebFingerClient.NormalizeSubject("justbob"));

    [Fact]
    public async Task Resolve_FindsSelfLink_ReturnsActorIri()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SelfDocument(ActorIri), Encoding.UTF8, WebFingerClient.WebFingerContentType),
        };

        var iri = await ResolveAsync("@bob@b.domain.local", response);

        Assert.NotNull(iri);
        Assert.Equal(ActorIri, iri!.Value.Value);
    }

    [Fact]
    public async Task Resolve_SelfLinkWithoutType_IsAccepted()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SelfDocument(ActorIri, type: null), Encoding.UTF8, WebFingerClient.WebFingerContentType),
        };

        var iri = await ResolveAsync("@bob@b.domain.local", response);
        Assert.Equal(ActorIri, iri!.Value.Value);
    }

    [Fact]
    public async Task Resolve_NoSelfLink_ReturnsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"subject":"acct:bob@b.domain.local","links":[{"rel":"http://webfinger.net/rel/profile","href":"https://b.domain.local"}]}""",
                Encoding.UTF8, WebFingerClient.WebFingerContentType),
        };

        Assert.Null(await ResolveAsync("@bob@b.domain.local", response));
    }

    [Fact]
    public async Task Resolve_NonSuccess_ReturnsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.Null(await ResolveAsync("@bob@b.domain.local", response));
    }
}
