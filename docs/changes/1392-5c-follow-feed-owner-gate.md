# 139.2-s5c — Gate the follow feed to the actor's owner

**Status:** Done.
**Closes:** the authz gap flagged in 139.2-s5 (Deferred surface #1): "Gating the *request itself*
to the owner (403/404 for a non-owner fetching someone else's feed) is a separate authz question."

## What was built

The follow feed (`GET /ap/v1/u/{handle}/feed`) is a **private** view — it shows the actor's own
non-public posts (DMs, followers-only) and any non-public items addressed to them. Previously, any
authenticated actor (or even an anonymous request) could fetch *anyone's* follow feed, which meant
a non-owner could see the owner's DMs and followers-only content.

### The gate

In `FollowFeedHandler` (`ActivityPubServerExtensions.cs`), after resolving the requester via
`ResolveAuthenticatedRequesterAsync`:

```csharp
if (requesterIri is null || requesterIri != actorIri)
{
    return Results.StatusCode(403);
}
```

- **Anonymous / unsigned** → 403 (no valid signature to identify the requester).
- **Signed as a different actor** → 403 (not the owner).
- **Signed as the actor themselves** → 200 (the owner, full visibility via the S5 visibility filter).

This is a **request-level** gate (authz), not a **content-level** filter (authn/visibility). The S5
visibility filter still applies to the response body; the S5c gate ensures only the owner reaches
that code path.

### Why 403, not 404

A 404 would hide the feed's existence from non-owners (a common hardening choice). A 403 is
deliberate: the feed IRI is a well-known, predictable endpoint (`/ap/v1/u/{handle}/feed`), so
hiding it adds no security. A 403 is more informative for debugging and matches the existing
`ResolveAuthenticatedRequesterAsync` pattern (which returns 403 for invalid signatures on other
endpoints).

### Federation impact

**None.** Remote instances federate via the **public outbox** (`/ap/v1/u/{handle}/outbox`), not the
follow feed. The outbox is public (no authz gate) and contains only the actor's published
activities. The follow feed is a client-side aggregation endpoint (own outbox + follows' outboxes)
used by the WASM Blazor client to render the home timeline. It is not a federation surface.

### Client impact

**None.** The WASM Blazor client signs every request with the session actor's key
(`SigningHandler` with `ClientToServer` profile). When the logged-in user fetches their own feed,
the signature resolves to their actor IRI, which matches the feed owner → 200. No client changes
needed.

## Test counts

Updated 11 test files to sign requests as the feed owner (or assert 403 for non-owners/anonymous):

- `FollowFeedIntegrationTests.cs` — added a `FollowFeedRoutingFetcher` (routes actor-doc fetches by
  host so the inbound signature validator can resolve the owner's key). Added 2 new tests:
  `Feed_NonOwner_SignedAsOtherActor_Returns403` and `Feed_Anonymous_Returns403`. Updated existing
  tests to use a signed `IActivityPubClient` via `GetObjectAsync`.
- `FollowFeedTypeProbeTests.cs` — 6 tests updated to sign as the actor.
- `FollowFeedOutboxCachingIntegrationTests.cs` — 1 test updated to sign as the actor.
- `FlagsCollectionIntegrationTests.cs` — 1 test updated.
- `BlocksCollectionIntegrationTests.cs` — 2 tests updated.
- `MutesCollectionIntegrationTests.cs` — 1 test updated.

Verification: full solution build clean (0 warnings / 0 errors); full `Category!=Slow` suite green —
**2,251 tests, 0 failed** across 11 projects (Iris.Server.Tests now 1,349, +4 new tests).

## Decisions

- **403, not 404.** The feed IRI is predictable; hiding it adds no security. 403 is more
  informative and consistent with the codebase's existing authz patterns.
- **Gate in the handler, not the service.** The service (`FeedService`) is a pure data-fetching
  component; authz is the handler's responsibility. This keeps the service testable without
  signature-validation infrastructure.
- **No federation impact.** The outbox (the federation surface) is unaffected. The follow feed is
  a client-side aggregation endpoint, not a wire protocol surface.
