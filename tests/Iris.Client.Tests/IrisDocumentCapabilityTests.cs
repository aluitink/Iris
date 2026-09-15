using System.Text.Json;
using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Client.Tests;

/// <summary>
/// Unit tests for the client's server-capability detection and capability-aware feed/members IRI
/// resolution (137.2): <see cref="IrisDocumentExtensions.IsLemmy"/>,
/// <see cref="IrisDocumentExtensions.ResolveFeedIri"/>, <see cref="IrisDocumentExtensions.ResolveMembersIri"/>,
/// and <see cref="IrisDocumentExtensions.IsActivityFeed"/>. A remote community may be served by a server
/// that exposes different collection routes than Iris: an Iris instance advertises its feed/members
/// endpoints on the document, a Mastodon/Pleroma-shaped server serves <c>/feed</c> + <c>/members</c>, and a
/// <em>Lemmy</em> community serves neither (its posts live in <c>/outbox</c>, its members in
/// <c>/followers</c>). The client reads the community's own document and resolves the routes by what the
/// server supports, so the UI can browse a Lemmy community.
/// </summary>
public class IrisDocumentCapabilityTests
{
    private const string CommunityIri = "https://lemmy.luit.ink/c/interop";
    private static readonly Iri Iri = new(CommunityIri);

    // --- Lemmy/Group detection (a Group whose content is in its outbox) -----------------

    [Fact]
    public void IsLemmy_GroupWithOutbox_ReturnsTrue()
    {
        var doc = LemmyGroup();
        Assert.True(doc.IsLemmy());
    }

    [Fact]
    public void IsLemmy_GroupWithoutOutbox_ReturnsFalse()
    {
        var doc = new Group { Id = CommunityIri };
        Assert.False(doc.IsLemmy());
    }

    [Fact]
    public void IsLemmy_PersonActor_ReturnsFalse()
    {
        var doc = new Person { Id = CommunityIri };
        Assert.False(doc.IsLemmy());
    }

    [Fact]
    public void IsLemmy_DeserializedLemmyDocument_ReturnsTrue()
    {
        // The real-world path: the document arrives via JSON deserialization (the @context is stripped
        // by the deserializer, so detection must rely on the Group.Outbox shape, not @context).
        var json = """{"@context":["https://join-lemmy.org/context.json","https://www.w3.org/ns/activitystreams"],"type":"Group","id":"https://lemmy.luit.ink/c/interop","preferredUsername":"interop","name":"Iris Interop","outbox":"https://lemmy.luit.ink/c/interop/outbox","followers":"https://lemmy.luit.ink/c/interop/followers"}""";
        var doc = ActivityJson.Deserialize<Group>(json);
        Assert.NotNull(doc);
        Assert.True(doc!.IsLemmy());
    }

    [Fact]
    public void IsLemmy_IrisGroupWithDefaultNamespace_ReturnsFalse()
    {
        // An Iris community advertises iris:-namespaced extension properties. Even though it is a
        // Group with an Outbox (like a Lemmy Group), the presence of '#' -containing extension keys
        // identifies it as Iris, so IsLemmy() returns false.
        var doc = IrisGroupWithFeed(namespaceIri: IrisDocumentExtensions.DefaultNamespaceIri);
        Assert.False(doc.IsLemmy());
    }

    [Fact]
    public void IsLemmy_IrisGroupWithCustomNamespace_ReturnsFalse()
    {
        // The real-world regression: a deployment that overrides NamespaceIri (e.g.
        // https://iris.luit.ink/ns#) still advertises iris:-namespaced extensions, but under a
        // different namespace base than DefaultNamespaceIri. Detection must not rely on matching a
        // fixed namespace — it must scan for any '#' -containing extension key.
        var doc = IrisGroupWithFeed(namespaceIri: "https://iris.luit.ink/ns#");
        Assert.False(doc.IsLemmy());
    }

    [Fact]
    public void ResolveFeedIri_IrisDocumentWithCustomNamespaceUsesFeedConvention()
    {
        // The critical regression (137.2): a deployment that overrides NamespaceIri advertises
        // iris:feed under the custom namespace, but GetFeedIri(DefaultNamespaceIri) cannot find it
        // (namespace mismatch). The old code fell through to IsLemmy() (which returned true for any
        // Group+Outbox) and resolved the feed to /outbox. The fix: IsLemmy() now returns false when
        // the document has any '#' -containing extension key, so ResolveFeedIri falls through to the
        // Mastodon/Pleroma /feed convention.
        var doc = IrisGroupWithFeed(namespaceIri: "https://iris.luit.ink/ns#");

        var feed = doc.ResolveFeedIri(Iri);
        Assert.Equal($"{CommunityIri}/feed", feed.Value);
        Assert.False(doc.IsActivityFeed(Iri));
    }

