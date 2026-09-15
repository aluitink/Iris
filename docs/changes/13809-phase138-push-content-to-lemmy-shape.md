# 138.9 — Push-content-to-Lemmy shape (decision)

**Phase 138 (Lemmy community integration), slice 138.9.** A decision slice: records *how* an Iris-authored post should be addressed and delivered so it appears inside a peered Lemmy community's own post list, unambiguously, so 138.10 has an implementation target. No code change.

## What was decided

An Iris-authored post that should land in a remote (non-Iris) community's post list is a **cross-post**, expressed with the existing ActivityStreams addressing + delivery machinery:

1. **Audience** — the target community's `Group` IRI goes in the post's `to` (direct recipient, primary destination — the same slot a Lemmy client uses when cross-posting). The normal follower `cc` fan-out is unchanged.
2. **Delivery** — the `Create` is delivered to the target community's inbox (its advertised `endpoints.sharedInbox`, else `communityIri + "/inbox"`; for Lemmy, the instance-level shared inbox), signed as the authoring local actor — mirroring the existing `OutboxPublishHandler` fan-out.
3. **Object shape** — a standard `Note` for now. Note-vs-`Page` is explicitly deferred to 138.11 (cross-post fidelity check).
4. **Mechanism** — the cross-post target is an explicit, client-supplied community IRI; Iris does not implicitly push posts into communities it follows (a follow is a pull, decision 036 / 89.1).
5. **Idempotency** — the cross-post is an additional delivery of the same once-minted `Create` (decision 055); no separate activity id.

The full rationale (context, three considered alternatives, consequences) lives in [decision 058](../decisions/058-outbound-post-to-peered-community-shape.md).

## Why a decision doc

This has real weight: multiple viable alternatives were considered (implicit fan-out, reuse-the-follower-set, explicit cross-post), it has spec/interop implications (ActivityStreams `to`/`cc` addressing, the cross-post convention Lemmy/Mastodon expect), and it is referenced by 138.10 (implementation) and 138.11 (fidelity). Per the [decisions conventions](../decisions/README.md), it gets its own document.

## Key types referenced (no new types)

- `IDeliveryService.DeliverToActorAsync` (src/Iris.Server/Delivery/IDeliveryService.cs) — resolves the recipient's `sharedInbox`/`inbox` and signs as the acting local actor.
- `OutboxPublishHandler` (src/Iris.Server/ActivityPubServerExtensions.cs:3225) — the existing outbound Create fan-out; the cross-post leg mirrors its delivery shape.
- `RewriteOutboundAudienceAsync` (src/Iris.Server/ActivityPubServerExtensions.cs:5175) — the existing `to`/`cc` rewrite; the cross-post adds the target community to `to`.

## Tests

None (decision-only slice — no code). The 138.10 implementation slice carries the integration coverage (the post appears in Lemmy's own post listing).
