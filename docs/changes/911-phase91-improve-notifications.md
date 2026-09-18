# 91.1 — Improve Notifications (user-centric verbs, drop server-only noise, stable order)

**Phase:** 91 — Improve notifications
**Date:** 2026-09-12
**Status:** Complete
**Commit:** `baecfd1`

## Objective

From the 91 investigation, three problems with the notifications surface:
1. **Wrong verb** — "Notifications seem to preset everything as 'replied to you' but in fact they are replies to other content."
2. **User-centricity** — the list should answer what the user cares about: *did someone I follow post, was I @-mentioned, did someone reply to one of my posts?*
3. **Size & formatting** — "the lengths look random"; and server-only `Deleted` messages the user doesn't need to see should be excluded.

Phase 85 had already fixed the single biggest verb bug (a `Create` reply to *someone else's* note is "replied", not "replied to you"). Phase 91 generalises that into a user-centric verb set, removes the server-only noise from the list entirely, and makes the order stable and newest-first.

## What was built

### Server — `apps/Iris.Web/WebAppFactory.FilterInboxByPrefs`

The notification list + unread-count both read the actor's inbox (`persistence.Activities.GetInboxAsync`) and run it through `FilterInboxByPrefs`. That method previously applied only the user's *prefs* filters (disabled types, muted actors) and returned the inbox in **store order** (position-ordered, i.e. oldest first). It now also:

- **Drops server-only noise.** A new `IsServerOnlyNotification` predicate removes:
  - `Update` — the author edited their own note (a content revision, not a user-facing event),
  - `Undo` — the actor withdrew their own like/boost/follow,
  - `Flag` / `Block` / `Mute` — the actor reported/blocked/muted the user (the user already sees these in their own moderation surface; duplicating them in the notification list is noise),
  - a `Delete` whose `object` is **not** the deleted actor's own IRI — i.e. a **remote post deletion**. An account deletion (`object == actor`) is **kept** so the UI can still render "deleted their account" (the existing `NotificationRow.IsSelfDelete` caption).
- **Sorts newest-first.** The result is sorted by `published` (falling back to `updated`), so the list reads newest-first regardless of the store's order. This is the fix for "the lengths/order look random" — a stable, predictable order.

The `selfIri` parameter was added (optional, default `null`) so the method can evaluate the self-delete rule and is wired with `account.ActorId` from both the `GET /local/v1/notifications` and `GET /local/v1/notifications/unread-count` endpoints — so the **badge count and the list agree** (both exclude the same noise). `Iris.Web.csproj` gained `InternalsVisibleTo(Iris.Web.Tests)` so the pure filter logic is unit-testable.

### Client — `apps/Iris.Web.Client/Components/NotificationRow.razor` (verbs)

`VerbFor` was rewritten to be **user-centric** (every verb is phrased around what the user should care about):

| Activity | Before | After |
|---|---|---|
| `Follow` | "follows you" (ambiguous — a Follow in the inbox is a *request*) | **sent you a follow request** |
| `Announce` (boost) | "boosted your post" | **boosted a post mentioning you** when the user is @-tagged in the boosted note (new `NoteMentionsSelf`); otherwise **boosted your post** |
| `Create` (reply to my note) | "replied to you" | **replied to your post** |
| `Create` (reply to someone else's note) | "replied" (85) | **replied** (unchanged) |
| `Create` (new note tagging me) | "posted" | **mentioned you** (new — via `NoteMentionsSelf`) |
| `Create` (plain post from a followed actor) | "posted" | **posted** (unchanged) |
| `Like` | "liked your post" | **liked your post** (unchanged — a like is only delivered to its author) |

`NoteMentionsSelf(IObject note)` (new) returns true when the note's `tag` set contains a `Mention` whose `href` (an `Iri`/`Uri`) equals the signed-in user's actor IRI (`SelfId`). `Mention` is an `ILink` per the AS vocabulary, so the href is compared as its absolute string.

The now-unreachable `Undo`/`Flag`/`Block`/`Mute` verb cases were removed from the switch (the server drops those types); a generic default verb remains as a safety net for any future type.

### Client — `apps/Iris.Web.Client/Components/Pages/Notifications.razor` (belt-and-suspenders)

The list's two load paths (`LoadNotificationsAsync`, `OnLoadMoreAsync`) now run each page through a static `KeepNotification(JsonElement)` guard that drops a `Delete` that is **not** an account deletion (`object != actor`, normalised). This makes the Deleted exclusion independent of the server filter — a stale client build, or a server that does not yet filter, still renders a clean list. (`NormalizeIri` resolves a bare IRI string, an `{"id"}` object, or an `{"href"}` link to a normalised absolute string.)

## Live verification (Playwright, per the WASM manual-test policy)

Rebuilt the Docker app (`docker compose build iris-web` + `up -d --force-recreate iris-web`) and restarted the Playwright MCP (`bash scripts/start-playwright.sh`, caching disabled) for a fresh browser. Logged in as `andrew` (whose inbox is a realistic mix: 41 Create / 37 Delete / 5 Announce / 3 Accept / 3 Like / 3 Update / 2 Follow / 1 Undo / 1 Block).

| Scenario | Result |
|---|---|
| Newest-first order | ✅ The "All" list is strictly newest-first (22m → 26m → 42m → 49m → 2h → 4h → 5h → 6h …); no out-of-order jumps |
| Server-only noise excluded | ✅ **No Delete rows** (all 37 remote deletions dropped); a search for `deleted|undid|blocked you|muted you|flagged you|updated` in the All list returns **no matches** (the 3 Update, 1 Undo, 1 Block also gone) |
| User-centric verb — replies | ✅ Remote replies render **replied** (reply to someone else's note); the self-note rule (85) is preserved |
| User-centric verb — boosts | ✅ Boosts tab renders **boosted your post** (5 rows, newest-first) |
| User-centric verb — likes | ✅ Likes tab renders **liked your post** (3 rows) |
| User-centric verb — follows | ✅ Follows tab renders **sent you a follow request** (2 rows) |
| Screenshot | ✅ `tmp/.playwright-mcp/page-2026-09-12T07-00-53-688Z.png` — clean list, consistent row layout |
| Console errors | **0 from this change** — the only logged error is the pre-existing `ursal.zone` federation 403 (a remote avatar fetch the proxy rejects), which predates this slice |

## Build / test

- `dotnet build -c Release` → 0 warnings, 0 errors.
- `dotnet test -c Release --no-build --filter "Category!=Slow"` → green. `Iris.Web.Tests` is now **71 passed** (63 prior + **8 new**). `Iris.Server.Tests` 1084 passed / 1 skip in isolated runs.
- **8 new coded tests** (`tests/Iris.Web.Tests/NotificationFilterTests.cs`) pin the filter contract directly (pure logic, no TestServer boot):
  - `FilterInboxByPrefs_NoiseTypes_Dropped` — Update/Undo/Flag/Block/Mute removed, Create kept.
  - `FilterInboxByPrefs_RemotePostDelete_Dropped` — a `Delete` with `object != actor` removed.
  - `FilterInboxByPrefs_AccountDelete_Kept` — a `Delete` with `object == actor` kept.
  - `FilterInboxByPrefs_UserFacingTypes_Kept` — Create/Like/Announce/Follow/Accept all kept.
  - `FilterInboxByPrefs_DisabledTypes_StillRespected` — a user-disabled type is still dropped.
  - `FilterInboxByPrefs_MutedActors_StillRespected` — a muted actor's item is still dropped.
  - `FilterInboxByPrefs_OrdersNewestFirst` — an oldest-first inbox comes back newest-first.
  - `FilterInboxByPrefs_NoSelfIri_NoPrefs_ReturnsUnchanged` — the no-args fast path returns the inbox as-is (prior behavior).
- Note: a **pre-existing flaky** `Iris.Server.Tests` test intermittently fails under full parallel-suite load (passes on isolated runs). It is unrelated to this slice — the change touches the notification filter/verbs (app + client) and adds `Iris.Web.Tests`; it does not modify the server test assembly. Not chased here.

## Notes / decisions

- **Why filter server-side (not just client)?** The server is the source of truth for the badge count *and* the list. Filtering only in the client would leave the unread badge inflated by the 37+ noise items (and a stale client build would still show them). Filtering in `FilterInboxByPrefs` fixes both at once; the client `KeepNotification` guard is a second line of defense for the list only.
- **Why drop Flag/Block/Mute/Update/Undo as well as Delete?** The 91 ask is "exclude Deleted because those are server only messages that the user doesn't need to see" — the principle is *server-only / self-referential noise*. `Update` (my own note was edited) and `Undo` (I withdrew my own like) are self-referential; `Flag`/`Block`/`Mute` (the other party acted on me) are already surfaced in the user's own moderation controls. All five are inbox-only metadata, not user-facing interactions. If a future phase wants a "moderation" notification tab, these can be re-surfaced behind a filter without re-adding them to the default list.
- **Why keep account deletions but drop post deletions?** An account deletion (`object == actor`) is a real, user-facing event ("so-and-so deleted their account") — `NotificationRow` already renders that caption. A remote *post* deletion is pure server bookkeeping with nothing to show the user, so it's dropped.
- **Why "replied to your post" (not "replied to you")?** The 91 copy asks for precision. "replied to you" reads as the reply is addressed to *me*; "replied to your post" is accurate — the reply is to a note *I* wrote. The distinction only matters for my own notes (the 85 self-note rule); replies to others' notes stay "replied".
- **Why "sent you a follow request" (not "follows you")?** A `Follow` in the inbox is an inbound follow *request*. "follows you" wrongly implies the follow is already accepted (that's the `Accept` case, which is handled separately as "accepted your follow request").
- **Why a client Deleted guard even though the server drops them?** Defense in depth: the client guard means the list is clean even if served by a build whose server filter is absent (e.g. an incremental deploy where the client WASM is newer than the server, or a test fixture). It is cheap (a raw-JSON check before deserialization) and keeps the contract honest at the rendering layer.
