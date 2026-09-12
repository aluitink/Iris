using Iris.Core.Identity;
using Iris.Server.Data.Accounts;
using Iris.Server.Inbox;
using Iris.Web;
using KristofferStrube.ActivityStreams;
using Xunit;

namespace Iris.Web.Tests;

/// <summary>
/// Unit tests for <see cref="WebAppFactory.FilterInboxByPrefs"/> — the server-side notification
/// filter (Phase 91). These pin the user-centric notification contract: server-only noise
/// (Update/Undo/Flag/Block/Mute + remote post deletions) is dropped, account deletions are kept,
/// user-facing activity is kept, and the result is ordered newest-first. <c>FilterInboxByPrefs</c>
/// is pure logic (no I/O), so it is exercised directly rather than through a full TestServer boot.
/// The method is <c>internal</c> to <c>Iris.Web</c> and visible here via <c>InternalsVisibleTo</c>.
/// </summary>
public sealed class NotificationFilterTests
{
    private const string Self = "https://self.test.local/ap/v1/u/alice";
    private const string Other = "https://other.test.local/users/bob";
    private const string Third = "https://third.test.local/users/cara";

    private static readonly Iri SelfIri = new(Self);
    private static readonly Iri OtherIri = new(Other);

    // A Create of a note (the note's content is irrelevant to the filter — only the activity type
    // and, for Delete, the object-vs-actor relationship matter).
    private static readonly Note Note = new()
    {
        Id = "https://self.test.local/ap/v1/u/alice/notes/n1",
        Content = ["hi"],
    };

    [Fact]
    public void FilterInboxByPrefs_NoiseTypes_Dropped()
    {
        var inbox = new List<IObjectOrLink>
        {
            CreateActivity(Note),
            UpdateActivity(Note),
            UndoActivity(LikeActivity(Note)),
            FlagActivity(Note),
            BlockActivity(new Person { Id = Self }),
            MuteActivity(new Person { Id = Self }),
        };

        var result = WebAppFactory.FilterInboxByPrefs(inbox, null, SelfIri);

        // Only the Create survives; Update/Undo/Flag/Block/Mute are server-only noise (Phase 91).
        Assert.Single(result);
        Assert.Equal("Create", TypeOf(result[0]));
    }

    [Fact]
    public void FilterInboxByPrefs_RemotePostDelete_Dropped()
    {
        // A Delete whose object is a remote post (object != actor) is server noise.
        var remoteDelete = new Delete
        {
            Id = "https://other.test.local/delete/d1",
            Actor = [new Person { Id = Other }],
            Object = [new Link { Href = new Uri("https://other.test.local/users/bob/statuses/123") }],
            Published = DateTime.UtcNow,
        };

        var inbox = new List<IObjectOrLink> { remoteDelete };

        var result = WebAppFactory.FilterInboxByPrefs(inbox, null, SelfIri);

        Assert.Empty(result);
    }

    [Fact]
    public void FilterInboxByPrefs_AccountDelete_Kept()
    {
        // A Delete whose object IS the actor's own IRI is an account deletion — kept so the UI can
        // render "deleted their account".
        var accountDelete = new Delete
        {
            Id = "https://other.test.local/delete/d2",
            Actor = [new Person { Id = Other }],
            Object = [new Link { Href = new Uri(Other) }],
            Published = DateTime.UtcNow,
        };

        var inbox = new List<IObjectOrLink> { accountDelete };

        var result = WebAppFactory.FilterInboxByPrefs(inbox, null, SelfIri);

        Assert.Single(result);
        Assert.Equal("Delete", TypeOf(result[0]));
    }

