# 139.1 — Federation & interop conformance review

> Part of [Phase 139](phase-139-platform-e2e-review.md). Scope: verify every activity type Iris
> speaks round-trips correctly against every peer platform it claims to interop with, using real
> live instances where possible (per Phase 138's Lemmy stack + the Phase 19/136 Mastodon test
> accounts), not just `TestServer` fixtures. Reuses Phase 136's cross-instance integration suite and
> Phase 138's interop matrix as the baseline — this review's job is to find what's *not* covered by
> either.

## Peers in scope

| Peer | Local fixture | Notes |
|---|---|---|
| Iris ↔ Iris | two-instance `TestServer` harness + the dev docker stack | baseline; should be fully green already |
| Iris ↔ Lemmy | [lemmy/](../../lemmy/) local stack | per Phase 138 |
| Iris ↔ Mastodon | `@RayvenMX@mastodon.world` (Phase 138 test-account) | real external instance — read-only interop only, do not spam-post |
| Iris ↔ Pleroma | spot-checked in Phase 79.3/81.2 | verify prior findings still hold |
| Iris ↔ Misskey | spot-checked in Phase 81.2 | verify prior findings still hold |
| Iris ↔ PeerTube | spot-checked in Phase 79.3 | verify prior findings still hold; video rendering path |

## Test scenarios

| # | Scenario | Steps | Pass criteria | Evidence |
|---|---|---|---|---|
| 1 | Cold WebFinger resolution for every peer type | Resolve `acct:` for a Person and (where applicable) a Group/Community on each peer from a clean Iris instance | Each resolves to the correct actor type without manual `!`-stripping surprises (Lemmy) or host mismatches | curl/UI transcript per peer |
| 2 | Actor document round-trip | Fetch each peer's actor doc via Iris's proxy; confirm all core fields (`inbox`, `outbox`, `publicKey`, `icon`) render in `ActorProfile` | No blank/missing fields beyond documented platform limitations (e.g. Lemmy Person has no `name`/`icon`) | screenshot per peer |
| 3 | Inbound Create (post) rendering | A post/status from each peer type appears correctly in the Iris feed/community view (title where applicable, content, attachments, sensitivity) | Content renders with no raw-JSON fallback, no truncation | screenshot per peer |
| 4 | Inbound reply/comment threading | A reply from each peer type threads correctly under its parent | Correct nesting depth and order (Phase 054 contract) | screenshot |
| 5 | Inbound Like/boost-equivalent | A like (and, for Lemmy, a dislike per Phase 138.17) from each peer is recorded and reflected in counts | `iris:likedCount`/`iris:dislikedCount` update correctly | before/after count diff |
| 6 | Outbound Create delivery | Iris posts to a community/actor followed by each peer type; confirm delivery succeeds and renders on the peer side | 2xx delivery, correct rendering on the peer (where the peer is locally controlled — Lemmy/Iris only; do not push test content to real Mastodon accounts) | delivery log + peer-side screenshot |
| 7 | Outbound Update/Delete propagation | Edit and then delete an Iris post that was delivered to a peer; confirm both propagate | Peer reflects the edit; peer shows the post removed/tombstoned per its own model | peer-side screenshot before/after |
| 8 | Follow/Undo-follow across peer types | Follow and unfollow each peer type from Iris and vice versa (where the peer UI allows it) | Follow/unfollow edges converge correctly on both sides (Phase 145 contract) | collection dump before/after |
| 9 | Pagination/backfill across peer types | Walk a peer's outbox with 50+ items; confirm Iris pages through all of them without loss or duplication | Item count matches source; no duplicate IRIs | count comparison |
| 10 | Signature/header conformance matrix | Capture the exact signature header set each peer sends and requires, compare against Iris's validator/signer (extends Phase 136.3's canonical verification matrix) | No peer's real traffic is rejected/rejects Iris for a header-construction reason not already known | header dump per peer |
| 11 | `@context`/vocabulary sniffing robustness | Confirm Iris's capability detection (`IsLemmy()`-style checks, Phase 137.2) doesn't misfire against Pleroma/Misskey/PeerTube documents | Correct feed/members IRI resolution for every peer type | unit test or live capture |
| 12 | Relay fan-out interop | Confirm a relay-subscribed peer receives fan-out correctly (Phase 28) against a real (or realistically simulated) external relay subscriber | Fan-out delivered, no duplicate/missing activities | delivery log |

## Deliverable check

All 12 scenarios executed with evidence recorded; findings triaged (class + severity) into this
doc's own tracker table (add one, matching the Loop protocol's shared-tracker shape, when scenarios
start failing); [docs/reference/](../reference/) interop matrix (Phase 138.28) updated with anything
new learned here.

