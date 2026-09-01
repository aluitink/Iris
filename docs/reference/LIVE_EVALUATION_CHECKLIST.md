# Live Evaluation Checklist

> The standing manual checklist the Playwright sessions (and the operator) execute between turns.
> Each item is a **UI path** or **wire check** mapped to a Phase 19 waypoint. The checklist is
> **repeatable**: run it after any code change, after a `down`/`up` recreation, or before declaring a
> phase done. A clean sweep = every item passes or is explicitly recorded as a known gap (19.4).
>
> **Prerequisites:** the compose stack is up and healthy (`docker compose ps`), the public FQDNs
> resolve (`https://iris-dev1.luit.ink`, `https://iris-dev2.luit.ink`), and the smoke test passes
> (`./scripts/docker-smoke-test.sh`).

## Standing checklist (every session)

These are the baseline checks that must pass in every Playwright session before proceeding to the
phase-specific waypoints.

### 1. Logon

| # | Check | UI path / wire | Pass criteria |
|---|---|---|---|
| 1.1 | Alice logs on to iris-dev1 | UI: open explorer → enter `https://iris-dev1.luit.ink` → log on as `alice` / `iris-sample` | Actor detail page loads, `privateKey` extension present (raw inspector) |
| 1.2 | Bob logs on to iris-dev1 | UI: switch actor to `bob` | Bob's actor detail loads, `keyAlgorithm: rsa` |
| 1.3 | Alice logs on to iris-dev2 | UI: switch instance to `https://iris-dev2.luit.ink` → log on as `alice` | Actor detail loads (different key from iris-dev1's alice) |
| 1.4 | Wrong password rejected | UI: log on as `alice` / `wrongpass` | Public document only (no `privateKey` extension) |

### 2. Explore

| # | Check | UI path / wire | Pass criteria |
|---|---|---|---|
| 2.1 | Alice's outbox (iris-dev1) | UI: alice → outbox | Seeded note visible; click-through to object view renders content |
| 2.2 | Alice's followers (iris-dev1) | UI: alice → followers | bob + carla listed; click-through to actor detail |
| 2.3 | Community document | UI: community `iris` → document | Group document renders, members listed |
| 2.4 | Community feed | UI: community `iris` → feed | Seeded posts visible, newest first |
| 2.5 | Object view (raw) | UI: any object → raw inspector | JSON renders, `@context` present, `id` is the IRI |

### 3. Switch instance

| # | Check | UI path / wire | Pass criteria |
|---|---|---|---|
| 3.1 | iris-dev1 → iris-dev2 | UI: instance switcher → `https://iris-dev2.luit.ink` | Explorer re-loads against iris-dev2, alice's actor detail loads |
| 3.2 | iris-dev2 → iris-dev1 | UI: switch back | Same as above, reverse direction |

### 4. Cross-instance write

| # | Check | UI path / wire | Pass criteria |
|---|---|---|---|
| 4.1 | Follow (UI) alice-dev1 → alice-dev2 | UI: alice (dev1) → follow → alice@dev2 | 202; edge recorded on dev2's followers (wire: `GET /ap/v1/u/alice/followers` on dev2) |
| 4.2 | Post (UI) on dev1, visible on dev2 | UI: compose on dev1 → publish | Peer's inbox received the `Create` (wire: check dev2's delivery queue / alice's inbox on dev2) |
| 4.3 | Like (UI) | UI: like a post | Like recorded in alice's `liked` collection; peer notified (wire) |

### 5. Moderate

| # | Check | UI path / wire | Pass criteria |
|---|---|---|---|
| 5.1 | Block a user | UI: actor detail → block | Block recorded; blocked user's content excluded from feeds |
| 5.2 | Unblock | UI: unblock | Block removed; content visible again |
| 5.3 | Flag a user | UI: actor detail → flag | Flag recorded in moderation collection |

### 6. External instance

| # | Check | UI path / wire | Pass criteria |
|---|---|---|---|
| 6.1 | Resolve @RayvenMX@mastodon.world | UI: search / WebFinger | Actor document fetches, key validates |
| 6.2 | Follow @RayvenMX (UI) | UI: follow RayvenMX | 202; Accept arrives from Mastodon (wire: check alice's outbox for Accept) |
| 6.3 | Post visible on mastodon.world | UI: compose → publish | Public post URL on mastodon.world renders the note (browser check) |

---

## Phase 19 waypoints (mapped to UI paths)

> **Execution guide for the 19.1/19.2 RayvenMX-driven items:** see
> `LIVE_INTEROP_TEST_PLAN.md` (the standing routine — operator action → agent wire/UI observation →
> record, plus the findings tracker + current position for a fresh session).

### 19.1 — Live interop verification

| Waypoint | UI path / wire check | Notes |
|---|---|---|
| 19.1.1 Iris↔Iris baseline | Follow (UI) alice-a → alice-b; Accept round-trip (wire: both outboxes); unfollow via `Undo` (edge removed both sides); like; post+reply (peer's inbox received `Create`); community follow; community post surfacing | Sanity check before external platforms |
| 19.1.2 Follow scenarios F1–F4 | RayvenMX follows us (UI: our followers collection shows them); we follow RayvenMX (UI) → their Accept arrives; Reject behavior (our local-follow-reject endpoint); unfollow via `Undo` (their profile UI) | |
| 19.1.3 Post/receive C1–C4 | We post (UI compose) → signed `Create` delivered to RayvenMX's inbox → **Mastodon renders it** (check public post URL on mastodon.world); RayvenMX posts → our inbox records it → visible in local feed; extended-type objects round-trip | Core "post and have it federate" proof |
| 19.1.4 Signature SIG1–SIG5 | Inbound from Mastodon: RSA-SHA256 validates (no 401); Ed25519 inbound (if available); unsigned POST rejected 401; our ServerToServer profile accepted by Mastodon; unsigned GETs both ways | Wire-level checks (raw inspector / delivery queue) |
| 19.1.5 Pagination + content types | Mastodon client pages our outbox via `?page`/`?limit`; we page their outbox to exhaustion; we serve `application/activity+json`; we accept `application/ld+json` inbound | |
| 19.1.6 Community G1–G4 | RayvenMX follows our `iris` community → we Accept → they appear in members/followers (UI community screen); G2/G4 tabled (record current behavior) | |
| 19.1.7 Discovery | Our nodeinfo + webfinger consumable by mastodon.world; our global search lists local actors + content; we fetch their public profile via explorer's object view | |
| 19.1.8 Matrix re-baseline | Update COMPATIBILITY_MATRIX.md §5 with live outcomes | Findings → 19.4 |

### 19.2 — Real-world Mastodon account

| Waypoint | UI path / wire check | Notes |
|---|---|---|
| 19.2.1 Inbound | RayvenMX posts → our server stores `Create`, object fetchable by IRI (explorer's object view), appears in local surfaces (community feed / member outbox / followed feed); signature + content-type verified on wire | |
| 19.2.2 Outbound | Follow, post, reply (to their toot → verify thread on mastodon.world), like, boost (`Announce`) — each verified **on mastodon.world** (public URLs) + wire-level signed delivery | |
| 19.2.3 Object-shape conformance | Fetch same object from our server + mastodon.world, diff shapes (`@context`, `attributedTo`/`to`/`cc`, `content` HTML, `url`, `published`/`updated`, `inReplyTo`, `tag`, `attachment`, `sensitive`, `spoilerText`) | Enrichment allowed only while conformance holds |
| 19.2.4 Threads/replies | 3-level thread via UI; verify `inReplyTo` chains render on mastodon.world + our object view (conversations) | Baseline for 19.7 |
| 19.2.5 Delete/moderation propagation | Delete our post → `Delete` propagates to Mastodon (tombstone/graveyard); mute (local) / block + flag (federated) → record which Mastodon honors; Undo of like/unfollow propagates | |

### 19.3 — Two-instance network

| Waypoint | UI path / wire check | Notes |
|---|---|---|
| 19.3.1 Follow-loop safety | Post on A → lands in B's inbox exactly once → B does not re-deliver A's post to A; count occurrences in outboxes/stores after recreation + repeated posts | |
| 19.3.2 Echo/amplification | Post once with mutual follows; enumerate every delivery event; assert total is bounded (no quadratic growth, no re-announce of announces) | |
| 19.3.3 Announce propagation | Boost note on A → reaches B's followers once; boost note from B on A → no infinite announce chain, correct `object` link | |
| 19.3.4 Delete propagation | Delete local note → peer tombstones; delete note originating on peer → correct scope, no collateral deletion | |
| 19.3.5 Follow-edge convergence | Follow/unfollow/re-follow cycle → both sides' `following`/`followers` converge (same IRIs, same counts, stable pagination) | |
| 19.3.6 Update propagation | Update (re-publish, same IRI) → peer's stored copy updated (or correctly ignored); record which | |
| 19.3.7 Recreation stability | Run 19.3.1–19.3.5, `down` (no `-v`) + `up`, re-verify: no re-delivery storms, no duplicated edges, outboxes unchanged | |

### 19.4 — Remediation

| Waypoint | UI path / wire check | Notes |
|---|---|---|
| 19.4.1 Triage | Collect every FAIL/GAP finding from 19.1–19.3 + 19.5–19.7 into a prioritized list (change doc, repro steps + wire evidence) | |
| 19.4.2 Fix in priority order | Federation correctness first (loops/echoes, signature failures, delivery loss); then conformance (object shape, audiences); then UI (navigability, rendering). Each fix is its own slice (impl + tests) | |
| 19.4.3 Regression re-verification | Re-run the full evaluation checklist end-to-end over the FQDNs; record a clean sweep | |

### 19.5 — Community creation & management

| Waypoint | UI path / wire check | Notes |
|---|---|---|
| 19.5.1 Community creation | UI path to create a community (signed `Create`/`Add` to community outbox, or management-style message). Verify: document endpoint, `members`, empty `feed`, `following`/`followers`, WebFinger discovery | Note as a finding if no UI path exists |
| 19.5.2 Membership management | Add/remove members via management-style activity messages (not direct store writes); community feed reflects membership changes | Record the join model decision |
| 19.5.3 Community peers | Community follows remote actor/community via `POST /ap/v1/c/{name}/outbox` (Follow); verify edge, `following` collection, delivery; unfollow via `Undo`; reject/undo for inbound follows | |
| 19.5.4 Community moderation | Flag/block/mute at community level; verify moderation collections; moderated actors' content excluded from community feed (or record gap) | |
| 19.5.5 Community feed correctness | Unified feed (members' outboxes, newest first, de-duplicated): local member posts, remote content in community inbox, pagination, `?refresh=true` cache bypass. Compare against raw member outboxes | |
| 19.5.6 Community lifecycle on recreation | Community created in prior turn (members, follows, content) survives `down`/`up` with all collections intact | |

### 19.6 — Architectural expectations

| Waypoint | UI path / wire check | Notes |
|---|---|---|
| 19.6.1 Management via ActivityStream only | Every management operation expressible + verified as signed ActivityStream/AP message through outbox/inbox — no side channel. UI writes show (raw inspector) they are these messages | |
| 19.6.2 All activities flow through outbox | Every activity a local actor/community authors appears in their outbox (Follow, Accept, Create, Like, Announce, Undo, Delete, moderation) in stable order. Enumerate outbox after every write screen, match 1:1 | |
| 19.6.3 Post-interact, server-delivers | Client posts to outbox; **server** performs delivery (signed, per-actor). Verify: peer's inbox received activity with valid signature; client's pipeline never made the cross-instance POST (delivery queue + peer logs) | |
| 19.6.4 Signature identity | Deliveries signed as the *acting* actor (decision 029), resolvable from actor document; proxy re-signs as acting actor (decision 037). Raw inspector: key IRI in `Signature` header matches acting actor's `publicKey` id | |
| 19.6.5 Audience correctness | Outbound `Create`/`Announce` carry correct `to`/`cc` (followers + `as:Public` for public; reply target for replies); delivery recipients match audience | |
| 19.6.6 Cache behavior at boundary | Cached reads expose `bypassCache`/`?refresh=true`; new activity visible after bypass; no stale-forever | |

### 19.7 — Threads compatibility probe (best-effort)

| Waypoint | UI path / wire check | Notes |
|---|---|---|
| 19.7.1 Discovery | WebFinger `@mosseri@threads.net`; fetch actor document (explorer's object view / raw inspector); record shape (Ed25519 key, `@context`, non-standard properties) | |
| 19.7.2 Follow | Follow mosseri via UI; observe response (Accept? silent? error?) + Threads' profile. If stuck, record wire exchange and stop | |
| 19.7.3 Inbound content | If following works, have a Threads post arrive (or fetch by IRI); verify server stores it, renders in object view, surfaces in followed feed. Verify unknown-property passthrough | |
| 19.7.4 Outbound content (best-effort) | Post Note to our outbox addressed to Threads audience; observe whether Threads accepts (202? 401? 422?). Reply to a Threads post if discoverable. **A stuck state is a valid outcome** — notes + `BLOCKED`/`GAP`, then move on | |
| 19.7.5 Threads findings doc | Consolidate probe into change doc: what works, what's rejected + why, minimal change list (deferred to 19.4 or future phase) | No implementation in this phase |

### 19.8 — UI navigability & rendering

| Waypoint | UI path / wire check | Notes |
|---|---|---|
| 19.8.1 Click-through audit | From each collection surface (actor outbox, followers, following, liked, blocks/flags/mutes, relays, community members/feed/following, followed feed, home feed, search results, recent-instances), selecting an item navigates to rendered view (ObjectPage/ActorDetail/Community) — no raw-JSON dead ends. Record every transition that doesn't work | |
| 19.8.2 Rendered object view quality | ObjectPage renders: author (clickable), content HTML (sanitized), audiences (to/cc as handles), timestamp, reply chain (conversations), like/boost counts, canonical public URL (remote objects) | |
| 19.8.3 Actor detail completeness | Actor detail renders: name, handle, bio, avatar, follow/unfollow button, outbox link, followers/following links, moderation buttons (block/flag/mute) | |
| 19.8.4 Community screen completeness | Community renders: name, description, member list, feed, follow/unfollow button, document link | |
| 19.8.5 Error states | 404 (unknown actor/object), 401 (unsigned inbox POST), 403 (forbidden) render as error views, not blank pages or raw JSON | |
| 19.8.6 Loading states | Collection pages show loading indicator while fetching; empty collections show "no items" message, not blank | |
