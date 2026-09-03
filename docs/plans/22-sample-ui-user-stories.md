# Phase 22 — Sample UI: user stories + per-component usage map

This is the detailed plan that Phase 22's umbrella item (22.0) unpacks into. The goal is **not** a
beautiful UI — it is a **more functional explorer tool**: better components, better support for
viewing/reviewing activities and objects, and better support for interacting with other servers.

The workflow this document drives:

1. The stories below are the **broad** view. They are intentionally high level; each one names the
   components it touches.
2. As we work each story we **deepen it into a component-level plan**: a deeper analysis of each
   component involved (its current shape, its inputs/outputs, the client/server seams it uses, the
   failure/empty states it must handle) and how it will be used. That deeper plan lands in its own
   document under `docs/plans/22-*.md`, and the main roadmap (22.0) gains a reference to it.
3. Each story is **implemented then manually tested** with the Playwright MCP tools; gaps and errors
   found during that pass are resolved before the story is closed.
4. Before broad implementation begins, the full story set is reviewed **together** to confirm the
   stories play well with each other (shared components, no contradictions, consistent navigation and
   error/empty states).

The sample UI is **verified manually** (not bUnit-tested) while it remains in flux; the underlying
wire is proven by the existing server/client integration tests. Each story therefore lists the client
and server seams it exercises, so the manual pass can confirm the wire as well as the rendering.

---

## User stories

Each story is written as "As a user of the explorer, I want … so that …" and lists the components it
touches. The component list is the seed for the per-component deep-dive when we work the story.

### A. Logging on and managing instances

**US-1 — Log on to an instance by WebFinger address.**
As a user, I want to enter a WebFinger address (`alice@host`) and password (or use OAuth2) and have the
explorer resolve my actor, fetch the actor document, and establish a signed session, so that I can act
as that actor on that instance.
Components: `Home` (logon card), `ExplorerSession.LogOnAsync` / `LogOnWithOAuth2Async`,
`IActivityPubClient` (WebFinger + actor-document fetch), `Iri`/`ResolveActorIri`, the
`BaseUrls` config surface (known host → browser dial base).

**US-2 — Switch between instances without losing where I was.**
As a user, I want to switch the active instance (re-entering the password) and have the session
preserve my last-viewed object/actor ("continue where you left off") and the recent-instances list, so
that I can move between iris-a, iris-b, and a remote instance without re-navigating.
Components: `Home` (recent-instances card), `ExplorerSession.SwitchInstanceAsync` /
`RecentInstances` / `ClearNavigableState`, `MainLayout` (current-identity + instance indicator).

**US-3 — See which instance and actor I am currently acting as.**
As a user, I want the active identity and dial base always visible (not only on the Home page), so that
I know which server a read/write will hit before I act.
Components: `MainLayout` (top bar), `ExplorerSession` (current identity + dial base), `Instance` page
(current-instance marker).

### B. Discovering and browsing actors, communities, and objects

**US-4 — Find actors and communities.**
As a user, I want to search for local actors and communities (and, where supported, resolve a remote
handle via WebFinger), so that I can get from "I have a handle" to a rendered detail page.
Components: `Actors` (search), `IActivityPubClient` global search + WebFinger, `ResolveActorIri`,
`ActorDetail` / `Community` (deep-link targets).

**US-5 — Review an actor in full.**
As a user, I want an actor's detail page to show every collection that describes that actor — outbox
(paged), followers, following, inbox, moderation (mutes/blocks/flags), liked — each rendered as
clickable items, plus the actor's rendered fields (name, summary, icon, url) and the raw JSON, so that
I can fully review who that actor is and what they have done.
Components: `ActorDetail` (collection cards), `ObjectView` (item rendering), `IActivityPubClient`
(`GetCollectionAsync` paging, `GetActorAsync`), `IriExtensions` collection derivations
(`OutboxOf`/`InboxOf`/`FollowersOf`/`FollowingOf`/`MutesOf`/`BlocksOf`/`FlagsOf`/`LikesOf`/`SharesOf`),
raw-inspector toggle.

