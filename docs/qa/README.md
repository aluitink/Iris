# QA Findings

One document per QA finding from the recurring **General UI/UX review** (Playwright-driven passes against the QA cluster, primarily `https://qa-iris-a.luit.ink`; peer services are `https://qa-lemmy.luit.ink` and `https://qa-mastodon.luit.ink`). `https://iris.luit.ink` is the production FQDN and should not be used for development or QA. This folder is where new QA findings live — **not** in `PLAN.md`.

## Process

1. During a QA pass, every finding is written (or updated) as its own document in this folder using the template below.
2. `PLAN.md` carries only a one-line pointer to this section, plus the count of open findings and the top-priority one.
3. When a finding is fixed and re-verified from a clean entry, set its Status to `fixed (date, commit)` and record the re-verification evidence in the doc.
4. The pass-by-pass narrative lives in [passes.md](passes.md) (append-only; PLAN.md no longer carries pass narratives).

## Template

```markdown
# S<n> — <short title>

- **Class:** bug | UX | perf | data-integrity | feature-gap — **Severity:** S1 (blocker) | S2 | S3
- **Status:** open | fix committed (hash), not yet live | fixed (date, commit)
- **Found:** Pass <n> (<date>)
- **Related:** links to sibling findings / plans / change docs

## Symptom
What the user sees (page, control, states, console errors, counts).

## Root cause
Code-level cause with file:line references.

## Fix
The agreed fix approach.

## Re-verify
Clean-entry steps that prove the fix (no evidence, no `fixed`).
```

## Open findings

