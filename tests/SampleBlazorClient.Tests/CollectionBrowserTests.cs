using Iris.Client;
using Iris.Client.Collections;
using Iris.Client.Pipeline;
using Iris.Core.Collections;
using Iris.Core.Identity;
using Iris.Samples.SampleBlazorClient.Components;
using Bunit;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Components;
using CollectionPage = Iris.Core.Collections.CollectionPage;
using Iri = Iris.Core.Identity.Iri;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// bUnit component tests for <see cref="CollectionBrowser"/> (31.7): the shared ordered-collection
/// browser that walks pages and renders each item either via a registered per-type <see
/// cref="CollectionBrowser.ItemTemplate"/> or — when none is registered — via the built-in basic item
/// rendering, with optional per-item <see cref="CollectionBrowser.ItemActions"/> (e.g. a follower
/// "Block" button). Pins the invariants the hand-rolled follower/following lists and the ad-hoc
/// <c>PagedCollection</c> call sites relied on: the initial first-page load re-renders once items arrive,
/// the empty/error states settle without a manual refresh, and the item template / basic fallback /
/// per-item actions render as specified.
/// </summary>
public class CollectionBrowserTests
{
    private const string CollectionIriValue = "https://a.domain.local/ap/v1/u/alice/followers";

    private class BaseFakeClient : IActivityPubClient
    {
        public Task<IObject?> GetObjectAsync(Iri objectId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Actor?> GetActorAsync(Iri actorId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<NodeInfo?> GetNodeInfoAsync(Iri instanceBase, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> DeliverAsync(Iri targetId, IObject activity, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> FollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> UndoFollowAsync(Iri actorId, Iri originalFollowId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> AcceptAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> RejectAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> RequestJoinAsync(Iri actorId, Iri communityIri, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> AcceptJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> RejectJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> SetManuallyApprovesMembersAsync(Iri communityIri, bool enabled, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> SetManuallyApprovesFollowersAsync(Iri actorIri, bool enabled, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> LikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> UnlikeAsync(Iri actorId, Iri originalLikeId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> AnnounceAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> UnannounceAsync(Iri actorId, Iri originalAnnounceId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> DeleteAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> BlockAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> GetBlocksAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> UnblockAsync(Iri actorId, Iri originalBlockId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> FlagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> UnflagAsync(Iri actorId, Iri originalFlagId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> AddMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> RemoveMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> CreateCommunityAsync(Iri actorId, string name, string displayName, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> GetFlagsAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> GetMutesAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> GetRelaysAsync(Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> PostNoteAsync(Iri actorId, Note note, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DeliveryResult> PostReplyAsync(
            Iri actorId, Iri parentIri, string content,
            IEnumerable<Iri>? mentions = null, IEnumerable<Iri>? to = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> GetRepliesAsync(Iri objectIri, CollectionQuery? query = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> GetLikesAsync(Iri objectIri, CollectionQuery? query = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> GetSharesAsync(Iri objectIri, CollectionQuery? query = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> GetInboxItemsAsync(
            Iri actorId, ProxyCredentials credentials, CollectionQuery? query = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public virtual IAsyncEnumerable<CollectionPage> GetCollectionAsync(
            Iri collectionId, CollectionQuery? query = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> GetCollectionItemsAsync(
            Iri collectionId, CollectionQuery? query = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> GetCommunityFeedAsync(
            Iri communityId, CollectionQuery? query = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> GetFollowFeedAsync(
            Iri actorId, CollectionQuery? query = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IObjectOrLink> SearchAsync(
            Iri instanceBase, string? query = null, SearchOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A client whose <see cref="GetCollectionAsync"/> yields one page of items (optionally with a
    /// <c>next</c> pointer) and records the collection IRIs it was asked for.
    /// </summary>
    private sealed class PagedFakeClient : BaseFakeClient
    {
        private readonly IReadOnlyList<CollectionPage> _pages;

        public PagedFakeClient(IReadOnlyList<CollectionPage> pages)
        {
            _pages = pages;
        }

        public IReadOnlyList<Iri> CollectionCalls { get; private set; } = [];

        public override IAsyncEnumerable<CollectionPage> GetCollectionAsync(
            Iri collectionId, CollectionQuery? query = null, CancellationToken ct = default)
        {
            CollectionCalls = [.. CollectionCalls, collectionId];
            return YieldPagesAsync(_pages, ct);
        }

        private static async IAsyncEnumerable<CollectionPage> YieldPagesAsync(
            IReadOnlyList<CollectionPage> pages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var page in pages)
            {
                ct.ThrowIfCancellationRequested();
                yield return page;
            }
        }
    }

    /// <summary>
    /// A client whose <see cref="GetCollectionAsync"/> throws *during enumeration*, to drive the
    /// component's error state.
    /// </summary>
    private sealed class ThrowingFakeClient : BaseFakeClient
    {
        public override IAsyncEnumerable<CollectionPage> GetCollectionAsync(
            Iri collectionId, CollectionQuery? query = null, CancellationToken ct = default)
            => ThrowOnMoveNextAsync();

        private static async IAsyncEnumerable<CollectionPage> ThrowOnMoveNextAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.FromException(new InvalidOperationException("boom"));
            yield break;
        }
    }

    private static CollectionPage BuildPage(IReadOnlyList<IObjectOrLink> items, Iri? nextPage = null)
    {
        var page = new OrderedCollectionPage
        {
            Id = "https://a.domain.local/ap/v1/u/alice/followers/page/1",
            PartOf = new Link { Href = new Uri(CollectionIriValue) },
            Items = items,
        };

        if (nextPage is not null)
        {
            page.Next = new Link { Href = nextPage.Value.Uri };
        }

        return new CollectionPage
        {
            Page = page,
            Items = items,
            NextPage = nextPage,
            PageId = new Iri(page.Id),
        };
    }

    /// <summary>
    /// The actor-link item renderer (the "registered per-type template"): each entry renders as a link to
    /// that actor's detail page showing the short handle.
    /// </summary>
    private static RenderFragment<IObjectOrLink> ActorLinkTemplate = item => builder =>
    {
        string? iri = item is IObject { Id: { Length: > 0 } id } ? id
            : item is ILink { Href: { } href } ? href.ToString()
            : null;

        if (iri is not null)
        {
            var idx = iri.LastIndexOf('/');
            var handle = idx >= 0 && idx < iri.Length - 1 ? iri[(idx + 1)..] : iri;
            builder.OpenElement(0, "a");
            builder.AddAttribute(1, "href", $"/actor?iri={Uri.EscapeDataString(iri)}");
            builder.AddAttribute(2, "title", iri);
            builder.AddContent(3, handle);
            builder.CloseElement();
        }
    };

    [Fact]
    public async Task ItemTemplate_WhenProvided_RendersCustomMarkup_NotBasicFallback()
    {
        // 31.7: a registered per-type template wins over the built-in basic rendering — the follower
        // renders as an actor-link (the template), not the basic /object link + type label.
        var fake = new PagedFakeClient([BuildPage([
            new Person { Id = "https://a.domain.local/ap/v1/u/bob", PreferredUsername = "bob" },
        ])]);

        using var ctx = new BunitContext();
        var cut = ctx.Render<CollectionBrowser>(parameters => parameters
            .Add(p => p.Client, fake)
            .Add(p => p.CollectionIri, new Iri(CollectionIriValue))
            .Add(p => p.Title, "Followers")
            .Add(p => p.ItemTemplate, ActorLinkTemplate));

        await cut.WaitForAssertionAsync(() =>
        {
            var link = cut.Find("ul.object-list > li a");
            Assert.Equal("/actor?iri=https%3A%2F%2Fa.domain.local%2Fap%2Fv1%2Fu%2Fbob", link.GetAttribute("href"));
            Assert.Equal("bob", link.TextContent.Trim());
        });
        // The basic fallback (a /object link) is NOT rendered when a template is registered.
        Assert.DoesNotContain("/object?iri=", cut.Markup);
    }

    [Fact]
    public async Task NoItemTemplate_RendersBasicFallback_ForActor()
    {
        // 31.7: when no per-type template is registered, the built-in basic rendering is the fallback —
        // an actor renders as a link to its actor detail (the short handle) plus a muted type label.
        var fake = new PagedFakeClient([BuildPage([
            new Person { Id = "https://a.domain.local/ap/v1/u/bob", PreferredUsername = "bob" },
        ])]);

        using var ctx = new BunitContext();
        var cut = ctx.Render<CollectionBrowser>(parameters => parameters
            .Add(p => p.Client, fake)
            .Add(p => p.CollectionIri, new Iri(CollectionIriValue))
            .Add(p => p.Title, "Followers"));

        await cut.WaitForAssertionAsync(() =>
        {
            var li = cut.Find("ul.object-list > li");
            var link = cut.Find("ul.object-list > li a");
            Assert.Equal("/actor?iri=https%3A%2F%2Fa.domain.local%2Fap%2Fv1%2Fu%2Fbob", link.GetAttribute("href"));
            Assert.Equal("bob", link.TextContent.Trim());
            // The basic fallback appends a muted type label (Person).
            Assert.Contains("Person", li.TextContent);
        });
    }

    [Fact]
    public async Task NoItemTemplate_RendersBasicFallback_ForBareLink()
    {
        // 31.7: a bare link (no id, no object) renders its IRI as <code> under the basic fallback.
        var fake = new PagedFakeClient([BuildPage([
            new Link { Href = new Uri("https://a.domain.local/ap/v1/u/carla") },
        ])]);

        using var ctx = new BunitContext();
        var cut = ctx.Render<CollectionBrowser>(parameters => parameters
            .Add(p => p.Client, fake)
            .Add(p => p.CollectionIri, new Iri(CollectionIriValue)));

        await cut.WaitForAssertionAsync(() =>
        {
            var code = cut.Find("ul.object-list > li code");
            Assert.Equal("https://a.domain.local/ap/v1/u/carla", code.TextContent.Trim());
        });
    }

    [Fact]
    public async Task ItemActions_RendersPerItemControls()
    {
        // 31.7: the ItemActions fragment renders per-item controls (e.g. a follower "Block" button)
        // after the item's content — this is how the owner-only management action is now supplied to the
        // shared browser instead of a hand-rolled list.
        var fake = new PagedFakeClient([BuildPage([
            new Person { Id = "https://a.domain.local/ap/v1/u/bob", PreferredUsername = "bob" },
        ])]);

        RenderFragment<IObjectOrLink> actions = item => builder =>
        {
            if (item is IObject { Id: { Length: > 0 } })
            {
                builder.OpenElement(0, "button");
                builder.AddAttribute(1, "class", "block-follower");
                builder.AddContent(2, "Block");
                builder.CloseElement();
            }
        };

        using var ctx = new BunitContext();
        var cut = ctx.Render<CollectionBrowser>(parameters => parameters
            .Add(p => p.Client, fake)
            .Add(p => p.CollectionIri, new Iri(CollectionIriValue))
            .Add(p => p.ItemTemplate, ActorLinkTemplate)
            .Add(p => p.ItemActions, actions));

        await cut.WaitForAssertionAsync(() =>
        {
            var button = cut.Find("ul.object-list > li button.block-follower");
            Assert.Equal("Block", button.TextContent.Trim());
            // The item link and the action button coexist in the same <li>.
            Assert.NotNull(cut.Find("ul.object-list > li a"));
        });
    }

    [Fact]
    public async Task InitialLoad_RendersItems_WithoutManualRefresh()
    {
        // The initial first-page load re-renders the card once the items arrive (no manual Refresh).
        var fake = new PagedFakeClient([BuildPage([
            new Person { Id = "https://a.domain.local/ap/v1/u/bob", PreferredUsername = "bob" },
            new Person { Id = "https://a.domain.local/ap/v1/u/carla", PreferredUsername = "carla" },
        ])]);

        using var ctx = new BunitContext();
        var cut = ctx.Render<CollectionBrowser>(parameters => parameters
            .Add(p => p.Client, fake)
            .Add(p => p.CollectionIri, new Iri(CollectionIriValue))
            .Add(p => p.ItemTemplate, ActorLinkTemplate));

        await cut.WaitForAssertionAsync(() =>
        {
            var lis = cut.FindAll("ul.object-list > li");
            Assert.Equal(2, lis.Count);
        });
        Assert.DoesNotContain("Loading…", cut.Markup);
        Assert.Single(fake.CollectionCalls);
    }

    [Fact]
    public async Task InitialLoad_EmptyCollection_RendersEmptyState_WithoutManualRefresh()
    {
        var fake = new PagedFakeClient([BuildPage([])]);

        using var ctx = new BunitContext();
        var cut = ctx.Render<CollectionBrowser>(parameters => parameters
            .Add(p => p.Client, fake)
            .Add(p => p.CollectionIri, new Iri(CollectionIriValue))
            .Add(p => p.EmptyMessage, "No followers recorded."));

        await cut.WaitForAssertionAsync(() =>
        {
            Assert.DoesNotContain("Loading…", cut.Markup);
            Assert.Contains("No followers recorded.", cut.Markup);
        });
    }

    [Fact]
    public async Task InitialLoad_Failure_RendersError_WithoutManualRefresh()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<CollectionBrowser>(parameters => parameters
            .Add(p => p.Client, new ThrowingFakeClient())
            .Add(p => p.CollectionIri, new Iri(CollectionIriValue)));

        await cut.WaitForAssertionAsync(() =>
        {
            Assert.DoesNotContain("Loading…", cut.Markup);
            Assert.Contains("boom", cut.Markup);
        });
    }
}