**US-6 — Review a community in full.**
As a user, I want a community's detail page to show its rendered fields, members (clickable), feed
(rendered, paged, filterable, refreshable), following/followers (clickable), moderation collections,
and its inbound-follows + membership + moderation management surfaces, so that I can review and manage
a community the same way I review an actor.
Components: `Community`, `ObjectView`, `IActivityPubClient` (`GetCommunityFeedAsync`,
`GetCollectionAsync`), `ILocalModerationClient` (community mute), `IActivityPubClient`
(`AddMemberAsync`/`RemoveMemberAsync`, community `AcceptAsync`/`RejectAsync`), raw-inspector toggle.

**US-7 — Review any object (note, image, group) in full.**
As a user, I want an object's detail page to render the object completely — type, author (clickable),
content (markdown/HTML, sanitized), audience (to/cc as links), published/updated, parent (in-reply-to,
clickable), mentions, attachments/media, sensitivity (behind a reveal), like/boost counts, reply chain,
and the canonical public URL when remote — so that I can review any object, local or remote, without
raw JSON.
Components: `ObjectPage`, `ObjectView`, `Markdown` renderer, `IriExtensions` reads
(`GetAudienceIris`/`IsSensitive`/`GetSummary`/`GetMediaAttachments`/`ResolveCanonicalPublicUrl`/
`LikesOf`/`SharesOf`), `IActivityPubClient` (`GetObjectAsync`, `GetLikesAsync`/`GetSharesAsync`,
replies collection), raw-inspector toggle.

**US-8 — Browse a remote (cross-instance) object and actor.**
As a user, I want to open an object or actor that lives on another instance (a peer item in a feed, or a
deep-linked IRI) and have it render via the proxy fallback / direct dial, so that the explorer works
across servers, not only on the local instance.
Components: `ObjectPage` / `ActorDetail` (deep-link + fetch), `IActivityPubClient`
(`GetObjectAsync`/`GetActorAsync` with `ProxyFallbackHandler`), the media proxy route
(`GET /ap/v1/media/proxy?url=…`) for cross-origin attachments, `ExplorerSession` dial base.

**US-9 — Enumerate and page any collection (outbox, inbox, feed, members).**
As a user, I want to enumerate any paged collection — a local or remote user's outbox, an inbox, a
community feed, members, followers — and page through it with "Load more", each item rendered and
clickable, so that I can walk long collections without loading them all at once.
Components: `ObjectView` (item), the paged-collection card pattern (first page via
`GetCollectionAsync`, "Load more" walking `next`), `IActivityPubClient` paged reads, `CollectionQuery`
(limit/bypass).

**US-10 — See instance information and resolve handles.**
As a user, I want the instance page to show nodeinfo (name, software, version, open-registration) and a
WebFinger lookup (resolve `@user@host` to an actor IRI), so that I can introspect the instance I am on
and resolve a handle to a detail page.
Components: `Instance` (nodeinfo + WebFinger cards), `IActivityPubClient` nodeinfo + WebFinger reads.

### C. Authoring and interacting

**US-11 — Compose and post a note (with media, markdown, sensitivity).**
As a user, I want to compose a note — with an optional media attachment (uploaded to my instance and
served same-origin), markdown content, and a sensitivity flag with summary — and post it through my
outbox, so that my post federates and the outbox returns the created object with its minted id.
Components: `Compose` (`InputFile` upload, markdown, sensitivity inputs), `IMediaClient`
(multipart upload → same-origin media IRI), `IActivityPubClient` (`PostNoteAsync`/outbox publish),
`IriExtensions.GetMediaAttachments`, `ObjectView` (rendering the result).

**US-12 — Reply to an object (threads).**
As a user, I want to reply to any object from its detail page, producing a note with `inReplyTo` set to
the object's IRI, and to see the reply chain (conversations) on the object page, so that I can take part
in threads and read them.
Components: `ObjectPage` (reply form), `ObjectView` (reply-chain / conversations rendering),
`IActivityPubClient` (`PostNoteAsync` with `InReplyTo`), the replies collection.

