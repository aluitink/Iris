using System.Text.Json;
using Iris.Core;
using Iris.Core.Collections;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Core.Tests;

/// <summary>
/// Wire-format edge-case + conformance tests (Phase 81.3). Covers the ActivityPub document shapes
/// that are easy to miss but required for robust federation:
///
/// <list type="bullet">
///   <item><see cref="Tombstone"/> — a deleted object: deserializes to the <see cref="Tombstone"/>
///   class with <c>formerType</c>/<c>deleted</c> populated, and survives a round-trip (Iris serves a
///   stored tombstone under the original IRI, so it must deserialize + re-emit cleanly).</item>
///   <item><see cref="Delete"/> activity — deserializes (via the polymorphic converter) with
///   <c>actor</c>/<c>object</c> resolving, and survives a round-trip (Iris both sends <c>Delete</c>
///   over the wire and accepts it inbound).</item>
///   <item>Outbox/inbox pagination — a real <c>Mastodon</c> <see cref="OrderedCollection"/> (page 1,
///   with <c>first</c>/<c>last</c>/<c>totalItems</c>) and a real <see cref="OrderedCollectionPage"/>
///   (page N, with <c>next</c>/<c>prev</c>/<c>partOf</c>/<c>orderedItems</c>) both deserialize to the
///   correct concrete types with the navigation links resolving.</item>
///   <item>Collection edge cases — an empty collection (<c>items: []</c>, <c>totalItems: 0</c>) and a
///   single-item collection (<c>totalItems: 1</c>) deserialize cleanly (the single-item case is the
///   one the library's one-or-multiple converter is known to collapse, so it is the risky edge).</item>
/// </list>
///
/// The Mastodon outbox documents are genuine fixtures captured in Phase 81.1
/// (<c>mastodon-outbox.json</c>, <c>mastodon-outbox-page1.json</c>); the tombstone/delete/edge documents
/// are representative literals in the exact wire shape Iris emits.
/// </summary>
public class WireFormatEdgeCaseTests
{
    private static string ReadFixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "InteropFixtures", name));

    /// <summary>
    /// A <see cref="Tombstone"/> (a deleted object) deserializes to the <see cref="Tombstone"/> class,
    /// with <c>formerType</c> + <c>deleted</c> populated and the original object IRI preserved, and
    /// survives a serialize round-trip (Iris stores + serves a tombstone under the deleted object's
    /// IRI, so a peer that fetches it must deserialize cleanly, not crash).
    /// </summary>
    [Fact]
    public void Tombstone_DeserializesAndRoundTrips()
    {
        var json = """
        {
            "@context": "https://www.w3.org/ns/activitystreams",
            "id": "https://iris.example/ap/v1/objects/note-123",
            "type": "Tombstone",
            "formerType": ["Note"],
            "deleted": "2026-01-15T10:30:00Z"
        }
        """;

        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(json)!;

        Assert.IsType<Tombstone>(payload);
        var tombstone = (Tombstone)payload;

        Assert.Equal("https://iris.example/ap/v1/objects/note-123", tombstone.Id);
        Assert.NotNull(tombstone.FormerType);
        Assert.Contains("Note", tombstone.FormerType!);
        Assert.NotNull(tombstone.Deleted);

        var reserialized = ActivityJson.Serialize(payload);
        using var doc = JsonDocument.Parse(reserialized);
        Assert.Equal("Tombstone", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("https://iris.example/ap/v1/objects/note-123", doc.RootElement.GetProperty("id").GetString());
        Assert.Contains("formerType", reserialized);
        Assert.Contains("Note", reserialized);
    }

    /// <summary>
    /// A <see cref="Delete"/> activity deserializes (via the polymorphic converter — <c>Delete</c> is an
    /// activity, not an object, so it is reached by casting the <see cref="IObjectOrLink"/> range
    /// result) with <c>actor</c> + <c>object</c> resolving to IRIs, and survives a round-trip. Iris
    /// sends <c>Delete</c> to a deleted object's remote audience and accepts it inbound.
    /// </summary>
    [Fact]
    public void DeleteActivity_DeserializesAndRoundTrips()
    {
        var json = """
        {
            "@context": "https://www.w3.org/ns/activitystreams",
            "id": "https://iris.example/ap/v1/activities/delete-1",
            "type": "Delete",
            "actor": "https://iris.example/ap/v1/u/andrew",
            "object": "https://iris.example/ap/v1/objects/note-123",
            "to": ["https://www.w3.org/ns/activitystreams#Public"],
            "cc": ["https://iris.example/ap/v1/u/andrew/followers"]
        }
        """;

        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(json)!;

        // Delete is an Activity (not an IObject) — the polymorphic converter produces the concrete
        // Delete, reachable by casting the IObjectOrLink range result (the dead-letter-store pattern).
        Assert.IsType<Delete>(payload);
        var delete = (Delete)payload;

        Assert.Equal("https://iris.example/ap/v1/activities/delete-1", delete.Id);
        Assert.NotNull(delete.Actor);
        Assert.Equal("https://iris.example/ap/v1/u/andrew", delete.Actor!.FirstOrDefault()?.ResolveObjectIri()?.Value);
        Assert.NotNull(delete.Object);
        Assert.Equal("https://iris.example/ap/v1/objects/note-123", delete.Object!.FirstOrDefault()?.ResolveObjectIri()?.Value);

        var reserialized = ActivityJson.Serialize(payload);
        using var doc = JsonDocument.Parse(reserialized);
        Assert.Equal("Delete", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("https://iris.example/ap/v1/objects/note-123", doc.RootElement.GetProperty("object").GetString());
    }

    /// <summary>
    /// A real <c>Mastodon</c> <see cref="OrderedCollection"/> (outbox page 1) deserializes to the
    /// <see cref="OrderedCollection"/> class with <c>totalItems</c> populated and <c>first</c>/<c>last</c>
    /// resolving to IRIs. This is the document a peer's outbox endpoint returns first; the
    /// <c>last</c> link is present (Mastodon emits it) and must deserialize — locking in that Iris's
    /// own outbox now emitting <c>last</c> (81.3) is symmetric with what it already consumes.
    /// </summary>
    [Fact]
    public void OrderedCollection_Page1_DeserializesWithFirstLastAndTotalItems()
    {
        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(ReadFixture("mastodon-outbox.json"))!;

        Assert.IsType<OrderedCollection>(payload);
        var collection = (OrderedCollection)payload;

        Assert.Equal("https://mastodon.online/users/Gargron/outbox", collection.Id);
        Assert.Equal(425u, collection.TotalItems);

        // first + last resolve to IRIs (Mastodon emits both on the collection document).
        Assert.Equal("https://mastodon.online/users/Gargron/outbox?page=true", collection.First?.ResolveCollectionIri()?.Value);
        Assert.Equal("https://mastodon.online/users/Gargron/outbox?min_id=0&page=true", collection.Last?.ResolveCollectionIri()?.Value);
    }

    /// <summary>
    /// A real <c>Mastodon</c> <see cref="OrderedCollectionPage"/> (outbox page N) deserializes to the
    /// <see cref="OrderedCollectionPage"/> class with <c>next</c>/<c>prev</c>/<c>partOf</c> resolving to
    /// IRIs and <c>orderedItems</c> carrying the page's activities. This is the mid-collection page a
    /// client walks via <c>next</c>; the navigation links + item list must deserialize.
    /// </summary>
    [Fact]
    public void OrderedCollectionPage_PageN_DeserializesWithNextPrevPartOfAndOrderedItems()
    {
        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(ReadFixture("mastodon-outbox-page1.json"))!;

        Assert.IsType<OrderedCollectionPage>(payload);
        var page = (OrderedCollectionPage)payload;

        Assert.Equal("https://mastodon.online/users/Gargron/outbox?page=true", page.Id);
        Assert.Equal("https://mastodon.online/users/Gargron/outbox", page.PartOf?.ResolveCollectionIri()?.Value);

        // next + prev resolve to IRIs (cursor-based max_id/min_id links).
        Assert.NotNull(page.Next);
        Assert.NotNull(page.Prev);
        Assert.Contains("max_id=", page.Next!.ResolveCollectionIri()?.Value);
        Assert.Contains("min_id=", page.Prev!.ResolveCollectionIri()?.Value);

        // orderedItems carries the page's activities (Mastodon puts items under orderedItems, not items).
        Assert.NotNull(page.OrderedItems);
        Assert.Equal(20, page.OrderedItems!.Count());
    }

    /// <summary>
    /// An empty collection (<c>items: []</c>, <c>totalItems: 0</c>) deserializes cleanly to an
    /// <see cref="OrderedCollection"/> with no items — the edge a fresh/empty outbox hits (a brand-new
    /// account, or a filtered query that matches nothing).
    /// </summary>
    [Fact]
    public void OrderedCollection_Empty_DeserializesCleanly()
    {
        var json = """
        {
            "@context": "https://www.w3.org/ns/activitystreams",
            "id": "https://iris.example/ap/v1/u/newuser/outbox",
            "type": "OrderedCollection",
            "items": [],
            "totalItems": 0,
            "first": "https://iris.example/ap/v1/u/newuser/outbox"
        }
        """;

        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(json)!;

        Assert.IsType<OrderedCollection>(payload);
        var collection = (OrderedCollection)payload;

        Assert.Equal(0u, collection.TotalItems);
        // An empty items list deserializes to an empty (or null) collection — neither should throw.
        var itemCount = collection.Items?.Count() ?? collection.OrderedItems?.Count() ?? 0;
        Assert.Equal(0, itemCount);

        // Round-trips (an empty outbox is a real, fetchable document).
        var reserialized = ActivityJson.Serialize(payload);
        using var doc = JsonDocument.Parse(reserialized);
        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
    }

    /// <summary>
    /// <see cref="CollectionPageFactory.ResolveCollectionItems"/> reads a page's items from
    /// <c>orderedItems</c> (the ActivityPub canonical form, used by Mastodon and other major
    /// implementations) when present. This is the shared helper the client's outbox/feed AND inbox read
    /// paths now both route through (81.3 fixed the inbox path, which previously read only <c>items</c>
    /// and would have dropped a page served with <c>orderedItems</c>) — locking in the canonical-form
    /// preference keeps the two paths symmetric.
    /// </summary>
    [Fact]
    public void ResolveCollectionItems_ReadsOrderedItemsWhenPresent()
    {
        var json = """
        {
            "@context": "https://www.w3.org/ns/activitystreams",
            "id": "https://remote.example/inbox?page=true",
            "type": "OrderedCollectionPage",
            "partOf": "https://remote.example/inbox",
            "orderedItems": [
                {
                    "id": "https://remote.example/activities/follow-1",
                    "type": "Follow",
                    "actor": "https://remote.example/u/alice",
                    "object": "https://remote.example/u/bob"
                }
            ],
            "next": "https://remote.example/inbox?page=2"
        }
        """;

        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(json)!;
        Assert.IsType<OrderedCollectionPage>(payload);
        var page = (OrderedCollectionPage)payload;

        var items = CollectionPageFactory.ResolveCollectionItems(page);
        Assert.Single(items);
        Assert.Equal("https://remote.example/activities/follow-1", items[0].ResolveObjectIri()?.Value);
    }

    /// <summary>
    /// <see cref="CollectionPageFactory.ResolveCollectionItems"/> falls back to <c>items</c> when
    /// <c>orderedItems</c> is absent (a less common but legal wire shape), and returns an empty list
    /// when neither is present — so a page served in either form yields its items.
    /// </summary>
    [Fact]
    public void ResolveCollectionItems_FallsBackToItemsAndEmptyWhenNeither()
    {
        // items-only page (no orderedItems) -> items are returned.
        var itemsOnly = """
        {
            "@context": "https://www.w3.org/ns/activitystreams",
            "id": "https://remote.example/outbox",
            "type": "OrderedCollection",
            "items": [
                {
                    "id": "https://remote.example/activities/create-1",
                    "type": "Create",
                    "actor": "https://remote.example/u/bob",
                    "object": "https://remote.example/objects/note-1"
                }
            ],
            "totalItems": 1
        }
        """;
        var itemsOnlyPage = (OrderedCollection)ActivityJson.Deserialize<IObjectOrLink>(itemsOnly)!;
        Assert.Single(CollectionPageFactory.ResolveCollectionItems(itemsOnlyPage));

        // Neither orderedItems nor items -> empty list (not null, not a throw).
        var empty = """
        {
            "@context": "https://www.w3.org/ns/activitystreams",
            "id": "https://remote.example/outbox",
            "type": "OrderedCollection",
            "totalItems": 0
        }
        """;
        var emptyPage = (OrderedCollection)ActivityJson.Deserialize<IObjectOrLink>(empty)!;
        Assert.Empty(CollectionPageFactory.ResolveCollectionItems(emptyPage));
    }

    /// <summary>
    /// A single-item collection (<c>items</c> with exactly one entry, <c>totalItems: 1</c>) deserializes
    /// cleanly. This is the edge the ActivityStreams library's one-or-multiple converter is known to
    /// collapse (a single-element collection may be read back as a bare value rather than a one-item
    /// list), so it is the riskiest collection shape to verify.
    /// </summary>
    [Fact]
    public void OrderedCollection_SingleItem_DeserializesCleanly()
    {
        var json = """
        {
            "@context": "https://www.w3.org/ns/activitystreams",
            "id": "https://iris.example/ap/v1/u/andrew/outbox",
            "type": "OrderedCollection",
            "items": [
                {
                    "id": "https://iris.example/ap/v1/activities/create-1",
                    "type": "Create",
                    "actor": "https://iris.example/ap/v1/u/andrew",
                    "object": "https://iris.example/ap/v1/objects/note-1"
                }
            ],
            "totalItems": 1,
            "first": "https://iris.example/ap/v1/u/andrew/outbox"
        }
        """;

        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(json)!;

        Assert.IsType<OrderedCollection>(payload);
        var collection = (OrderedCollection)payload;

        Assert.Equal(1u, collection.TotalItems);
        // Exactly one item, whether the converter surfaces it via Items or OrderedItems.
        var items = collection.Items ?? collection.OrderedItems;
        Assert.NotNull(items);
        Assert.Single(items!);
    }
}
