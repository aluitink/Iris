using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Inbox;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Integration tests for 138.12 (Lemmy community relay): when a Lemmy community relays a member's
/// post (an <c>Announce</c> whose object is the member's <c>Create</c>, which embeds a <c>Page</c>),
/// the post surfaces in the Iris community feed (the embedded object is stored in the object store and
/// recorded in the community's local members' outboxes).
/// </summary>
public sealed class LemmyCommunityRelayIntegrationTests
{
    private const string AHost = "a.domain.local";
    private const string Bob = "bob";
    private const string ACommunity = "inter";

    private readonly InMemoryPersistenceProvider _persistence;
    private readonly InboxProcessor _processor;
    private readonly Iri _bobActorIri;
    private readonly Iri _aCommunityIri;

    public LemmyCommunityRelayIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        // Seed bob (a member of the community).
        var aSeeded = TestSeeder.SeedPersonWithKey(_persistence, AHost, Bob);
        _bobActorIri = aSeeded.ActorIri;

        // Seed the community, with bob as a local member (so the inbound relayed post is recorded in
        // bob's outbox by CommunityContentRecorder).
        var (_, aCommunityIri, _) = TestSeeder.SeedCommunityWithKey(
            _persistence, AHost, ACommunity, memberIri: _bobActorIri);
        _aCommunityIri = aCommunityIri;

