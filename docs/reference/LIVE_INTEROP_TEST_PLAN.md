# Live Interop Test Plan (Phase 19.1 + 19.2)

> The **execution guide** for the live interop + real-world-Mastodon waypoints, driven by the real
> public account `@RayvenMX@mastodon.world`. The operator (the user) performs the manual actions from
> the Mastodon account; the agent observes the wire + UI and records the outcome. This doc is the
> standing routine: each turn = bring the stack up clean, hand the operator the next action, observe,
> record. A fresh session can pick up from the **Current position** section below.
>
> Companion docs: `LIVE_EVALUATION_CHECKLIST.md` (the waypoint→UI-path map), `COMPATIBILITY_MATRIX.md`
> §5 (predicted gaps to re-check), `ROADMAP.md` Phase 19.1/19.2 (status + `remaining:`).

## 0. Environment + ground rules

- **Stack**: `docker compose -f docker-compose.yml up --build -d` → `iris-a` (iris-dev1, alice),
  `iris-b` (iris-dev2, alice), `iris-ui` (the Blazor explorer). Health: `docker compose ps`.
- **Public FQDNs** (external reverse proxy → our listening ports; we only need to be up):
  - iris-a / dev1: `https://iris-dev1.luit.ink` → local 8081
  - iris-b / dev2: `https://iris-dev2.luit.ink` → local 8082
  - UI: local 8090 / 8088 (the public UI origin is a separate host; confirm with the operator).
- **Persistence (verified, change 161-preface)**: `Iris__PersistenceDirectory=/data` on named volumes
  `iris-a-data`/`iris-b-data`. A `down` **without** `-v` preserves state (actors, keys, follows,
  activities, outboxes all round-trip). A `down -v` wipes it (fresh re-seed). **Never `down -v` between
  live turns** unless intentionally resetting.
- **Key identity is stable across a `down`/`up` (no `-v`)**: `keys.json` persists, so
  `…/alice#key-1` does not change. (The `IrisSigner`-dumped private PEM at
  `/tmp/iris-alice-key.pem` is **regenerated per boot** in the container's local fs — it is *not* on
  the volume, so it is not needed for persistence.)
- **`Iris__ManuallyApprovesFollowers=true` on iris-a**: alice does **NOT** auto-accept an inbound
  follow. An inbound follow from RayvenMX sits **pending** (a provisional edge) until the operator
  Accepts/Rejects it by publishing an `Accept`/`Reject` to alice's outbox (see §1).