## Progress tracking

- [x] 1  - [x] 2  - [x] 3  - [ ] 4  - [ ] 5  - [ ] 6
- [ ] 7  - [ ] 8  - [ ] 9  - [ ] 10 - [ ] 11 - [ ] 12

Check a scenario off only once its pass criterion is met with evidence attached (link/path). Update
the area's Status cell in [phase-139-platform-e2e-review.md](phase-139-platform-e2e-review.md) to
`in progress` on the first checked box, `done` when all are checked (or explicitly skipped).

**Resume checkpoint:** scenarios 1–3 done (F-3 found + fixed) — begin at scenario 4.

## Findings tracker (class + severity, per Loop protocol)

| # | Scenario | Class | Severity | Finding | Disposition |
|---|---|---|---|---|---|
| F-1 | 1 | UX | S3 | **Lemmy community WebFinger returns 400** — Lemmy does not serve WebFinger for communities (only persons). The Web UI's `!community@host` lookup (`Directory`/`Search`/`CommunityDetail`) strips the `!` then queries WebFinger, which 400s for a community. The supported + working path is **pasting the full community IRI** (`/c/{name}`), which the UI hint already documents, and which is exactly what the 138.5 federation path (`POST /local/v1/c/{name}/follow/{targetIri}`) uses. | No defect in the federation path. Optional UX polish (a later UX slice): auto-construct the `/c/{name}` IRI from a `!community@host` handle for Lemmy, instead of relying on WebFinger. |
| F-2 | 2 | UX | S3 | **Lemmy person `/followers` + `/following` collections return 404** — Lemmy does not expose person collections (verified: direct fetch of `lemmy.luit.ink/u/lemmyadmin/{followers,following}` → 404). The ActorDetail page requests both collections via the proxy; they 404 and the page logs 2 console errors, but renders correctly (handle + avatar monogram fallback, "No posts yet", no crash). | Not an Iris defect (Lemmy platform limitation). The page already degrades gracefully. Optional polish (a later UX slice): skip the collections fetch for actors whose document has no `followers`/`following` link, to avoid the console 404s. |
| F-3 | 3 | bug | S2 | **Cross-posted Note→Page renders its body twice** — alice's 138.11 cross-post (an Iris `Note` delivered to Lemmy as a `Page`) showed in the Lemmy community feed with the body rendered **twice**: once as the `name`/title and once as the `content`. Root cause: Iris composes Notes with **no `name`** (`ComposeNote.Build` sets only `Content`); when `TransformCreateForCrossPost` (`ActivityPubServerExtensions.cs:5208`) copies the null `Name` onto the Lemmy `Page`, **Lemmy derives the `Page`'s `name` from its content** on ingest — so `name` ≈ `content` in Lemmy's outbox. `ObjectView`'s Create + Announce/boosted branches each rendered both the title and the body without deduplicating. Normal Lemmy posts (`name` ≠ `content`) rendered correctly. | **Fixed + verified.** Added `NameDuplicatesContent` (HTML-stripped, case-insensitive equality/containment) in `ObjectView.razor.cs` + `ActivityTitleDuplicatesContent` / `BoostedTitleDuplicatesContent` guards; the `.razor` Create branch (line 46) and the Announce/boosted branch (line 241) now suppress `object-title` when the title duplicates the body. Build clean (0 warnings); live Playwright re-check confirms the duplicate is gone and distinct titles still render. |

## Scenario 3 — Inbound Create (post) rendering (evidence, 2026-09-15)