        // Build the InboxProcessor with the AnnounceActivityHandler.
        var queue = new InMemoryDeliveryQueue();
        var delivery = new DeliveryService(queue, NullLogger<DeliveryService>.Instance);
        var localActors = new DefaultLocalActorResolver(_persistence);
        var handler = new AnnounceActivityHandler(_persistence, delivery, localActors);
        _processor = new InboxProcessor(_persistence, [handler]);
    }

    /// <summary>
    /// A Lemmy-style <c>Announce(Create(Page))</c> delivered to the community's inbox stores the
    /// embedded Page in the object store and records the Create in the community's local member's
    /// outbox (so the post surfaces in the community feed).
    /// </summary>
    [Fact]
    public async Task LemmyCommunityRelay_AnnounceWithEmbeddedCreate_StoresPageAndRecordsInMemberOutbox()
    {
        // The Lemmy community (remote) relays a member's post: an Announce whose object is the member's
        // Create, which embeds a Page.
        var pageIri = new Iri($"https://lemmy.luit.ink/post/1");
        var createIri = new Iri($"https://lemmy.luit.ink/c/interop/123");
        var announceIri = new Iri($"https://lemmy.luit.ink/activities/announce/create/51a65ff0");
        var lemmyCommunityIri = new Iri($"https://lemmy.luit.ink/c/interop");
        var lemmyMemberIri = new Iri($"https://lemmy.luit.ink/u/lemmyadmin");

        var page = new Page
        {
            Id = pageIri.Value,
            Name = ["Hello from Lemmy"],
            Content = ["<p>World</p>"],
            AttributedTo = [new Link { Href = lemmyMemberIri.Uri }],
            Published = DateTime.UtcNow,
        };

        var create = new Create
        {
            Id = createIri.Value,
            Actor = [new Link { Href = lemmyMemberIri.Uri }],
            Object = [page],
            Published = DateTime.UtcNow,
        };

        var announce = new Announce
        {
            Id = announceIri.Value,
            Actor = [new Link { Href = lemmyCommunityIri.Uri }],
            Object = [create],
        };

        // Deliver the Announce to the community's inbox (the recipient is the local community).
        var delivery = new InboxDelivery(_aCommunityIri, announce);

        await _processor.ProcessAsync(delivery, CancellationToken.None);

        // (a) The Page is stored in the object store.
        Assert.True(
            await _persistence.Objects.TryGetObjectAsync(pageIri, out var storedPage),
            "The relayed Page should be stored in the object store");
        Assert.IsType<Page>(storedPage);
        var storedPageAsPage = (Page)storedPage;
        Assert.NotNull(storedPageAsPage.Name);
        Assert.Contains("Hello from Lemmy", storedPageAsPage.Name);

        // (b) The Create is recorded in bob's (the community's local member) outbox, so the post
        // surfaces in the community feed.
        var bobOutbox = (await _persistence.Activities.GetOutboxAsync(_bobActorIri)).ToList();
        Assert.Contains(bobOutbox, a =>
            a is Create c && c.Object?.OfType<Page>().Any(p => p.Id == pageIri.Value) == true);
    }

    /// <summary>
    /// A plain <c>Announce</c> (whose object is a bare object IRI, not an embedded <c>Create</c>) does
    /// NOT store the object in the object store (it's a boost/re-share, not a community relay).
    /// </summary>
    [Fact]
    public async Task PlainAnnounce_ObjectIsBareIri_DoesNotStoreInObjectStore()
    {
        // A plain Announce (boost): the object is a bare IRI (a link), not an embedded Create.
        var objectIri = new Iri($"https://lemmy.luit.ink/post/1");
        var announceIri = new Iri($"https://lemmy.luit.ink/activities/announce-2");
        var lemmyCommunityIri = new Iri($"https://lemmy.luit.ink/c/interop");

        var announce = new Announce
        {
            Id = announceIri.Value,
            Actor = [new Link { Href = lemmyCommunityIri.Uri }],
            Object = [new Link { Href = objectIri.Uri }],
        };

        var delivery = new InboxDelivery(_aCommunityIri, announce);

        await _processor.ProcessAsync(delivery, CancellationToken.None);

        // The object is NOT stored in the object store (it's a boost, not a community relay).
        Assert.False(
            await _persistence.Objects.TryGetObjectAsync(objectIri, out _),
            "A plain Announce (bare IRI object) should NOT store the object in the object store");
    }

    /// <summary>
    /// The <c>Announce</c> itself is still recorded in the community's outbox (the relay envelope),
    /// even when it wraps an embedded <c>Create</c>.
    /// </summary>
    [Fact]
    public async Task LemmyCommunityRelay_AnnounceIsAlsoRecordedInCommunityOutbox()
    {
        var pageIri = new Iri($"https://lemmy.luit.ink/post/2");
        var createIri = new Iri($"https://lemmy.luit.ink/c/interop/124");
        var announceIri = new Iri($"https://lemmy.luit.ink/activities/announce/create/51a65ff1");
        var lemmyCommunityIri = new Iri($"https://lemmy.luit.ink/c/interop");
        var lemmyMemberIri = new Iri($"https://lemmy.luit.ink/u/lemmyadmin");

        var page = new Page
        {
            Id = pageIri.Value,
            Content = ["<p>Another post</p>"],
            AttributedTo = [new Link { Href = lemmyMemberIri.Uri }],
        };

        var create = new Create
        {
            Id = createIri.Value,
            Actor = [new Link { Href = lemmyMemberIri.Uri }],
            Object = [page],
        };

        var announce = new Announce
        {
            Id = announceIri.Value,
            Actor = [new Link { Href = lemmyCommunityIri.Uri }],
            Object = [create],
        };

        var delivery = new InboxDelivery(_aCommunityIri, announce);

        await _processor.ProcessAsync(delivery, CancellationToken.None);

        // The Announce is recorded in the community's outbox.
        var communityOutbox = (await _persistence.Activities.GetOutboxAsync(_aCommunityIri)).ToList();
        Assert.Contains(communityOutbox, a => a is Announce { Id: var id } && id == announceIri.Value);
    }

    /// <summary>
    /// 138.19 (shares/boosts interop) — the relay-unwrap path correctly attributes the content to the
    /// underlying <c>Create</c>'s actor (the original Lemmy member), NOT to the relaying community
    /// (the <c>Announce</c>'s actor). The community-feed merge path shows the original author, and the
    /// UI renders a <c>LemmyVoteBar</c> (upvote/downvote/score) instead of the <c>EngagementBar</c>
    /// (which carries the Boost button), so no boost affordance is offered for Lemmy-sourced content.
    /// </summary>
    [Fact]
    public async Task LemmyCommunityRelay_ContentAttributedToOriginalAuthor_NotRelayingCommunity()
    {
        var pageIri = new Iri($"https://lemmy.luit.ink/post/3");
        var createIri = new Iri($"https://lemmy.luit.ink/c/interop/125");
        var announceIri = new Iri($"https://lemmy.luit.ink/activities/announce/create/51a65ff2");
        var lemmyCommunityIri = new Iri($"https://lemmy.luit.ink/c/interop");
        var lemmyMemberIri = new Iri($"https://lemmy.luit.ink/u/lemmyadmin");

        var page = new Page
        {
            Id = pageIri.Value,
            Name = ["Original author's post"],
            Content = ["<p>Content</p>"],
            AttributedTo = [new Link { Href = lemmyMemberIri.Uri }],
        };

        var create = new Create
        {
            Id = createIri.Value,
            Actor = [new Link { Href = lemmyMemberIri.Uri }],
            Object = [page],
        };

        var announce = new Announce
        {
            Id = announceIri.Value,
            Actor = [new Link { Href = lemmyCommunityIri.Uri }],
            Object = [create],
        };

        var delivery = new InboxDelivery(_aCommunityIri, announce);

        await _processor.ProcessAsync(delivery, CancellationToken.None);

        // The Create in bob's outbox is attributed to the original Lemmy member (the Create's actor),
        // NOT to the relaying community (the Announce's actor).
        var bobOutbox = (await _persistence.Activities.GetOutboxAsync(_bobActorIri)).ToList();
        var recordedCreate = bobOutbox.OfType<Create>().FirstOrDefault(
            c => c.Object?.OfType<Page>().Any(p => p.Id == pageIri.Value) == true);
        Assert.NotNull(recordedCreate);

        var actorRef = recordedCreate!.Actor?.FirstOrDefault();
        Assert.NotNull(actorRef);
        var actorIri = actorRef?.ResolveObjectIri();
        Assert.Equal(lemmyMemberIri.Value, actorIri?.ToString());
    }
}
