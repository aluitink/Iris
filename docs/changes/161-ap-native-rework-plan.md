# Change 161 — AP-Native Rework Plan (PROPOSAL, pending review)

> **Status: PROPOSAL — not started.** This doc captures the investigation findings and the
> concrete rework path for making Iris fully ActivityPub-native. **No code has been changed.**
> Implementation begins on a new branch only after this plan is reviewed and approved.
>
> **Directive:** keep everything as AP-native as possible. All actor/community activities —
> including follow Accept/Reject, mute, block, flag, and the rest — should flow through the
> actor's outbox (client authors the activity → outbox POST → server delivers to the right
> inbox / applies the local effect). The AP client becomes a *pure protocol layer*; any
> convenience layer is built separately.

## 1. Why

Today a mix of **ActivityPub-core** activities and **Iris-only operator endpoints** coexist.
The Iris-only ones are the "stuff that worked" that drifted in: a handful of dedicated HTTP
routes that accept/reject/mute/relay/flag *directly*, bypassing the outbox the rest of the
system is built around. The goal is to collapse that onto the outbox so the client is a clean
AP client and the server only speaks AP.

## 2. What the investigation found (the actual surface)

The codebase is **closer to AP-native than the docs suggest.** The client's
`ActivityPubClient` *implementation* already posts **every** activity to
`actorId.OutboxOf()` — `Follow` (L243), `Undo` (265), `Like` (366), `Delete` (406), `Block`
(441), `Flag` (495), `Create`/note (698, 759). The **stale `IActivityPubClient` XML docs**
claim "delivered to `targetId.InboxOf()`" — the comments are wrong, the code is right. So the
real Iris-only surface is **smaller** than it looks.

### 2.1 Server routes — classify

**AP-CORE (keep):**
- `GET /ap/v1` node info
- `GET /o/{path}` object
- `POST /i/{path}` inbox (all inbound activity)
- `GET /u/{handle}`, `GET /c/{name}` actor/community doc
- `GET /u/{handle}/{outbox,followers,following,mutes,relays,flags,notes,feed}` — collections
- `GET /c/{name}/{outbox,followers,following,mutes,feed}` — collections
- `GET /ap/v1/c/{name}/search`, `GET /ap/v1/search` — search (Mastodon-standard; not core AP
  but widely implemented — **decision D2**)
- `POST /oauth2/*` — OAuth (Mastodon-standard)
- `GET /health` — operational

**IRIS-ONLY operator endpoints (REMOVE):**
| Route | Handler | Effect today |
|---|---|---|
| `POST /ap/v1/u/{h}/follows/{**f}` | `LocalFollowDecisionHandler` | accept/reject an inbound follow |
| `POST /ap/v1/c/{n}/follows/{**f}` | `CommunityFollowDecisionHandler` | accept/reject a community inbound follow |
| `POST /ap/v1/u/{h}/mutes/{**t}` | `LocalMuteHandler` | mute/unmute |
| `POST /ap/v1/c/{n}/mutes/{**t}` | `CommunityMuteHandler` | community mute/unmute |
| `POST /ap/v1/u/{h}/relays/{**t}` | `LocalRelayHandler` | subscribe/unsubscribe relay |
| `POST /ap/v1/proxy/{**t}` | `ProxyHandler` | browser cross-instance relay |

**BORDERLINE (decision D1/D3):** the proxy relay is a genuine cross-origin need (the browser
can't sign). Search + feeds are widely-implemented extensions. These stay *for now* but are
flagged; the follow-decision + mute + relay endpoints go.

### 2.2 Client methods — classify

**AP-CORE (keep, already post to the outbox):** `FollowAsync`, `UndoFollowAsync`, `LikeAsync`,
`UnlikeAsync`, `DeleteAsync`, `BlockAsync`, `UnblockAsync`, `FlagAsync`, `UnflagAsync`,
`PostNoteAsync`, `PostReplyAsync`, `Get*` collections, `SearchAsync`, `SendAsync`.

**IRIS-ONLY convenience (MOVE to a separate `LocalModerationClient` / drop from the core
interface):**
- `AcceptFollowAsync` / `RejectFollowAsync` (4 overloads) → hit the `follows/` endpoint
- `MuteAsync` / `UnmuteAsync` (4 overloads) → hit the `mutes/` endpoint
- `SubscribeRelayAsync` / `UnsubscribeRelayAsync` (4 overloads) → hit the `relays/` endpoint

### 2.3 The gap the rework closes (Accept/Reject)

The **inbound** `Follow` handler (`FollowActivityHandler`) is *already* AP-native: it records
the edge, surfaces the follow in the followed actor's **outbox**, and — when auto-accepting —
schedules the outbound `Accept` to the follower's inbox (L165-168). When
`manuallyApprovesFollowers` is set it stops early and waits for an operator decision.

The **missing** piece is the operator's manual decision: today it goes to the Iris-only
`follows/` endpoint. After the rework it goes to the **outbox** as an authored `Accept` or
`Reject`, and the `OutboxPublishHandler` gains two branches:

- **`Accept`**: record the `Accept` in the outbox; deliver it to the follower's inbox (the same
  `DeliverToActorAsync` the auto-accept path uses); the follower-side edge is already recorded
  by the inbound handler (provisional) — confirm it (idempotent).
