using System.Net;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Object = KristofferStrube.ActivityStreams.Object;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 22.6.2 integration tests: the typed <see cref="IrisDocumentExtensions"/> readers for the
/// un-prefixed collection-endpoint extensions the server advertises on the public actor/community
/// document. The server puts <c>feed</c>, <c>blocks</c>, <c>flags</c>, <c>mutes</c>, <c>star</c>
/// (relays) on a person document, and <c>members</c>, <c>feed</c> (and <c>search</c>) on a community
/// document. A client fetching the document can read each IRI directly (no hardcoded endpoint paths) via
/// <c>GetFeedIri()</c>, <c>GetMembersIri()</c>, <c>GetBlocksIri()</c>, <c>GetFlagsIri()</c>,
/// <c>GetMutesIri()</c>, and <c>GetRelaysIri()</c>.
/// </summary>
public sealed class IrisDocumentExtensionReaderTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string Community = "devs";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _aliceIri;
    private readonly Iri _communityIri;

    public IrisDocumentExtensionReaderTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        var aliceSeeded = TestSeeder.SeedPersonWithKey(_persistence, AHost, Alice);
        _aliceIri = aliceSeeded.ActorIri;

        var communitySeeded = TestSeeder.SeedCommunityWithKey(_persistence, AHost, Community);
        _communityIri = communitySeeded.CommunityIri;

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _persistence,
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- Person document: the un-prefixed collection-endpoint readers ------------------------

    [Fact]
    public async Task PersonDoc_Readers_ReadCollectionEndpoints()
    {
        var doc = await FetchDocumentAsync(_aliceIri);

        AssertIri(doc.GetFeedIri(), $"{_aliceIri.Value}/feed", "feed");
        AssertIri(doc.GetBlocksIri(), $"{_aliceIri.Value}/blocks", "blocks");
        AssertIri(doc.GetFlagsIri(), $"{_aliceIri.Value}/flags", "flags");
        AssertIri(doc.GetMutesIri(), $"{_aliceIri.Value}/mutes", "mutes");
        AssertIri(doc.GetRelaysIri(), $"{_aliceIri.Value}/relays", "star (relays)");

        // members is community-only: absent on a person document.
        Assert.Null(doc.GetMembersIri());
    }

    // --- Community document: members + feed + moderation readers ----------------------------

    [Fact]
    public async Task CommunityDoc_Readers_ReadMembersAndFeed()
    {
        var doc = await FetchDocumentAsync(_communityIri);

        // members is community-only: present here, absent on a person document (see the person test).
        AssertIri(doc.GetMembersIri(), $"{_communityIri.Value}/members", "members");
        AssertIri(doc.GetFeedIri(), $"{_communityIri.Value}/feed", "feed");

        // A community also advertises its moderation collections (19.5.4), mirroring the person document.
        AssertIri(doc.GetBlocksIri(), $"{_communityIri.Value}/blocks", "blocks");
        AssertIri(doc.GetFlagsIri(), $"{_communityIri.Value}/flags", "flags");
        AssertIri(doc.GetMutesIri(), $"{_communityIri.Value}/mutes", "mutes");
    }

    // --- Absent-term safety: a bare object returns nulls, not throws -------------------------

    [Fact]
    public void Readers_BareObject_ReturnNulls()
    {
        var bare = new Object { Id = "https://a.domain.local/ap/v1/u/x" };

        Assert.Null(bare.GetFeedIri());
        Assert.Null(bare.GetMembersIri());
        Assert.Null(bare.GetBlocksIri());
        Assert.Null(bare.GetFlagsIri());
        Assert.Null(bare.GetMutesIri());
        Assert.Null(bare.GetRelaysIri());
    }

    // --- Helpers ------------------------------------------------------------------------

    private async Task<Object> FetchDocumentAsync(Iri iri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, iri.Value);
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return ActivityJson.Deserialize<Object>(body)!;
    }

    private static void AssertIri(Iri? actual, string expected, string label)
    {
        Assert.NotNull(actual);
        Assert.True(
            string.Equals(actual!.ToString(), expected, StringComparison.Ordinal),
            $"the {label} IRI mismatch: expected {expected}, got {actual}");
    }
}
