# 138.26 — Community-level NSFW alignment

## What changed

Added the `IrisDocumentExtensions.RequiresCw` client helper, which combines a content object's
own `sensitive` flag with its source community's `iris:communityNsfw` flag to determine whether a
CW (content warning) overlay should be rendered.

## Design decision

**Client-side CW derivation; no server-side retro-apply.**

The question: when Iris syncs a post from a Lemmy community that is flagged NSFW at the community
level, should Iris retro-apply the `sensitive` flag to every synced post?

**Decision: No.** The server does NOT mutate stored posts. Instead, the client derives the
effective CW state at render time by combining the two flags via `RequiresCw(content, community)`.

**Rationale:**

1. **Data model cleanliness.** A community's NSFW flag can change later (an admin un-flags it).
   Retro-applying `sensitive` to already-stored posts would be wrong — the posts would remain
   flagged even after the community is no longer NSFW. Client-side derivation is always correct
   because it reads the current community state.

2. **Simplicity.** The implementation is a single boolean helper in the client library. No changes
   to the server's object store, community feed service, or document builders are needed.

3. **Existing infrastructure.** The `iris:communityNsfw` term is already rendered on community
   documents (138.25) and readable via `GetCommunityNsfw()` (138.24). The `IsSensitive()` helper
   already reads the per-post `sensitive` flag. `RequiresCw` simply combines the two.

4. **Lemmy parity.** Lemmy's own UI applies the community-level NSFW flag client-side: when a
   community is flagged NSFW, Lemmy shows a CW gate on all its posts in the UI, even though
   individual posts may not carry the per-post `sensitive` flag. Iris mirrors this behavior.

## Files changed

| File | Change |
|---|---|
| `src/Iris.Client/IrisDocumentExtensions.cs` | Added `RequiresCw(IObject content, IObject? community, string namespaceIri)` public static method. |
| `tests/Iris.Server.Tests/CommunityNsfwAlignmentIntegrationTests.cs` | 8 integration tests (new). |

## API

```csharp
public static bool RequiresCw(IObject content, IObject? community, string namespaceIri = DefaultNamespaceIri)
```

- Returns `true` when the object's own `sensitive` flag is `true` **OR** the community's
  `iris:communityNsfw` flag is `true`.
- When `community` is null (person-to-person content, not community-associated), only the
  object's own `sensitive` flag is considered.
- When the community is present but does not carry `iris:communityNsfw` (locally-created or
  non-Lemmy source), the community flag is treated as `false`.

## Tests

8 integration tests in `CommunityNsfwAlignmentIntegrationTests`:

1. **`ObjectSensitive_CommunityNotNsfw_RequiresCw`** — per-post sensitive, clean community → CW.
2. **`ObjectNotSensitive_CommunityNsfw_RequiresCw`** — clean post, NSFW community → CW.
3. **`ObjectSensitive_CommunityNsfw_RequiresCw`** — both flags set → CW.
4. **`ObjectNotSensitive_CommunityNotNsfw_NoCw`** — neither flag set → no CW.
5. **`ObjectNotSensitive_CommunityNull_UsesObjectFlagOnly_NoCw`** — no community, no per-post flag → no CW.
6. **`ObjectSensitive_CommunityNull_RequiresCw`** — no community, per-post flag → CW.
7. **`CommunityNsfwFalse_ObjectNotSensitive_NoCw`** — community explicitly non-NSFW → no CW.
8. **`FullRoundTrip_NsfwCommunityPost_StoredAndRequiresCw`** — full round-trip: seed community with
   bare `sensitive` key, render `iris:communityNsfw`, verify `RequiresCw` returns true for a post
   from that community.

All 8 pass. Full suite: 1279 passed, 0 real failures (1 known flaky).