- **`Reject`**: record the `Reject` in the outbox; **remove the provisional follow edge**;
  deliver the `Reject` to the follower's inbox.

Deterministic IRIs are preserved: `{actorIri}/accepts/{followIri}` and
`{actorIri}/rejects/{followIri}` (Mastodon-compatible, from `FollowIris.BuildAccept`/
`BuildReject`).

### 2.4 Mute / relay (no ActivityStreams type)

A `Mute` and a relay subscription have **no ActivityStreams type** and are **local, non-
federated** decisions — they cannot be expressed as an outbox activity the way Follow/Accept
can. Two options:

- **D4a (recommended):** keep them as **local Basic-auth endpoints** but move them *off* the
  `/ap/v1/...` route tree (they are not AP) into a clearly-separated `LocalModerationClient` +
  a non-AP local route namespace, so the `/ap/v1` surface is 100% AP. The client's core
  `IActivityPubClient` no longer carries them.
- **D4b:** model them as a small custom activity (e.g. `iris:Mute`) posted to the outbox and
  interpreted locally by the outbox handler. More "AP-shaped" but invents a non-standard
  activity type.

D4a is the lower-risk, more honest choice: mute/relay are *not* federated, so they don't
belong on the AP wire at all.

## 3. The rework path (phases)

Branch: `feat/ap-native` off `main`. Each phase is an independently-committable,
test-green step (commit style: `feat(...)` impl+tests, `docs: ...` docs).

### Phase A — Outbox Accept/Reject (the core)
1. `OutboxPublishHandler`: add `Accept` + `Reject` branches (deliver to follower inbox;
   Accept confirms edge, Reject removes edge). Reuse the deterministic IRIs.
2. `IActivityPubClient` / `ActivityPubClient`: add **AP-core** `AcceptAsync(actorId, followIri)`
   / `RejectAsync(actorId, followIri)` that build the deterministic `Accept`/`Reject` and post
   to `actorId.OutboxOf()`.
3. Sample UI `ActorDetail.razor` "Inbound follows" card: switch the Accept/Reject buttons to
   the new outbox-based client methods.
4. Tests: outbox-handler Accept/Reject (server), client Accept/Reject (client), sample UI
   screen (sample).

### Phase B — Remove the follow-decision + mute + relay Iris-only routes
1. Server: delete `LocalFollowDecisionHandler`, `CommunityFollowDecisionHandler`,
   `LocalMuteHandler`, `CommunityMuteHandler`, `LocalRelayHandler` routes + their handlers;
   fold the *core effect* (edge confirm/remove, mute add/remove, relay add/remove) into the
   outbox handler (Accept/Reject) / local moderation service respectively.
2. Client: move `AcceptFollowAsync`/`RejectFollowAsync`/`MuteAsync`/`UnmuteAsync`/
   `SubscribeRelayAsync`/`UnsubscribeRelayAsync` out of `IActivityPubClient` into a new
   `LocalModerationClient` (Basic-auth, non-AP routes). The core client no longer carries
   them.
3. Sample UI: update callers to the new client surface.
4. Tests: retire the endpoint-targeted tests; replace with outbox/local-moderation tests.

### Phase C — Fix the stale docs + final cleanup
1. Correct the `IActivityPubClient` XML docs (they say "InboxOf" but the code posts to
   "OutboxOf").
2. Sweep for any remaining Iris-only references; update COMPATIBILITY_MATRIX,
   LIVE_EVALUATION_CHECKLIST, LIVE_INTEROP_TEST_PLAN, ROADMAP, PLAN.
3. Full build + full test suite green; record the delta.

## 4. Open decisions (need your call)

- **D1 — Proxy relay (`/proxy/{**t}`):** keep as-is (it's a real browser CORS need, not AP),
  or also move off the `/ap/v1` tree? Recommend: keep, but it's the only non-AP POST left on
  the AP route tree.
- **D2 — Search (`/search`, `/c/{n}/search`):** keep (Mastodon-standard) or remove? Recommend:
  keep.
- **D3 — Feeds (`/u/{h}/feed`, `/c/{n}/feed`):** these are the "home timeline." Keep (common
  extension) — recommend keep.
- **D4 — Mute/relay modeling:** D4a (local Basic-auth, non-AP, recommended) vs D4b (custom
  `iris:Mute` activity via outbox).

## 5. Scope / churn estimate

- **Client:** 32 public methods; ~12 move out of the core interface (6 methods × 2 overloads).
  Stale doc comments on ~6 methods corrected.
- **Server:** 5 Iris-only route handlers removed (~600-800 lines), outbox handler gains 2
  branches (~80 lines).
- **Sample UI:** 1 page (`ActorDetail.razor`) + 1 client refactor.
- **Tests:** 19 test files reference the Iris-only surface (mostly integration). Expect to
  retire ~8-12 files and rewrite the rest to target the outbox / local-moderation path.
  Current suite: **1,260 passing** (baseline from change 160).

## 6. What does NOT change

- Deterministic IRIs (`/accepts/{follow}`, `/rejects/{follow}`, `/follows/{target}`,
  `/unfollows/{target}`) — preserved.
- The inbound `Follow`/`Accept`/`Reject` handlers (the follower-side edge model).
- OAuth, node info, health, all `GET` collections, inbox, outbox `GET`.
- The auto-accept path (already AP-native).
- The delivery/queue machinery.