**US-13 — Like, boost, and undo those interactions on an object.**
As a user, I want to like/unlike and boost/unboost an object from its detail page, with the like/boost
counts updating, so that I can react to content and see the reaction totals.
Components: `ObjectPage` / `ObjectView` (Like/Boost buttons + counts), `IActivityPubClient`
(`LikeAsync`/`UnlikeAsync`/`AnnounceAsync`/`UnannounceAsync`), `IriExtensions` `LikesOf`/`SharesOf`
counts (null for external objects).

**US-14 — Delete an object I authored.**
As a user, I want to delete a note I authored from its detail page (author-only), so that the note is
tombstoned and the delete propagates to followers.
Components: `ObjectPage` (author-only Delete), `IActivityPubClient` (`DeleteAsync`, learned id).

**US-15 — Follow / unfollow an actor or community.**
As a user, I want to follow/unfollow an actor or community from its detail page (the follow published to
my outbox; the unfollow undoing it by the learned id), so that I control who reaches my feeds.
Components: `ActorDetail` / `Community` (follow card), `IActivityPubClient` (`FollowAsync`/`UndoAsync`
with learned id), the learned-id model (Decision 055).

**US-16 — Accept or reject an inbound follow (person or community).**
As a user, I want to see inbound follows on an actor's or community's detail page and accept or reject
them (the decision published as an AP-native Accept/Reject to the outbox), so that I can gate who
follows a gated actor or community.
Components: `ActorDetail` / `Community` (inbound-follows card), `IActivityPubClient`
(`AcceptAsync`/`RejectAsync`), the `manuallyApprovesFollowers` gate.

**US-17 — Moderate an actor (mute/block/flag) and a community's members.**
As a user, I want to mute (local) / block / flag (federated) an actor from its detail page, and to
manage community membership (add/remove) and community-scoped moderation, so that I can curate my feeds
and the communities I administer.
Components: `ActorDetail` / `Community` (moderation cards), `ILocalModerationClient` (mute/relay),
`IActivityPubClient` (`BlockAsync`/`FlagAsync`/`AddMemberAsync`/`RemoveMemberAsync`), the moderation
collections.

**US-18 — Create a community.**
As a user, I want to create a community (Group) from the UI, published as a `Create` of a Group to my
outbox (the server materializing it), so that I can establish a community and then manage it.
Components: a new create-community surface (form calling `CreateCommunityAsync`), `IActivityPubClient`
(`CreateCommunityAsync`), the community document + collections that must resolve afterward.

### D. Cross-cutting: components that make the tool "work"

These are not single-user stories but the shared components the stories above depend on. Each is a
candidate for its own deep-dive plan because several stories touch it.

**US-19 — Better object/activity rendering (the `ObjectView` component).**
The single most-reused component. It must render every object type (Note, Image, Video, Article,
Group/Person, Activity) completely and consistently: type badge, author (clickable), content
(markdown/HTML sanitized), audience, published/updated, parent, mentions, attachments/media (same-origin
proxy for external), sensitivity reveal, like/boost counts, and a deep-link to the object page. Every
collection card uses it, so improving it improves every page at once.
Components: `ObjectView`, `Markdown`, `IriExtensions` reads, media proxy.

**US-20 — A consistent paged-collection card (with Load more, refresh, and filter).**
A reusable component for any paged collection: first page via `GetCollectionAsync`, "Load more" walking
`next`, an optional `?refresh=true` bypass, and an optional `?q` filter. Used by outbox, inbox, feed,
members, followers, following, moderation collections, search.
Components: a new `PagedCollection` component, `IActivityPubClient` paged reads, `CollectionQuery`.

**US-21 — A raw-JSON inspector (explicit, per detail page).**
An explicit toggle on every detail page (actor, community, object, instance) showing the raw
ActivityStreams JSON of the loaded document — the debugging + verification surface that confirms the
UI's reads/writes are AP-native (19.6.1). Not the default view; a deliberate escape hatch.
Components: a `RawInspector` component, the loaded document's JSON serialization.

**US-22 — Consistent navigation (back links + deep-links).**
Every detail page has a back link to its parent (the page that deep-linked to it, or Home); every
collection item deep-links to its detail page. Navigation state (last-viewed object/actor) is preserved
across instance switches.
Components: `MainLayout` (back-link slot), each detail page, `ExplorerSession` navigable state.