    [Fact]
    public void ResolveFeedIri_IrisDocumentWithDefaultNamespaceUsesAdvertisedFeed()
    {
        // When the namespace matches DefaultNamespaceIri, GetFeedIri finds the advertised iris:feed
        // extension and returns it directly (step 1 of ResolveFeedIri).
        var doc = IrisGroupWithFeed(namespaceIri: IrisDocumentExtensions.DefaultNamespaceIri);

        var feed = doc.ResolveFeedIri(Iri);
        Assert.Equal($"{CommunityIri}/feed", feed.Value);
        Assert.False(doc.IsActivityFeed(Iri));
    }

    // --- Feed resolution ---------------------------------------------------------------

    [Fact]
    public void ResolveFeedIri_IrisDocumentUsesAdvertisedIrisFeed()
    {
        // An Iris community advertises iris:feed on the document (the only feed that also supports ?q=).
        var doc = new Group { Id = CommunityIri };
        doc.ExtensionData ??= new Dictionary<string, JsonElement>();
        doc.ExtensionData[IrisDocumentExtensions.DefaultNamespaceIri + CollectionExtensionNames.Feed] =
            StringElement($"{CommunityIri}/feed");

        var feed = doc.ResolveFeedIri(Iri);
        Assert.Equal($"{CommunityIri}/feed", feed.Value);
        Assert.False(doc.IsActivityFeed(Iri));
    }

    [Fact]
    public void ResolveFeedIri_LemmyDocumentUsesOutbox()
    {
        var doc = LemmyGroup();

        var feed = doc.ResolveFeedIri(Iri);
        Assert.Equal($"{CommunityIri}/outbox", feed.Value);
        Assert.True(doc.IsActivityFeed(Iri));
    }

    [Fact]
    public void ResolveFeedIri_MastodonDocumentUsesFeedConvention()
    {
        // No iris: extension and not Lemmy → the Mastodon/Pleroma /feed convention.
        var doc = new Group { Id = CommunityIri };

        var feed = doc.ResolveFeedIri(Iri);
        Assert.Equal($"{CommunityIri}/feed", feed.Value);
        Assert.False(doc.IsActivityFeed(Iri));
    }

    // --- Members resolution ------------------------------------------------------------

    [Fact]
    public void ResolveMembersIri_IrisDocumentUsesAdvertisedMembers()
    {
        var doc = new Group { Id = CommunityIri };
        doc.ExtensionData ??= new Dictionary<string, JsonElement>();
        doc.ExtensionData[CollectionExtensionNames.Members] = StringElement($"{CommunityIri}/members");

        Assert.Equal($"{CommunityIri}/members", doc.ResolveMembersIri(Iri).Value);
    }

    [Fact]
    public void ResolveMembersIri_LemmyDocumentUsesFollowers()
    {
        var doc = LemmyGroup();

        Assert.Equal($"{CommunityIri}/followers", doc.ResolveMembersIri(Iri).Value);
    }

    [Fact]
    public void ResolveMembersIri_MastodonDocumentUsesMembersConvention()
    {
        var doc = new Group { Id = CommunityIri };

        Assert.Equal($"{CommunityIri}/members", doc.ResolveMembersIri(Iri).Value);
    }

    // --- IsActivityFeed (drives the UI's content-item filter) --------------------------

    [Fact]
    public void IsActivityFeed_TrueOnlyForLemmy()
    {
        Assert.True(LemmyGroup().IsActivityFeed(Iri));
        Assert.False(new Group { Id = CommunityIri }.IsActivityFeed(Iri));

        var iris = new Group { Id = CommunityIri };
        iris.ExtensionData ??= new Dictionary<string, JsonElement>();
        iris.ExtensionData[IrisDocumentExtensions.DefaultNamespaceIri + CollectionExtensionNames.Feed] =
            StringElement($"{CommunityIri}/feed");
        Assert.False(iris.IsActivityFeed(Iri));
    }

    // --- Helpers -----------------------------------------------------------------------

    /// <summary>
    /// A minimal Lemmy community document: a <c>Group</c> whose <c>outbox</c>/<c>followers</c> are
    /// present (the AP-standard shape for a community: its posts are in its outbox, its members are
    /// its followers).
    /// </summary>
    private static Group LemmyGroup()
    {
        return new Group
        {
            Id = CommunityIri,
            Outbox = new Link { Href = new Uri($"{CommunityIri}/outbox") },
            Followers = new Link { Href = new Uri($"{CommunityIri}/followers") },
        };
    }

    /// <summary>
    /// An Iris community document: a <c>Group</c> with an <c>outbox</c> (like a Lemmy Group) that also
    /// advertises the <c>iris:feed</c> extension under the given <paramref name="namespaceIri"/>. This
    /// is the shape the real Iris server produces — a Group with both an AP-standard outbox and
    /// Iris-specific, namespaced extension properties.
    /// </summary>
    private static Group IrisGroupWithFeed(string namespaceIri)
    {
        var doc = new Group
        {
            Id = CommunityIri,
            Outbox = new Link { Href = new Uri($"{CommunityIri}/outbox") },
            ExtensionData = new Dictionary<string, JsonElement>
            {
                [namespaceIri + CollectionExtensionNames.Feed] = StringElement($"{CommunityIri}/feed"),
            },
        };
        return doc;
    }

    private static JsonElement StringElement(string value) => JsonSerializer.SerializeToElement(value);
}