    [Fact]
    public void FilterInboxByPrefs_UserFacingTypes_Kept()
    {
        var inbox = new List<IObjectOrLink>
        {
            CreateActivity(Note),
            LikeActivity(Note),
            AnnounceActivity(Note),
            FollowActivity(),
            AcceptActivity(),
        };

        var result = WebAppFactory.FilterInboxByPrefs(inbox, null, SelfIri);

        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void FilterInboxByPrefs_DisabledTypes_StillRespected()
    {
        var prefs = new NotificationPreferences { DisabledTypes = ["Like"] };
        var inbox = new List<IObjectOrLink>
        {
            CreateActivity(Note),
            LikeActivity(Note),
        };

        var result = WebAppFactory.FilterInboxByPrefs(inbox, prefs, SelfIri);

        Assert.Single(result);
        Assert.Equal("Create", TypeOf(result[0]));
    }

    [Fact]
    public void FilterInboxByPrefs_MutedActors_StillRespected()
    {
        var prefs = new NotificationPreferences { MutedActors = { Other } };
        var inbox = new List<IObjectOrLink>
        {
            CreateActivityBy(Third, Note),   // the poster is a non-muted actor
            LikeActivity(Note),              // the liker is the muted actor
        };

        var result = WebAppFactory.FilterInboxByPrefs(inbox, prefs, SelfIri);

        // The Create (by the non-muted poster) survives; the Like (by the muted actor) is dropped.
        Assert.Single(result);
        Assert.Equal("Create", TypeOf(result[0]));
    }

    [Fact]
    public void FilterInboxByPrefs_OrdersNewestFirst()
    {
        // Deliberately stored oldest-first (the inbox store's position order); the filter must return
        // newest-first (Phase 91: a stable, non-"random" order).
        var oldest = CreateActivity(Note);
        oldest.Published = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newest = CreateActivity(Note);
        newest.Id = "https://self.test.local/ap/v1/u/alice/notes/n2";
        newest.Published = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        var inbox = new List<IObjectOrLink> { oldest, newest };

        var result = WebAppFactory.FilterInboxByPrefs(inbox, null, SelfIri);

        Assert.Equal(2, result.Count);
        Assert.Equal(newest, result[0]);
        Assert.Equal(oldest, result[1]);
    }

    [Fact]
    public void FilterInboxByPrefs_NoSelfIri_NoPrefs_ReturnsUnchanged()
    {
        // The fast path: with no prefs and no self IRI, the inbox is returned as-is (the prior
        // behavior) — including any noise the caller is responsible for handling.
        var inbox = new List<IObjectOrLink> { CreateActivity(Note), UpdateActivity(Note) };

        var result = WebAppFactory.FilterInboxByPrefs(inbox, null);

        Assert.Equal(2, result.Count);
    }

    /// <summary>
    /// The first element of an item's <c>Type</c> (an <see cref="IEnumerable{String}"/>) — the
    /// activity's concrete type. Returns null when the item is not an activity with a type.
    /// </summary>
    private static string? TypeOf(IObjectOrLink item) =>
        (item as Activity)?.Type?.FirstOrDefault();

    // --- activity factories (constructed per the 3rd-Party ActivityStreams rules: object
    // initializers, collection expressions; Type is set by each concrete type's constructor) ---

    private static Create CreateActivity(Note note) => CreateActivityBy(Other, note);

    private static Create CreateActivityBy(string actorIri, Note note) => new()
    {
        Id = $"https://self.test.local/ap/v1/u/alice/create/{Guid.NewGuid():N}",
        Actor = [new Person { Id = actorIri }],
        Object = [note],
        Published = DateTime.UtcNow,
    };

    private static Update UpdateActivity(Note note) => new()
    {
        Id = $"https://self.test.local/ap/v1/u/alice/update/{Guid.NewGuid():N}",
        Actor = [new Person { Id = Other }],
        Object = [note],
        Published = DateTime.UtcNow,
    };

    private static Undo UndoActivity(Like like) => new()
    {
        Id = $"https://self.test.local/ap/v1/u/alice/undo/{Guid.NewGuid():N}",
        Actor = [new Person { Id = Other }],
        Object = [like],
        Published = DateTime.UtcNow,
    };

    private static Flag FlagActivity(Note note) => new()
    {
        Id = $"https://self.test.local/ap/v1/u/alice/flag/{Guid.NewGuid():N}",
        Actor = [new Person { Id = Other }],
        Object = [note],
        Published = DateTime.UtcNow,
    };

    private static Block BlockActivity(Person person) => new()
    {
        Id = $"https://self.test.local/ap/v1/u/alice/block/{Guid.NewGuid():N}",
        Actor = [new Person { Id = Other }],
        Object = [person],
        Published = DateTime.UtcNow,
    };

    private static MuteActivity MuteActivity(Person person) => new()
    {
        Id = $"https://self.test.local/ap/v1/u/alice/mute/{Guid.NewGuid():N}",
        Actor = [new Person { Id = Other }],
        Object = [person],
        Published = DateTime.UtcNow,
    };

    private static Like LikeActivity(Note note) => new()
    {
        Id = $"https://self.test.local/ap/v1/u/alice/like/{Guid.NewGuid():N}",
        Actor = [new Person { Id = Other }],
        Object = [note],
        Published = DateTime.UtcNow,
    };

    private static Announce AnnounceActivity(Note note) => new()
    {
        Id = $"https://self.test.local/ap/v1/u/alice/announce/{Guid.NewGuid():N}",
        Actor = [new Person { Id = Other }],
        Object = [note],
        Published = DateTime.UtcNow,
    };

    private static Follow FollowActivity() => new()
    {
        Id = $"https://self.test.local/ap/v1/u/alice/follow/{Guid.NewGuid():N}",
        Actor = [new Person { Id = Other }],
        Object = [new Link { Href = new Uri(Self) }],
        Published = DateTime.UtcNow,
    };

    private static Accept AcceptActivity() => new()
    {
        Id = $"https://self.test.local/ap/v1/u/alice/accept/{Guid.NewGuid():N}",
        Actor = [new Person { Id = Self }],
        Object = [FollowActivity()],
        Published = DateTime.UtcNow,
    };
}