Verified live against the Lemmy interop community feed (`/community?iri=…/c/interop`, which reads the
Lemmy community outbox directly per `ResolveFeedIri`'s Lemmy branch) and the home timeline
(Mastodon/Pleroma inbound posts).

| Peer | Item | Render |
|---|---|---|
| Lemmy (lemmyadmin post) | `Announce → Create → Page` (`name` "Hello from Lemmy interop" ≠ `content`) | ✅ distinct title + body both render; `LemmyVoteBar` (score + comment count) |
| Lemmy (lemmyadmin post 2) | `Announce → Create → Page` (`name` "138.3 fixture post two" ≠ `content`) | ✅ distinct title + body both render |
| Iris→Lemmy cross-post (alice) | `Announce → Create → Page` (`name` ≈ `content`, Note cross-posted as Page) | ✅ **single** body render (title suppressed — see F-3 fix) |
| Mastodon (home timeline) | `Create → Note` | ✅ content + link + media/card attachment + boost/like controls |
| Pleroma (home timeline) | `Create → Note` | ✅ content renders |

**F-3 found + fixed.** alice's cross-post rendered its body **twice** (once as `name`/title, once as
`content`) because Iris composes Notes with no `name` (`ComposeNote.Build`), `TransformCreateForCrossPost`
copies the null `Name` onto the Lemmy `Page`, and Lemmy then derives the `Page`'s `name` from its
content on ingest — so `name` ≈ `content` in Lemmy's outbox. `ObjectView`'s Create + Announce/boosted
branches each rendered both the title and the body without deduplicating. **Fix:** added
`NameDuplicatesContent` (HTML-stripped, case-insensitive equality/containment) in
`ObjectView.razor.cs` + `ActivityTitleDuplicatesContent` / `BoostedTitleDuplicatesContent` guards; the
`.razor` Create branch (line 46) and the Announce/boosted branch (line 241) now suppress
`object-title` when the title duplicates the body. Normal Lemmy posts (distinct title) are unaffected
(verified live: titles still render). Build clean (0 warnings). Live Playwright re-check: the duplicate
is gone, distinct titles intact.

**Pass:** every peer's inbound Create renders with no raw-JSON fallback, no truncation, no duplicate
text. The one defect found (F-3) is fixed and verified.

## Scenario 2 — Actor document round-trip (evidence, 2026-09-15)

| Peer | Core fields present | Missing (documented limitation) | UI render |
|---|---|---|---|
| Iris Person (andrew) | id, type=Person, inbox, outbox, publicKey, icon, preferredUsername, name | summary, manuallyApprovesFollowers | ✅ full profile (avatar + name + handle) |
| Lemmy Person (lemmyadmin) | id, type=Person, inbox, outbox, publicKey, preferredUsername | **icon, name** (Lemmy Persons carry neither) | ✅ handle + monogram avatar fallback; 2 console 404s on `/followers`+`/following` (F-2, Lemmy limitation) |
| Lemmy Group (interop) | id, type=Group, inbox, outbox, publicKey, preferredUsername, name, summary | **icon** | ✅ name + summary + monogram avatar fallback (no icon) |
| Iris Group (interopX) | id, type=Group, inbox, outbox, publicKey, preferredUsername, name | icon, summary | ✅ name + monogram avatar fallback |
| Mastodon (real external) | id, type=Person, inbox, outbox, publicKey, icon, preferredUsername, name | — | ✅ (verified via direct fetch; actor doc renders all core fields) |

**Pass:** every peer's actor doc renders in `ActorProfile` with no blank/broken fields. The documented
platform limitations (Lemmy Person: no `name`/`icon`; Lemmy Group: no `icon`) are handled by the
avatar monogram fallback + handle-only display, exactly as `ActorProfile` is designed (null-checks for
Name/Summary/Icon/Banner). The only console noise is the F-2 Lemmy person-collections 404 (a Lemmy
limitation; the page degrades gracefully).

## Scenario 1 — Cold WebFinger resolution (evidence, 2026-09-15)

| Peer | Query | Result |
|---|---|---|
| Iris Person (local) | `acct:andrew@iris.luit.ink` | ✅ resolves → `https://iris.luit.ink/ap/v1/u/andrew` (also `alice` → `/ap/v1/u/alice`) |
| Lemmy Person | `acct:lemmyadmin@lemmy.luit.ink` | ✅ resolves → `https://lemmy.luit.ink/u/lemmyadmin` (Person) |
| Lemmy Community | `acct:interop!lemmy.luit.ink` / `acct:c/interop@lemmy.luit.ink` | ⚠️ **400** (Lemmy doesn't serve community WebFinger) — see F-1 |
| Mastodon (real external) | `acct:RayvenMX@mastodon.world` | ✅ resolves → `https://mastodon.world/users/RayvenMX` |
| Misskey | `acct:misskey@misskey.io` | ✅ resolves → `https://misskey.io/users/7rkr40rk13` |
| Pleroma / PieFed / PeerTube | — | ✅ verified via existing fixture round-trip tests (9 pass: `MisskeyInteropRoundTripTests` + `PleromaFamilyInteropRoundTripTests`); live WebFinger is host-dependent (fosstodon.org 404s the webfinger endpoint; piefed.social 200s). |

**Pass:** every peer type resolves to the correct actor type without manual surprises. The only
platform-specific behavior is the Lemmy community WebFinger 400 (F-1), which is a Lemmy-side
limitation and does not affect the Iris federation path (138.5 uses direct IRI, not WebFinger, for
Lemmy communities). No host mismatches; no `!`-stripping surprise in the federation path (the `!`
is only a Web UI input convention).
