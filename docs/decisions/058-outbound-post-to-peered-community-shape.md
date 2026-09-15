# 058 — Outbound post-to-peered-community shape (cross-post)

> Resolved 2026-09-15. Introduced by slice 138.9 of [Phase 138](../plans/phase-138-lemmy-community-integration.md) (Lemmy community integration). See [change 13809](../changes/13809-phase138-push-content-to-lemmy-shape.md).

## Context

A community *follow* (decision 036, Phase 89.1) is a **pull** relationship: the follower subscribes to the followed side's content, and the followed side's posts flow into the follower's feed. It does **not** push the follower's own posts into the followed community. So when an Iris community follows a Lemmy community (the 138.6 mutual peering), Lemmy's posts appear in the Iris community's feed — but an Iris-authored post does **not** appear in the Lemmy community's own post list.

For an Iris-authored post to appear *inside* a peered Lemmy community's post list (the 138.10 check), Iris must do what any Fediverse client does when **cross-posting**: address the target community in the activity's audience and deliver the `Create` to that community's inbox. This decision records the exact shape so 138.10 has an unambiguous implementation target.

## Decision

An Iris-authored post that should land in a remote (non-Iris) community's post list is a **cross-post**, and it is expressed with the existing ActivityStreams addressing + delivery machinery:

1. **Audience.** The target community's `Group` IRI is placed in the post's `to` (direct recipient — the community is the primary destination, not a carbon copy). This is the same slot a Lemmy client uses when cross-posting: the target community is a direct addressee. The post's normal follower `cc` fan-out is unchanged (the post still reaches the author's own followers).

2. **Delivery.** The `Create` is delivered to the target community's inbox — resolved exactly like any other actor delivery: the community's advertised `endpoints.sharedInbox` when present, otherwise `communityIri + "/inbox"`. For a Lemmy community the `sharedInbox` is the instance-level `https://lemmy.luit.ink/inbox`. Delivery is signed as the authoring local actor (the Iris community or person authoring the post), mirroring the existing `OutboxPublishHandler` fan-out.

3. **Object shape.** The post is a standard `Note` (Iris's current community/post object). Minting a `Page` for Lemmy-bound posts is **out of scope for this decision** — it is an open question explicitly deferred to 138.11 (cross-post fidelity check), which will verify whether Lemmy validates/renders a `Note` as a proper community post or requires a `Page`. The decision here fixes only the addressing + delivery shape, which is orthogonal to the object type.

4. **Mechanism.** The cross-post target is an explicit, client-supplied address (the operator or author names the remote community IRI they are cross-posting to), not an implicit derivation. Iris does **not** infer "this community follows a Lemmy community, so push every post there." A follow is a pull; a cross-post is a deliberate outbound act. The target must be a known remote community (resolvable to a `Group` with an inbox).

5. **Idempotency / dedup.** A cross-post is a distinct delivery of the same `Create` activity to an additional recipient's inbox; the activity's id is minted once (decision 055) and is the same on the author's outbox and in the cross-post delivery. No separate activity id is minted for the cross-post leg.

## Alternatives considered

### 1. Implicit fan-out: every post to a following community is auto-pushed to communities it follows

Rejected. A community *follow* is a pull (036 / 89.1). Auto-pushing every post into every followed remote community conflates "I subscribe to you" with "post to me" — it would flood a followed community with the follower's content without the follower's or the followed community's explicit intent. The Fediverse has no such implicit-push semantic; cross-posting is always an explicit act.

### 2. Reuse the follower fan-out (treat the Lemmy community as a "remote follower")

Rejected as the *primary* mechanism. The 138.6 mutual peering does record the Lemmy community as a follower of the Iris community, so a plain follower fan-out would deliver the post to Lemmy's shared inbox. But (a) that delivery is addressed as a `cc`-style follower copy, not a direct `to` cross-post, so Lemmy may treat it as a follower copy rather than a first-party community post; (b) it couples "who follows us" to "where our posts land as community posts," which is the conflation alternative 1 warns against. The explicit cross-post (alternative 3) is the correct, spec-aligned shape. The follower fan-out remains the *correct* mechanism for reaching followers; the cross-post is an *additional*, explicit delivery to the target community.

### 3. Explicit cross-post (chosen)

The target community is named in `to` and the `Create` is delivered to its inbox, exactly as a Lemmy (or Mastodon) client cross-posts. This is unambiguous, matches the receiving platform's expectation of a first-party community post, and does not conflate follow state with posting intent.

## Consequences

- **Enables:** 138.10 (implement + exercise the post-to-Lemmy-community delivery path) has a concrete, spec-aligned target: address the Lemmy community in `to`, deliver the `Create` to its `sharedInbox`/`inbox`, sign as the authoring local actor. The post should then appear in Lemmy's own post listing attributed to the Iris actor.
- **Costs:** a new outbound addressing + delivery path (an explicit cross-post leg, distinct from the follower fan-out). The operator/author must name the target community IRI.
- **Constrains:** the object-type question (Note vs Page) is deliberately left open for 138.11. If Lemmy requires a `Page`, that is a separate change (mint `Page` for community-audience posts headed to a Lemmy peer) and does not alter the addressing/delivery shape decided here.
- **No new state:** the cross-post is a one-time delivery of an already-minted activity; it records no new follow/membership edge. The receiving community's acceptance of the post is its own local concern (Lemmy stores the post in its own post list).