| ID | Title | Class | Sev | Status | Doc |
|---|---|---|---|---|---|
| S2 | Signed-out remote reads bypass the proxy (CORS/blank avatars) | bug | S2 | open (proxy 401s unsigned GETs) | [s02](s02-signed-out-proxy-bypass.md) |
| S3 | Object-detail 404s a local post's collections (Create-activity IRI) | bug | S3 | open | [s03](s03-object-detail-create-iri-404.md) |
| S4 | Communities "Following" tab drops followed REMOTE communities | UX / bug | S2 | open | [s04](s04-communities-following-remote.md) |
| S5 | Search lists a stale orphaned local actor (localhost IRI) | bug / data-integrity | S2 | fixed (2026-09-20, `456b0d9`) | [s05](s05-search-localhost-orphan-actor.md) |
| S6 | Join on a remote community is a silent no-op (CSP-blocked browser POST) | bug | S2 | fix committed (`68ae703`), not yet live | [s06](s06-remote-join-csp-blocked.md) |
| S7 | Directory external lookup stuck on the spinner forever | bug | S2 | fixed (2026-09-20, `456b0d9`) | [s07](s07-directory-external-lookup-stuck.md) |
| S8 | Communities "All on this instance" list is incomplete/inconsistent | bug / data | S2 | fixed (2026-09-20, Pass 27) | [s08](s08-communities-all-tab-incomplete.md) |
| S9 | Report/flag is a silent no-op (no feedback, duplicate flags) | UX / bug | S2 | fixed (2026-09-20, Pass 38) | [s09](s09-report-silent-noop.md) |
| S10 | Article "(long-form)" is mislabeled | UX / feature-gap | S2 | fixed (2026-09-20, Pass 38) | [s10](s10-article-longform-mislabeled.md) |
| S11 | Poll broken (silent no-op w/o body + invisible in "Your posts") | bug | S2 | fixed (2026-09-20, Pass 38) | [s11](s11-poll-silent-noop-and-outbox.md) |
| S12 | @mention linkify (case-sensitive dead link + autocomplete mismatch) | bug | S2 | fixed (2026-09-20, Pass 38) | [s12](s12-mention-case-and-autocomplete.md) |
| S13 | Remote Lemmy object-detail logs expected proxy 404s | UX / bug | S3 | fixed (2026-09-20, Pass 39) | [s13](s13-remote-lemmy-404-noise.md) |
| S14 | Signed-out remote actor-detail is CSP-blocked (S2 facet) | bug | S2 | open | [s14](s14-signed-out-actor-detail-csp.md) |
| S15 | Compose visibility hint is misleading for Followers/Direct | UX / cosmetic | S3 | fixed (2026-09-20, Pass 39) | [s15](s15-visibility-hint-misleading.md) |
| S16 | Poll votes are not persisted | bug / data-integrity | S3 | fixed (2026-09-20, Pass 37); UX badge gap remains | [s16](s16-poll-votes-not-persisted.md) |
| S17 | Profile tabs over-fetch the entire outbox (on load + every tab switch) | perf / request-spam | S2 | open | [s17](s17-profile-tabs-overfetch-outbox.md) |
| S18 | Following a local account: follow "succeeds" but follower's Home timeline stays empty | bug / data-integrity | S2 | partially fixed (Pass 39 — state persists, timeline populates; follow request not auto-approved) | [s18](s18-local-follow-timeline-empty.md) |
| S19 | Community page 404s; actor page has no Requests tab; notifications lack Accept/Decline | bug | S2 | open (Pass 43 — scope changed: /c/{name} 404s, actor page has no Requests/Members tabs) | [s19](s19-community-requests-tab-fails.md) |
| S20 | Home feed "Communities" tab is non-functional (no API call, same content as Posts) | bug / feature-gap | S2 | open (found Pass 42) | [s20](s20-home-feed-communities-tab-nonfunctional.md) |
| S21 | Newly created community missing from Following tab; /c/{handle} 404s | bug / data-integrity | S2 | open (found Pass 46) | [s21](s21-new-community-missing-following-tab.md) |
| S24 | Cross-instance follow: Following tab omits remote actors, spurious self-follow in outbox, remote-actor GET 404s | bug / data-integrity | S2 | **PARTIALLY FIXED (Pass 116, current build)** — **D1 (Following-tab omits remote actor) FIXED**: ii-a1's Following tab now renders the **remote `ii-b1`** (with Unfollow) alongside the local community, and the wire `GET A /u/ii-a1/following` → `totalItems`=2 (community + remote ii-b1); D3 (remote-actor GET) already fixed. **D2 (foreign activities in the local actor's outbox) STILL OPEN** (re-confirmed Pass 114: 3 B activities — Announce+Create+Follow — in ii-a1's outbox). **Status: OPEN (narrowed) — D2 only.** | [s24](s24-cross-instance-follow-state-inconsistent.md) |
| S25 | Remote post delivered to inbox but not surfaced in follower's home feed | bug / data-integrity | S2 | **superseded by [S36](s36-home-feed-omits-posts-and-is-polluted-with-actor-document-activity.md)** (Pass 101, 2026-09-21) — the remote post IS delivered+stored on the peer, but the **whole home feed** omits content `Create`s (own + local + remote) and is polluted with actor-doc activity; S25 is a subset of the broader S36 regression | [s25-remote-post-not-in-followers-home-feed.md](s25-remote-post-not-in-followers-home-feed.md) |
| S26 | Remote reply delivered+stored but not threaded under parent Note's `replies` | bug / data-integrity | S2 | **fixed (2026-09-21, Interop A5 re-test)** — remote reply threaded under parent's `replies` + rendered nested; not reproduced | [s26-remote-reply-not-threaded-under-parent.md](s26-remote-reply-not-threaded-under-parent.md) |
| S27 | Cross-instance Like delivered to shared inbox but DROPPED ("no local recipient"), never applied | bug / federation-delivery | S2 | **fixed (2026-09-21, `27b1ba6`, change 14824)** — remote Like now routed to the note's author via shared inbox; author's `likedCount` 0→1; not reproduced | [s27-like-dropped-at-shared-inbox-no-local-recipient.md](s27-like-dropped-at-shared-inbox-no-local-recipient.md) |
| S28 | Remote Announce (Boost) delivered+stored on author but not surfaced in note's `shares` | bug / data-integrity | S2 | **PARTIALLY FIXED (Pass 112, current build)** — the Pass-102 **regression (Announce dropped at shared inbox "no local recipient") is RESOLVED**: the remote Announce is now **delivered + accepted + stored** (`AnnounceActivityHandler ok` + `Inbox accepted`) and **surfaced** — `GET A <note>/shares` → 200 (contains the boost) + Shares tab lists the booster (ii-b1). **Remaining (count facet):** author `shares`/`sharedCount` = None, note `shares.totalItems` = 0, Boost button 0, author `/shares` 404s — the **count is not materialized** (same pattern as S37 for Likes). The S28 fix is now deployed. | [s28-remote-announce-stored-not-in-shares.md](s28-remote-announce-stored-not-in-shares.md) |
| S29 | Community (Group) not resolvable via WebFinger (`acct:!name@host` → 404) though Group doc exists | bug / discovery | S2 | **fixed (2026-09-21, Interop A8 re-test)** — community webfinger `acct:!…@…` → 200; not reproduced | [s29-community-webfinger-404.md](s29-community-webfinger-404.md) |
| S30 | Cross-instance community join/view blocked (remote community unreachable from peer) | bug / federation | S2 | **PARTIALLY FIXED (Pass 111, build `11fbec6`)** — **A8.2 direct-view FIXED** (`GET B /ap/v1/c/{name}` now serves the cached remote Group 200 + B UI renders the community page w/ Join + "Post to this community" + Feed/Members); A8.3 follow works (actor page); **A8.4 community feed STILL OPEN** (`GET B /ap/v1/c/ii-a8-community/feed` → 404 — dev's `11fbec6` fixed only the `/c/{name}` direct-view, not the `/feed` endpoint) | [s30-cross-instance-community-join-blocked.md](s30-cross-instance-community-join-blocked.md) |
| S31 | Editing a Note clears its `published` timestamp; `Update` object omits `updated` | bug / data-integrity | S3 | **fixed (2026-09-21, `45f3038`, change 14822)** — edit preserves `published` + stamps `updated` on the stored Note + `Update` object; not reproduced (the peer-copy-dropped side-effect is S32) | [s31-edit-clears-published-timestamp.md](s31-edit-clears-published-timestamp.md) |
| S32 | Delete (tombstone) emitted locally but NOT propagated to peer; peer keeps stale live copy | bug / federation | S2 | **OPEN (narrowed, Pass 109 @ build `6b11799`)** — dev's `6b11799` fixes the **receiving-side** routing (a shared-inbox Delete/Update of a *local* note now routes to the author, +2 integration tests), but the **cross-instance A→B Delete is still not delivered**: A's `Delete` has `to`/`cc`=None and A sends nothing to B's shared inbox, so B's Tombstone is again a **lazy refetch**, not a propagated delete → a non-refetching peer keeps a stale live copy. **Sending-side delivery (address the Delete to the note's audience) still missing.** | [s32-delete-not-propagated-peer-stale-copy.md](s32-delete-not-propagated-peer-stale-copy.md) |
| S33 | Unfollow (`Undo` of `Follow`) emitted locally but NOT propagated; peer's `followers` edge remains | bug / federation | S2 | **fixed (2026-09-21, `d6914d6`, change 14823)** — shared-inbox bare-IRI `Undo` now routes to the follow's target; peer's `followers` drops the unfollower; not reproduced | [s33-unfollow-undo-not-propagated-peer-edge-remains.md](s33-unfollow-undo-not-propagated-peer-edge-remains.md) |
| S34 | Gated (manually-approved) follow request added to public `followers` BEFORE acceptance | bug / data-integrity (privacy) | S2 | **fixed (2026-09-21, Interop A3 re-test)** — gated follower withheld from public `followers` until Accept; not reproduced | [s34-gated-follow-not-withheld-from-public-followers.md](s34-gated-follow-not-withheld-from-public-followers.md) |
| S35 | Cross-instance actor discovery fails: remote Mastodon actor unresolvable (Iris proxy → upstream 404 on the Mastodon AP actor doc) | bug / discovery / federation | S1 | open (found Interop M1/M2/M3, 2026-09-21) — **Mastodon-side (this cluster) per wire: every `/ap/users/{id}`, `/api/v1/accounts/*`, `/@{handle}` route 404s while webfinger resolves + `user_count:1`**; blocks M2–M12 | [s35](s35-remote-actor-discovery-proxy-404.md) |
| S36 | Home feed omits ALL post `Create`s (own + local-follow + remote-follow) and is polluted with actor-document activity noise (`Update`/`Add`/`Remove`/`Follow`/`Undo`/`Delete`/`Like` on actor/note IRIs) → UI "Your timeline is empty" | bug / feed / regression | S2 | **NEW (Pass 101, 2026-09-21, build `27b1ba6`)** — broad home-feed regression: the authenticated `/feed` returns 16–17 items dominated by actor-doc activity; only one content `Create` (a community Group) + one `Announce` are present. Restarting B (clearing the in-memory feed cache) did **not** change it → not a stale cache. **Supersedes/blocks S25** and re-opens the S18 local-timeline symptom. Dev code pass on `FeedService.BuildFeedUncachedAsync` (feed source over-inclusive of actor-doc activity / under-inclusive of content `Create`s). | [s36](s36-home-feed-omits-posts-and-is-polluted-with-actor-document-activity.md) |
| S37 | Like/Boost stored + `/likes`/`/shares` correct, but the Note's `likedCount`/`sharedCount`/embedded `likes.totalItems`/`shares.totalItems`/inline counts stay 0/None (count not materialized) — the count-analog of S28 | bug / count-materialization | S3 | **BROADENED (Pass 117, current build): the gap is GENERAL — it affects LOCAL Like/Boost too, not just remote.** On note `06GC4RR4` (local Like by ii-a1 + remote Boost by ii-b1): wire `likedCount`/`sharedCount`/`likes.totalItems`/`shares.totalItems` all None/0, while `/likes` totalItems=1 + `/shares` totalItems=1 (correct); UI Like button shows "1" (derived) but the **Boost button count is 0** + "1 boost"/"Shares (1)" show. Fix = materialize the denormalized counts on any Like/Announce (local or remote). | [s37](s37-remote-like-stored-but-likedcount-not-materialized.md) |
| S38 | WebFinger does not proxy remote accounts — `GET /.well-known/webfinger?resource=acct:{handle}@{remote-host}` → 404 (empty) for a remote account, even when the remote actor is cached locally (`/ap/v1/u/{handle}` → 200); own-instance webfinger works (200) | bug / discovery | S3 | **NEW (Pass 126, 2026-09-21, current build `aebe420`)** — A `webfinger(ii-b1@qa-iris-b)` → 404 + B `webfinger(ii-a1@qa-iris-a)` → 404 (both directions), while `A webfinger(ii-a1@qa-iris-a)` (own) → 200 + `A /ap/v1/u/ii-b1` (cached) → 200. The WebFinger handler only resolves LOCAL accounts; it does not proxy to the remote instance's WebFinger. Handle-based cross-instance discovery (type `user@remote`) is broken at the WebFinger layer (follow-by-IRI still works, so the A-suite wasn't blocked). Fix = proxy the WebFinger request to the remote host when `@host` ≠ local. | [s38](s38-webfinger-does-not-proxy-remote-accounts.md) |

**Open: S2, S3, S4, S14, S17, S19, S20, S21, S24, S28, S30, S32, S35, S36, S37, S38 (16). Fix-committed-not-live: S6. Fixed: S5, S7, S8, S9, S10, S11, S12, S13, S15, S26, S27, S29, S31, S33, S34 (15). Superseded: S25 (by S36). Partially-fixed: S18. Core-fixed-UX-gap: S16.**
> 2026-09-21 fresh-cluster re-test: **S26 (reply threading), S29 (community webfinger), S34 (gated follow) confirmed FIXED** (not reproduced). **NEW S35 (S1)** — cross-instance Mastodon actor discovery 404s (attributed Mastodon-side). **1 S1/blocker (S35)** — but S35 is a QA-cluster provisioning issue, not an Iris regression.
> 2026-09-21 Pass 100 (rebuilt QA cluster @ HEAD `27b1ba6`): **S27 (remote Like applied on author), S31 (edit preserves `published`), S33 (unfollow `Undo` propagates) confirmed FIXED** — these had reproduced in the earlier run only because the QA cluster images pre-dated the fixes; rebuilding the two Iris services from HEAD made them live and they no longer reproduce.

> Note: S1 was the Pass-10 defect set (signed-out 401 spam, proxy 500, Lemmy misclassification) — all fixed and verified Pass 11; it is recorded in [docs/changes/997-ui-ux-review.md](../changes/997-ui-ux-review.md), not here.

## Pass log

See [passes.md](passes.md) for the archived pass-by-pass narrative (Passes 10–25).