- **RayvenMX IRI**: `https://mastodon.world/users/RayvenMX` (the actor doc IRI Mastodon serves).
- **Follow decision — AP-native (Phase 19.0b)**: the operator Accepts/Rejects an inbound follow of a
  local actor by publishing a deterministic `Accept`/`Reject` activity to the **followed actor's own
  outbox** — NOT via the removed `/ap/v1/u/{handle}/follows/{followId}` Basic-auth endpoint (that
  endpoint was removed; the outbox is the sole write path, and the server records + server-delivers the
  decision).
  - Accept: `POST https://iris-dev1.luit.ink/ap/v1/u/alice/outbox` with an `Accept` whose `object` is
    `{followIri}` (HTTP-signed as alice — the client's `AcceptAsync` does this; see F1).
  - Reject: `POST https://iris-dev1.luit.ink/ap/v1/u/alice/outbox` with a `Reject` whose `object` is
    `{followIri}`.
  - `{followIri}` = the absolute IRI of the original `Follow` activity (read from
    `docker exec iris-a cat /data/activities.json` — the Follow's `id`, i.e.
    `https://mastodon.world/users/RayvenMX/follows/https://iris-dev1.luit.ink/ap/v1/u/alice`).
- **Recording**: after each item, record the outcome in the **Findings tracker** (§6) with wire
  evidence (the relevant collection / outbox / inbox JSON + the Mastodon public URL if applicable).
  PASS / FAIL / GAP + a one-line note. Findings feed 19.4 (remediation).

## 1. Standing pre-flight (every turn)

1. `docker compose ps` → all three `healthy`. If down: `up --build -d` + wait for health.
2. `curl https://iris-dev1.luit.ink/.well-known/webfinger?resource=acct:alice@iris-dev1.luit.ink`
   → 200, actor IRI present. Same for dev2. (Proves the external proxy path is live.)
3. Confirm no stale RayvenMX edges from a wiped volume:
   `docker exec iris-a grep -c mastodon.world /data/follows.json /data/activities.json` (expect 0 on
   a fresh volume).
4. Note the seeded state: alice's outbox has a seeded `Note` (`…/notes/1`) + any prior follows.

## 2. Phase 19.1 — Follow scenarios F1–F4 (the active slice)

Each item: operator action → agent observes wire + UI → record.

### F1 — They follow us → we Accept
- **Operator**: from Mastodon, `@RayvenMX` follows `alice@iris-dev1.luit.ink`.
- **Agent observes**:
  - `docker exec iris-a cat /data/activities.json` → a `Follow` from
    `https://mastodon.world/users/RayvenMX` object `…/alice` is recorded (the inbound follow).
  - `docker exec iris-a cat /data/follows.json` → a **provisional** edge
    `RayvenMX → alice@dev1` (pending, since manually-approves is on).
  - UI: alice's followers (dev1) lists RayvenMX (pending or accepted — record which).
- **Agent acts** (the operator's decision, mirrored to the wire): publish a deterministic `Accept`
  (object = `{followIri}`) to alice's outbox — `POST …/ap/v1/u/alice/outbox` (HTTP-signed as alice; the
  client's `AcceptAsync` does this). The server records it and server-delivers it to RayvenMX's inbox.
- **Verify on the wire**: alice's outbox gains the deterministic `Accept`
  (`…/alice/accepts/{followIri}`); the `Accept` is **server-delivered** to RayvenMX's inbox (Mastodon
  finalizes the edge). Confirm the delivery was scheduled (delivery queue) and, if possible, that
  Mastodon's UI now shows the follow as accepted (not pending on their side).
- **Pass criteria**: inbound Follow recorded + provisional edge + Accept published to outbox +
  delivered to their inbox. **GAP/FAIL** if the inbound Follow is not recorded, the Accept is not
  signed/delivered, or Mastodon does not finalize.

### F2 — We follow them → their Accept arrives
- **Operator/agent**: we follow RayvenMX. Agent signs a `Follow` (IrisSigner, alice's key) and
  `POST`s it to alice's outbox on dev1; the server delivers it to RayvenMX's inbox.
- **Verify on the wire**:
  - dev1 outbox gains the `Follow` (object `https://mastodon.world/users/RayvenMX`).
  - The delivery to Mastodon returns **202** (signature accepted — the F-1912-1 SHA-256-digest fix).
  - RayvenMX's `Accept` arrives at our inbox → recorded; alice's `following` collection lists
    RayvenMX; the edge is confirmed.
- **Pass criteria**: our Follow is accepted (202) and their Accept is recorded. (ROADMAP note: F2 was
  previously PASS for the signature; re-confirm on the fresh volume.)

### F3 — Reject behavior
- **Operator**: RayvenMX sends a *second* follow (or we use a fresh local test) that we **Reject** by
  publishing a deterministic `Reject` (object = `{followIri}`) to alice's outbox —
  `POST …/ap/v1/u/alice/outbox` (HTTP-signed as alice; the client's `RejectAsync` does this).
- **Verify on the wire**: the deterministic `Reject` (`…/alice/rejects/{followIri}`) is recorded in
  alice's outbox and **server-delivered** to RayvenMX's inbox; the provisional edge is removed
  locally. Observe whether Mastodon honors the `Reject` (their UI should not show the follow).
- **Pass criteria**: `Reject` published + delivered + local edge removed. Record whether Mastodon
  honors it (GAP if they ignore it).

### F4 — Unfollow via Undo
- **Agent**: we unfollow RayvenMX → an `Undo` of the original `Follow` is published to alice's outbox
  and delivered to RayvenMX's inbox.
- **Verify on the wire**: the `Undo` is in alice's outbox; the local `following` edge to RayvenMX is
  removed; observe whether Mastodon removes the relationship on their side (their profile / our
  follower count).
- **Pass criteria**: `Undo` published + delivered + local edge removed. Record Mastodon's response.

## 3. Phase 19.1 — Post/receive C1–C4 + 19.2 (the "federate a post" proof)

### C1 / 19.2.2 — We post → Mastodon renders it (the core proof)
- **Agent**: compose a public `Note` (UI or a signed `Create` to alice's outbox on dev1). The server
  delivers a signed `Create` to RayvenMX's inbox (and any public followers).
- **Verify on the wire**: the `Create` is in alice's outbox + delivered (202). Then **on
  mastodon.world**, the note is visible (public post URL / RayvenMX's timeline if they follow us, or
  a direct fetch of our note's IRI from their side). This is the headline "post and have it
  federate" check.
- **Pass criteria**: our `Create` accepted by Mastodon + the note renders on mastodon.world.

### C2 / 19.2.1 — RayvenMX posts → we receive + store it
- **Operator**: RayvenMX posts a public toot (mentioning / addressed to us, or we are following them).
- **Agent observes**: the `Create` arrives at our inbox → stored (`/data/activities.json` +
  `objects.json`); the object is fetchable by IRI (explorer object view); it appears in the correct
  local surface (followed feed / community feed). Verify the inbound signature (RSA-SHA256) validates
  and the content-type is accepted.
- **Pass criteria**: inbound `Create` stored + fetchable + surfaced locally.

### C3 — Reply / thread (19.2.4 baseline)
- **Operator/agent**: build a 3-level thread (our note → their reply → our reply-to-reply). Verify
  `inReplyTo` chains render on mastodon.world **and** in our object view.

### C4 — Extended-type object round-trip
- **Operator**: RayvenMX posts a toot with `sensitive`/`spoilerText` (or a media post if available).
  Verify our server stores + renders it without rejection (Mastodon extension passthrough).

## 4. Phase 19.1 — SIG / P-T / G / S (secondary, as time allows)

- **SIG1–SIG5**: inbound RSA-SHA256 validates (no 401); Ed25519 inbound (if a target uses EdDSA);
  unsigned POST rejected 401; our ServerToServer profile (with `digest`) accepted by Mastodon;
  unsigned GETs both ways.
- **P1–P2 / T1–T3**: a Mastodon client pages our outbox (`?page`/`?limit`); we page their outbox;
  we serve `application/activity+json`; we accept `application/ld+json` + extended `@context`.
- **G1/G3**: RayvenMX follows our `iris` community → we Accept by publishing a deterministic `Accept`
  (object = `{followIri}`) to the community's outbox (`POST /ap/v1/c/iris/outbox`, HTTP-signed as the
  community; the community outbox's `Accept` branch records the decision) → they appear in
  members/followers. G2/G4 tabled (record current behavior).
- **S1–S2 / nodeinfo**: our nodeinfo + webfinger consumable by mastodon.world; our global search
  (`/ap/v1/search`) lists local actors + content; we fetch their profile via the explorer.

## 5. Phase 19.2 — Object-shape conformance + delete/moderation

- **19.2.3**: fetch the same object from our server + mastodon.world; diff `@context`,
  `attributedTo`/`to`/`cc`, `content` HTML, `url`, `published`/`updated`, `inReplyTo`, `tag`,
  `attachment`, `sensitive`, `spoilerText`. Enrichment allowed **only while conformance holds**.
- **19.2.5**: delete one of our posts → `Delete` propagates (Mastodon tombstone); block/flag (federated
  moderation) → record what Mastodon honors; Undo of like/unfollow propagates.

## 6. Findings tracker (19.1 / 19.2)

> Record each item as it is executed. Status: **PASS** / **FAIL** (broken) / **GAP** (predicted,
> confirmed) / **BLOCKED** (needs operator / external action). Findings promote to 19.4 (remediation)
> with repro + wire evidence.

| # | Waypoint | Item | Status | Wire/UI evidence | Mastodon URL / note |
|---|---|---|---|---|---|
| — | 19.1.2 F1 | RayvenMX follows us → we Accept | pending | | |
| — | 19.1.2 F2 | We follow them → their Accept | pending (prior PASS: signature) | | |
| — | 19.1.2 F3 | Reject behavior | pending | | |
| — | 19.1.2 F4 | Unfollow via Undo | pending | | |
| — | 19.1.3 / 19.2.2 C1 | We post → Mastodon renders | pending | | |
| — | 19.1.3 / 19.2.1 C2 | RayvenMX posts → we store | pending | | |
| — | 19.1.4 SIG1–5 | Signature scenarios | pending | | |
| — | 19.1.5 P/T | Pagination + content types | pending | | |
| — | 19.1.6 G1/G3 | Community follow | pending | | |
| — | 19.1.7 S | Discovery / nodeinfo | pending | | |
| — | 19.2.3 | Object-shape conformance | pending | | |
| — | 19.2.4 | Threads / replies | pending | | |
| — | 19.2.5 | Delete / moderation | pending | | |

## Current position (for a fresh session)

- **Stack**: up + healthy; public FQDNs reachable (external proxy → our ports).
- **Volumes**: fresh (the prior `down -v` wiped state; re-seeded). No RayvenMX edges present yet.
- **Persistence**: **confirmed working** across `down` (no `-v`) / `up` (named volumes + file-backed
  stores round-trip; md5-verified on 7/8 stores, the seeded cross-instance Follow + federated edge
  survived). A `down -v` is the only thing that resets state.
- **Next action for the operator**: **F1** — from Mastodon, `@RayvenMX` follows
  `alice@iris-dev1.luit.ink`. Then the agent records the inbound Follow, the operator (via the agent)
  Accepts it, and the Accept's delivery is verified. After F1: F2 (we follow them), F3 (reject), F4
  (unfollow), then C1 (the post-federates proof), then C2 (their post → us).
- **CI server work (19.6.1)**: deferred until the live 19.1/19.2 slice stabilizes.
