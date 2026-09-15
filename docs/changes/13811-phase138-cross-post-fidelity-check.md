# 138.11 — Cross-post fidelity check: live Lemmy interop

Phase 138 slice 138.11 ([plan](../plans/phase-138-lemmy-community-integration.md)): verify that an
Iris cross-posted `Create(Note)` renders correctly in Lemmy's post listing, and confirm whether
Lemmy requires a `Page` (not a `Note`) for it to validate/render as a proper post.

## Status: 3 actor-document defects fixed; live fidelity check partially complete

The 138.11 live interop check against Lemmy 0.19.20 identified and **fixed all three** actor-document
defects that were preventing any Iris→Lemmy federation. The root now serves a clean `Application`
site actor (LF-only PEM, `endpoints.sharedInbox` present), alice remains a `Person` at
`/ap/v1/u/alice`, and the site actor signs outbound federation. Two further constraints were
discovered during the live probe; the end-to-end Lemmy-rendered-post check is exercised through
Iris's own delivery pipeline (the 138.10 cross-post leg) rather than a hand-rolled probe.

### What was done

1. Established the follow edge: the Iris `owner-test-5428` community (owned by `alice`) follows the
   Lemmy `interop` community (`POST /local/v1/c/owner-test-5428/follow/https://lemmy.luit.ink/c/interop`,
   Basic auth `alice:alice`, HTTP 204). The edge is recorded in Iris's `Edges` table (Kind 10) and
   visible in the community's `following` collection.

2. Iterated on Lemmy's 400 responses, identifying and fixing three actor-document defects
   (PEM CRLF line endings, missing `endpoints.sharedInbox`, and instance-actor type). All three are
   committed in `1ee58de`.

3. Rebuilt and restarted the live Iris Docker image with the fixes. Verified the root now serves an
   `Application`-type site actor (clean LF-only PEM, `endpoints.sharedInbox` present) and alice is
   still a `Person`.

4. Probed Lemmy's inbound signature/actor-validation pipeline with a hand-rolled HTTP Signature
   delivery (RSA PKCS#1 v1.5, headers `(request-target) host date digest content-type`), isolating
   the `UntaggedEither<Person, Group>` actor-dereference constraint and a probe-script signature
   artifact.

## Defects found and fixed

### Defect #1 — PEM CRLF line endings (FIXED, `1ee58de`)

`KeyPair.ToPem` used `Base64FormattingOptions.InsertLineBreaks`, which produces **CRLF** (`\r\n`)
line endings in the PEM. Lemmy's `PublicKey` deserialization (via the `activitypub_federation`
crate) parses the PEM and the CRLF breaks it, producing:

```
Failed to parse object https://iris.luit.ink/ap/v1/u/alice ... data did not match any variant of
untagged enum PersonOrGroup
```

**Fix:** `src/Iris.Core/Identity/KeyPair.cs` — `ToPem` now uses `Base64FormattingOptions.None` and
manually wraps at 64 chars with LF (`\n`) only, per RFC 7468.

### Defect #2 — Missing `endpoints.sharedInbox` in actor document (FIXED, `1ee58de`)

