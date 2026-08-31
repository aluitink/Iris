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
        return await client.ResolveActorAsync(account, dialScheme: "https");
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

    [Fact]
    public async Task Resolve_WithDialBaseUri_DialsExplicitAuthority_AndReturnsSelfLink()
    {
        // The S1 scenario: the address's host (localhost) is not browser-reachable, so the dial base
        // (a host-published port) must form the well-known URL's authority. The query resource still
        // carries the account's host; only the dialed authority changes.
        var handler = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SelfDocument(ActorIri), Encoding.UTF8, WebFingerClient.WebFingerContentType),
        });
        var client = new WebFingerClient(new HttpClient(handler));

        var iri = await client.ResolveActorAsync("alice@localhost", new Uri("http://localhost:8081"));

        // Dialed the explicit dial base authority (http://localhost:8081), not the address host (https://localhost).
        Assert.Equal("http://localhost:8081", handler.LastUri!.GetLeftPart(UriPartial.Authority));
        Assert.StartsWith("/.well-known/webfinger?", handler.LastUri.AbsolutePath + handler.LastUri.Query);
        Assert.Contains("resource=acct%3Aalice%40localhost", handler.LastUri.Query);
        Assert.Equal(ActorIri, iri!.Value.Value);
    }

    [Fact]
    public async Task Resolve_WithoutDialBaseUri_DialsAddressHostOverScheme()
    {
        // The RFC 8410 norm (no explicit dial base): the well-known URL's authority is the address's
        // own host, dialed over the given scheme.
        var handler = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SelfDocument(ActorIri), Encoding.UTF8, WebFingerClient.WebFingerContentType),
        });
        var client = new WebFingerClient(new HttpClient(handler));

        var iri = await client.ResolveActorAsync("@bob@b.domain.local", dialScheme: "http");

        Assert.Equal("http://b.domain.local", handler.LastUri!.GetLeftPart(UriPartial.Authority));
        Assert.Equal(ActorIri, iri!.Value.Value);
    }
}
