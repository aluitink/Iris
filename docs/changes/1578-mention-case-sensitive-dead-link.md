# 1578 — S12a: Case-sensitive same-instance mention → dead link

**Severity:** S2
**Status:** Fixed

## Problem

Typing `@Alice` (the local actor is `alice`) in compose produced a mention tag linking to
`https://iris.luit.ink/ap/v1/u/Alice`, which **404'd** because the server's actor document
handler (`ActorDocumentHandler`, GET `/ap/v1/u/{handle}`) does a case-sensitive exact-match
lookup: `BuildActorIri(baseUrl, handle)` builds the IRI verbatim, then `TryGetActorAsync`
does `e.Id == actorIri.Value`. The canonical `…/u/alice` returned 200; the mixed-case
`…/u/Alice` returned 404 — so the mention was not actually addressed to anyone.

S12b (autocomplete candidate/target disagreement) was already clean on the deployed build.

## Fix

`ActorDocumentHandler` (`src/Iris.Server/ActivityPubServerExtensions.cs`): when the
exact-case `TryGetActorAsync` misses, fall back to a case-insensitive `preferredUsername`
match via `SearchActorsAsync(handle, localOnly: true)`, further filtered to actors whose
IRI origin matches the instance base origin. This guards against a cached remote actor
whose `preferredUsername` collides (the `localOnly` heuristic alone is unreliable — some
remote actors carry a `preferredUsername`). The matched actor's canonical IRI replaces the
verbatim IRI, so the document is served under the correct identity.

## Verification

- Build clean (0 warnings, 0 errors).
- `Iris.Web.Tests` 106/106 pass. `Iris.Server.Tests` 1380/1380 pass.
- Live-verified (fresh browser context):
  - `curl /ap/v1/u/Alice` → 200, body `id: https://iris.luit.ink/ap/v1/u/alice`,
    `preferredUsername: alice` (the local actor, not the remote "Alice McFlurry").
  - `curl /ap/v1/u/alice` → 200 (unchanged).
  - `curl /ap/v1/u/nonexistentuser123` → 404 (no false positive).
  - Posted a note with `@Alice` (mixed case) → 202; the mention link in the profile
    points to `…/ap/v1/u/Alice` which now resolves to the canonical local actor (200).
