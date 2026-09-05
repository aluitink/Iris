using Iris.Client;
using Iris.Client.Collections;
using Iris.Client.Pipeline;
using Iris.Core.Collections;
using Iris.Core.Identity;
using Iris.Samples.SampleBlazorClient.Components;
using Bunit;
using KristofferStrube.ActivityStreams;
using CollectionPage = Iris.Core.Collections.CollectionPage;
using Iri = Iris.Core.Identity.Iri;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// bUnit component tests for <see cref="PagedCollection"/>: the render-lifecycle invariants the
/// browser-assisted sample review (31.2) confirmed live — most importantly that the initial
/// first-page load re-renders the card once the items arrive (no manual Refresh click required).
/// </summary>
public class PagedCollectionTests
{
    private const string CollectionIriValue = "https://a.domain.local/ap/v1/u/alice/outbox";

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
    /// A client whose <see cref="GetCollectionAsync"/> yields pages from a per-collection-IRI map
    /// (falling back to a default page set) and records the collection IRIs it was asked for.
    /// </summary>
    private sealed class PagedFakeClient : BaseFakeClient
    {
        private readonly IReadOnlyList<CollectionPage> _defaultPages;
        private readonly Dictionary<string, IReadOnlyList<CollectionPage>> _pagesByIri;

        public PagedFakeClient(
            IReadOnlyList<CollectionPage> defaultPages,
            IReadOnlyDictionary<string, IReadOnlyList<CollectionPage>>? pagesByIri = null)
        {
            _defaultPages = defaultPages;
            _pagesByIri = pagesByIri is null ? [] : pagesByIri.ToDictionary(
                kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<Iri> CollectionCalls { get; private set; } = [];

        public override IAsyncEnumerable<CollectionPage> GetCollectionAsync(
            Iri collectionId, CollectionQuery? query = null, CancellationToken ct = default)
        {
            CollectionCalls = [.. CollectionCalls, collectionId];
            var pages = _pagesByIri.TryGetValue(collectionId.Value, out var specific)
                ? specific
                : _defaultPages;
            return YieldPagesAsync(pages, ct);
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
    /// component's error state. (A synchronous throw before the await escapes the component's
    /// try/catch — the component awaits the enumeration, so the exception must surface while it is
    /// being consumed, mirroring a real network failure mid-request.)
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
            Id = "https://a.domain.local/ap/v1/u/alice/outbox/page/1",
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

    [Fact]
    public async Task InitialLoad_RendersItems_WithoutManualRefresh()
    {
        // 31.2: LoadInitialAsync is fired fire-and-forget from OnParametersSet (not an @onclick
        // handler, not awaited), so without an explicit StateHasChanged the render pass that
        // committed the "Loading…" spinner (Items == null) is never re-run after the items arrive
        // — the card is stuck on "Loading…" until the user clicks Refresh.
        var fake = new PagedFakeClient([BuildPage([
            new Note { Id = "https://a.domain.local/n/1", Content = ["first"] },
            new Note { Id = "https://a.domain.local/n/2", Content = ["second"] },
        ])]);

        using var ctx = new BunitContext();
        var cut = ctx.Render<PagedCollection>(parameters => parameters
            .Add(p => p.Client, fake)
            .Add(p => p.CollectionIri, new Iri(CollectionIriValue))
            .Add(p => p.Title, "Outbox"));

        // Let the fire-and-forget initial load run (its StateHasChanged re-renders the card; the
        // bUnit wait loop observes that re-render).
        await cut.WaitForAssertionAsync(() =>
        {
            var lis = cut.FindAll("ul.object-list > li");
            Assert.Equal(2, lis.Count);
        });
        var items = cut.FindAll("ul.object-list > li");
        Assert.Contains("first", items[0].InnerHtml);
        Assert.Contains("second", items[1].InnerHtml);

        Assert.DoesNotContain("Loading…", cut.Markup);
        Assert.Single(fake.CollectionCalls);
    }

    [Fact]
    public async Task InitialLoad_EmptyCollection_RendersEmptyState_WithoutManualRefresh()
    {
        // An empty first page must settle into the empty state (no items) — not spin forever on
        // "Loading…" — without a manual Refresh.
        var fake = new PagedFakeClient([BuildPage([])]);

        using var ctx = new BunitContext();
        var cut = ctx.Render<PagedCollection>(parameters => parameters
            .Add(p => p.Client, fake)
            .Add(p => p.CollectionIri, new Iri(CollectionIriValue)));

        await cut.WaitForAssertionAsync(() =>
        {
            Assert.DoesNotContain("Loading…", cut.Markup);
            Assert.Contains("No items.", cut.Markup);
        });
    }

    [Fact]
    public async Task InitialLoad_Failure_RendersError_WithoutManualRefresh()
    {
        // A failing fetch must settle into the error state (not spin forever) without a manual
        // Refresh: LoadInitialAsync's catch records LoadError and the StateHasChanged re-render
        // surfaces it.
        using var ctx = new BunitContext();
        var cut = ctx.Render<PagedCollection>(parameters => parameters
            .Add(p => p.Client, new ThrowingFakeClient())
            .Add(p => p.CollectionIri, new Iri(CollectionIriValue)));

        await cut.WaitForAssertionAsync(() =>
        {
            Assert.DoesNotContain("Loading…", cut.Markup);
            Assert.Contains("boom", cut.Markup);
        });
    }

    [Fact]
    public async Task NewCollectionIri_Reloads_FirstPage()
    {
        // Switching the CollectionIri (a new actor/community detail card) triggers a fresh
        // first-page load for the new collection.
        var fake = new PagedFakeClient(
            defaultPages: [BuildPage([new Note { Id = "https://a.domain.local/n/1", Content = ["old"] }])],
            pagesByIri: new Dictionary<string, IReadOnlyList<CollectionPage>>
            {
                ["https://a.domain.local/ap/v1/u/bob/outbox"] =
                    [BuildPage([new Note { Id = "https://a.domain.local/n/2", Content = ["new"] }])],
            });

        using var ctx = new BunitContext();
        var cut = ctx.Render<PagedCollection>(parameters => parameters
            .Add(p => p.Client, fake)
            .Add(p => p.CollectionIri, new Iri(CollectionIriValue)));

        await cut.WaitForAssertionAsync(() => Assert.Contains("old", cut.Markup));

        // bUnit 2.9 has no SetParametersAndRender: a second render with the new IRI is a fresh
        // component instance, which is the same code path the parent page uses (a new card per
        // collection IRI, e.g. navigating between actor detail pages).
        var cut2 = ctx.Render<PagedCollection>(parameters => parameters
            .Add(p => p.Client, fake)
            .Add(p => p.CollectionIri, new Iri("https://a.domain.local/ap/v1/u/bob/outbox")));

        await cut2.WaitForAssertionAsync(() => Assert.Contains("new", cut2.Markup));
        Assert.Equal(2, fake.CollectionCalls.Count);
    }
}