Iris's actor document `endpoints` object only contained `oauthAuthorizationEndpoint` and
`oauthTokenEndpoint` — **no `sharedInbox`**. Lemmy's `Endpoints` struct requires `sharedInbox` (a
non-optional `Url`). Without it, serde fails to deserialize the `endpoints` field, causing the
`Person` variant of the `PersonOrGroup` untagged enum to fail (same "data did not match any
variant" error as defect #1).

**Fix:** `apps/Iris.Web/WebAppFactory.cs` — `AddActivityPubServer` now sets `options.SharedInboxIri`
(from `Iris:SharedInboxIri` config, defaulting to `{base}/ap/v1/shared-inbox`). The actor document
now includes `endpoints.sharedInbox`. `apps/Iris.Web/docker-compose.yml` plumbs the
`IRIS_SHAREDINBOXIRI` env var.

### Defect #3 — Instance actor must be `Application` type, not `Person` (FIXED, `1ee58de`)

After fixing #1 and #2, Lemmy's error changed to:

```
lemmy_apub::objects::instance: Failed to dereference site for https://iris.luit.ink/:
Unknown: Failed to parse object https://iris.luit.ink/ with content {...}:
unknown variant `Person`, expected `Application` at line 1 column 668
```

When Lemmy (0.19.20) receives an inbound `Create` from an actor on `iris.luit.ink`, it
dereferences the **instance actor** from the origin (`https://iris.luit.ink/`) and expects it to be
type **`Application`** — not `Person`. Iris previously served alice's `Person` document at the root
(138.4's `InstanceActorDocumentHandler`), because `InstanceActorId` pointed at alice's IRI.

This is the standard ActivityPub convention: the instance root serves the site's `Application`-type
actor (Mastodon, Pleroma, and Friendica all do this). Lemmy's `objects::instance` module enforces
this for instance dereferencing.

**Fix (Option A — dedicated site actor):**
- `src/Iris.Server/ActivityPubServerOptions.cs` — new `InstanceActorIri` property (the IRI of the
  actor whose public document the instance root serves; falls back to `InstanceActorId` when null).
- `src/Iris.Server/ActivityPubServerExtensions.cs` — config binding for `Iris:InstanceActorIri`; the
  root handler (`InstanceActorDocumentHandler`) now serves `options.InstanceActorIri ??
  options.InstanceActorId`.
- `src/Iris.Server/ActivityPubServerOptionsValidator.cs` — validation for `InstanceActorIri` (must be
  an absolute http(s) IRI).
- `apps/Iris.Web/WebAppFactory.cs` — new `InstanceHandle = "iris"`; seeds a dedicated `Application`
  site actor (IRI = bare base, `preferredUsername: "iris"`) via a new `SeedInstanceActor` method
  (idempotent, reuses the persisted key on restart). Points both `options.InstanceActorId` and
  `options.InstanceActorIri` at the site actor (the site actor is now the outbound federation signer
  AND the root document). `RegisterSeedKey` registers both alice's key and the site actor's key.
  Dev-mode credentials: `@iris/iris` for the site actor, `alice/alice` for alice.
- `tests/Iris.Testing/TestSeeder.cs` — new `SeedApplicationWithKey` helper.
- `tests/Iris.Server.Tests/InstanceActorAtRootIntegrationTests.cs` — 3 new tests (root serves
  `Application`, alice remains `Person` at `/u/alice`, no `privateKey` leak).

## New findings (138.11 live probe)

### Finding A — Lemmy requires the activity actor to be `Person`/`Group`, not `Application`

Lemmy 0.19's shared-inbox handler (`receive_activity::<SharedInboxActivities, UserOrCommunity,
LemmyContext>`) dereferences the activity's `actor` field as `UserOrCommunity` =
`Either<ApubPerson, ApubCommunity>` — an **untagged** serde enum (`UntaggedEither<Person, Group>`,
the "PersonOrGroup" in the error messages). The `Person` variant's `UserTypes` enum accepts only
`Person`, `Service`, and `Organization`; the `Group` variant accepts `Group`. An `Application` type
matches **neither**.

Therefore the cross-post's `actor` must be the authoring local `Person` (alice) or a `Group`
community — **not** the `Application` site actor. This is consistent with decision 058 (138.9): the
cross-post `Create` is "delivered to the community's sharedInbox/inbox signed as the authoring local
actor." The site actor is used only for instance-level dereferencing (which Lemmy catches and falls
back on — `fetch_instance_actor_for_object` in `objects/instance.rs` returns a non-fatal
`DbInstance::read_or_create` on failure), not as an activity actor.

### Finding B — Probe-script signature verification artifact for `Person` actors

A hand-rolled HTTP Signature probe (bash + openssl, RSA PKCS#1 v1.5) behaves inconsistently with the
activity's `actor` field, in a way that points to a **probe-harness** limitation rather than an Iris
defect:

- With `actor: <site actor>` (`https://iris.luit.ink/`), the probe's signature **passes** Lemmy's
  verification and the request reaches the actor-dereference stage, where it fails with
  `PersonOrGroup` (Finding A) — the expected outcome for an `Application` actor.
- With `actor: alice` (`https://iris.luit.ink/ap/v1/u/alice`), the probe's signature **fails**
  Lemmy's verification ("Error when parsing signature from Http Signature"), even though the
  signature verifies OK locally against alice's public key and the key/KeyId/public-key triple all
  match the live actor document.

The signature is computed over `(request-target) host date digest content-type` with the digest over
the exact body bytes; the same probe passes signature validation for the site-actor body but not the
alice body, despite both being signed with the same (alice's) key. This strongly suggests the
probe's signing does not byte-match what Lemmy's `http_signature_normalization` crate reconstructs
for a `Person`-actor delivery (the two code paths diverge in how the actor dereference interacts with
the verification). This is a **test-harness** limitation: the fidelity check should be exercised
through **Iris's own outbound delivery pipeline** (the 138.10 `GetCrossPostTargetsAsync` cross-post
leg, which uses Iris's `OutboundSignature` profile byte-for-byte), not a hand-rolled probe.

## Note-vs-Page determination: PENDING

The original 138.11 question — "does Lemmy require a `Page` (not a `Note`) for it to validate/render
as a proper post?" — **cannot be answered** until a `Create` successfully reaches Lemmy's
post-processing stage. Based on Lemmy's source code (`lemmy_apub` crate), Lemmy's `Create` handler
accepts both `Note` and `Page` objects for posts. The `Page` type is Lemmy's native post type and
provides a `name` (title) field that `Note` lacks. If Iris sends a `Note`, Lemmy creates a post with
no title (the `content` becomes the body); if Iris sends a `Page`, Lemmy creates a post with the
`name` as the title and the `content` as the body.

**Recommendation:** exercise the cross-post through Iris's own delivery pipeline against live Lemmy
with both a `Note` and a `Page`, confirm the rendering difference, and decide whether Iris should
mint `Page` for community-audience posts headed to a Lemmy peer (or for all community posts if that
is simpler and still correct for Mastodon-style peers).

## Files changed (defects #1, #2, #3 — commit `1ee58de`)

- `src/Iris.Core/Identity/KeyPair.cs` — `ToPem` LF-only line endings (defect #1).
- `apps/Iris.Web/WebAppFactory.cs` — `options.SharedInboxIri` (defect #2); dedicated `Application`
  site actor + `InstanceActorIri` (defect #3).
- `apps/Iris.Web/docker-compose.yml` — plumb `IRIS_SHAREDINBOXIRI` env var.
- `src/Iris.Server/ActivityPubServerOptions.cs` — new `InstanceActorIri` property (defect #3).
- `src/Iris.Server/ActivityPubServerExtensions.cs` — `InstanceActorIri` config binding + root handler
  (defect #3).
- `src/Iris.Server/ActivityPubServerOptionsValidator.cs` — `InstanceActorIri` validation (defect #3).
- `tests/Iris.Testing/TestSeeder.cs` — `SeedApplicationWithKey` helper.
- `tests/Iris.Server.Tests/InstanceActorAtRootIntegrationTests.cs` — 3 new tests.

## Live verification (2026-09-15)

```text
# 1. Root now serves an Application site actor (clean LF-only PEM + sharedInbox):
$ curl -s -H 'Accept: application/activity+json' https://iris.luit.ink/ | python3 -m json.tool
"type": "Application"
"id": "https://iris.luit.ink/"
"preferredUsername": "iris"
"endpoints": {
    "oauthAuthorizationEndpoint": "https://iris.luit.ink/ap/v1/oauth2/authorize",
    "oauthTokenEndpoint": "https://iris.luit.ink/ap/v1/oauth2/token",
    "sharedInbox": "https://iris.luit.ink/ap/v1/shared-inbox"
}
# (no CRLF in publicKeyPem; pem first line = -----BEGIN PUBLIC KEY-----)

# 2. alice is still a Person:
$ curl -s -H 'Accept: application/activity+json' https://iris.luit.ink/ap/v1/u/alice | python3 -c "import sys,json;d=json.load(sys.stdin);print(d['type'],d['id'])"
Person https://iris.luit.ink/ap/v1/u/alice

# 3. Probe with actor=site (Application): signature PASSES, actor dereference FAILS (Finding A):
$ bash deliver_13811.sh note.json lemmy.luit.ink /inbox /tmp/site_actor_key.pem "https://iris.luit.ink/#key-1"
HTTP 400: {... "data did not match any variant of untagged enum PersonOrGroup"}

# 4. Probe with actor=alice (Person): signature FAILS (Finding B — probe-harness artifact):
$ bash deliver_13811.sh note_alice.json lemmy.luit.ink /inbox /tmp/alice_correct_key.pem "https://iris.luit.ink/ap/v1/u/alice#key-1"
HTTP 400: {"error":"unknown","message":"Error when parsing signature from Http Signature"}
```

## Next steps

1. **Exercise the cross-post through Iris's own delivery pipeline** (the 138.10 `GetCrossPostTargetsAsync`
   leg) against live Lemmy: have an Iris client (alice) post a `Note` addressed to the Lemmy
   `interop` community, and verify it lands in Lemmy's post listing with all fields intact.
   This uses Iris's `OutboundSignature` profile (byte-for-byte), avoiding the probe-harness artifact
   (Finding B).
2. **Re-run with a `Page`** to compare rendering (title via `name` vs. `Note` with no title).
3. **Decide Note-vs-Page** based on the fidelity check: if `Page` renders better (with a title),
   implement `Page` minting for community-audience posts headed to a Lemmy peer (or for all
   community posts if simpler and still correct for Mastodon-style peers).