**US-23 — Consistent error/empty/loading states.**
Every card on every page handles: loading (a spinner/placeholder, not a blank), empty (a clear "no
items" message), and error (a clear message, not a blank or a raw dump). This is partially done
(19.8.7); it must be consistent across all cards added by this phase.
Components: a shared `CardState`/`LoadState` pattern, each card's `catch` + empty branch.

**US-24 — Cross-server interaction (acting on other servers).**
The explorer should make multi-server use first-class: switching instance preserves state; remote
objects/actors render via the proxy; a remote item's attachments load via the media proxy; and the
identity bar makes clear which server an action will hit. This is the "interacting with other servers"
goal made concrete.
Components: `MainLayout` (identity bar), `ExplorerSession` (dial base + recent instances), the
`ProxyFallbackHandler`, the media proxy route, `ObjectPage`/`ActorDetail` remote fetch.

---

## Component inventory (where the deep-dives will land)

The components named above, grouped, each a candidate for a `docs/plans/22-*.md` deep-dive:

| Component | Current state | Stories it serves |
|---|---|---|
| `ObjectView.razor` | Renders object fields, media, audience, sensitivity, counts | 5,6,7,9,12,13,19 |
| `ObjectPage.razor` | Object detail: content + Like/Boost/Delete + replies (read-only) | 7,12,13,14,8,21,23 |
| `ActorDetail.razor` | Actor detail: outbox/inbox/follows/moderation + follow/inbound-follows cards | 5,15,16,17,8,21,23 |
| `Community.razor` | Community detail: feed/members/follows + membership/moderation/inbound-follows | 6,16,17,18,20,21,23 |
| `Compose.razor` | Compose: text + media upload + post | 11 |
| `Feed.razor` | Followed-feed page (first page) | 9,20 |
| `Actors.razor` | Actor/community search | 4 |
| `Instance.razor` | Recent-instances + switch | 2,3,10 |
| `Home.razor` | Logon + logged-on shell + recent-instances + continue-where-you-left-off + community feed | 1,2,9 |
| `MainLayout.razor` | Shell/nav (no identity bar, no back-link slot yet) | 3,22,24 |
| `Markdown` (sample-local) | Dependency-free markdown renderer | 7,11,19 |
| `ExplorerSession` | Session: identity, dial base, recent instances, navigable state, client bundle | 1,2,3,8,24 |
| `IActivityPubClient` / `ILocalModerationClient` | The client seams (outbox publish, paged reads, media, moderation) | all |
| `PagedCollection` (new) | Reusable paged card (Load more / refresh / filter) | 5,6,9,20 |
| `RawInspector` (new) | Raw JSON toggle per detail page | 5,6,7,10,21 |
| `CardState` / loading-empty-error pattern (new or tightened) | Consistent card states | 23, all cards |

**Deep-dive plan naming convention:** `docs/plans/22-{n}-{component-or-story-slug}.md` (e.g.
`22-1-objectview-deep-dive.md`, `22-2-paged-collection-component.md`). Each references the story(ies)
it elaborates and the roadmap item (22.0) it feeds. As each is produced, 22.0 in the roadmap gains a
one-line reference to it.

## Working order (suggested, to be confirmed at the broad review)

1. **Shared components first** (they unblock everything): `ObjectView` deep-dive (US-19), the
   `PagedCollection` component (US-20), the `RawInspector` (US-21), the card state pattern (US-23), and
   the `MainLayout` identity bar + back links (US-3, US-22).
2. **Detail pages** on top of the shared components: object (US-7,12,13,14), actor (US-5,15,16,17),
   community (US-6,18), instance (US-10).
3. **Authoring**: compose (US-11) and reply (US-12).
4. **Cross-server** polish: remote rendering (US-8), multi-server identity (US-24).
5. **Broad review of the full story set** to confirm they play together (shared components, no
   contradictions, consistent nav/error states) before the implementation sweep.

Each step: deepen → implement → manually test with Playwright MCP → resolve gaps → close, recording the
per-change doc and a 22.0 reference.
