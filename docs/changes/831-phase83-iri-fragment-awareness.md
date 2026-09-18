# 83.1 — Global `Iri` fragment-awareness

**Phase 83.1** — make `Iri` equality fragment-aware (the larger, per-usage change that 82.3 deferred).

## What was built

`Iri` was a `readonly record struct` over `System.Uri`. Its synthesized equality delegated to
`Uri.Equals`, which is **fragment-insensitive by design** (a URI fragment is not part of the resource
identifier under the W3C definition — `Uri` compares two URIs as equal when their schemes, hosts, paths,
and queries match, ignoring the fragment). That default is fine for incidental fragment spelling, but it
is **wrong** for ActivityPub IRIs, whose fragments are semantically significant:

- a **key IRI** (`{actor}#key-1`, `{actor}#main-key`) names a specific signing key — distinct from the
  bare actor IRI and from a different fragment;
- the **public-audience IRI** (`https://www.w3.org/ns/activitystreams#Public`) is a distinct resource.

With fragment-blind equality, an `Iri`-keyed `Dictionary` conflates `#key-1` with `#key-2` and with the
bare actor IRI, so a key stored under one fragment is "found" for a different fragment — a silent
key-management corruption. 82.3 fixed this **surgically** (the in-memory + file-backed key stores got an
explicit `IriEqualityComparer`), but the rest of the codebase (151 `Iri`-as-dict-key sites, including the
caches, feed dedup, and every store) still used the fragment-blind default.

This slice makes the fix **global**: `Iri` is now a `readonly struct` with explicit fragment-aware
equality. `Equals(Iri)` / `GetHashCode` / `==` / `!=` compare `Iri.Value` (the canonical absolute-URI
string, which **includes** the fragment), case-sensitively per RFC 3986, and are null-safe for
`default(Iri)` (a default `Iri` has no underlying `Uri`; it is distinct from any real IRI, and two
defaults are equal). Every `Iri`-keyed `Dictionary`/`HashSet`/`ConcurrentDictionary` in the codebase is
now fragment-aware by default, matching the durable Postgres store (`EfKeyStore`) and the
`IriEqualityComparer` already used by the key stores.

## The change

- **`src/Iris.Core/Identity/Iri.cs`** — `readonly record struct` → `readonly struct` + explicit
  `Equals(Iri)` / `Equals(object?)` / `GetHashCode()` / `operator ==` / `operator !=`, all comparing a
  private null-safe `_value()` (the `Value` string, or `null` for `default`). No `with`/deconstruction
  usages of `Iri` exist, so the record→struct conversion is safe.
- **`src/Iris.Core/Identity/IriEqualityComparer.cs`** — doc updated: it is now an *explicit* declaration
  of the same fragment-aware, `Value`-based semantics (behavior identical to the default `Iri`
  equality); kept as the documented comparer the key stores pass to their dictionaries.
- **`src/Iris.Server/Inbox/MoveActivityHandler.cs`** — two genuine bug fixes the change exposed
  (below).
- **`tests/Iris.Server.Tests/Services/FeedServiceTests.cs`** — test-data fix (below).
- **`tests/Iris.Core.Tests/Identity/IriTests.cs`** — 7 new tests locking the fragment-aware semantics.

## Bugs the change surfaced

The fragment-aware change did **not** break 25 tests as 82.3 predicted — it broke **2 real ones** (the
rest of the "25" were cascades from a `default(Iri)` `NullReferenceException` in the first draft of the
equality, fixed by making `_value()` null-safe). The 2 real failures were both **genuine latent bugs**
accidentally masked by fragment-blind conflation:

1. **`MoveActivityHandler` never invalidated a non-standard key fragment.** `ResolveOldKeyIri` read the
   cached actor doc with `bypassCache: true` (which skips the cache read and always calls the factory —
   here a null-returning factory — so it never read the doc's `publicKey.id`) **and** it ran *after* the
   actor-doc invalidation (line 109 invalidated the doc, line 110 then read a now-absent doc). Both made
   it fall back to `#key-1`. For a non-standard key fragment (`#main-key`), the fallback `#key-1`
   "invalidated" the `#main-key` cache entry only because fragment-blind `#key-1 == #main-key`. Fixed by
   (a) resolving the key IRI **before** the actor-doc invalidation, and (b) reading the cache
   (`bypassCache: false`, null factory) so a cache hit yields the doc and its real key IRI.
2. **`FeedServiceTests` seeded inconsistent note IRIs.** `AddPostWithId` gave the embedded note
   `Id = {noteIri}#note` (a fragment-suffixed variant), so a boost of the bare `noteIri` coalesced with
   the post only by fragment-blind coincidence. Under fragment-aware equality the two are distinct (a
   note at `…#note` is a different resource than `…`), so the boost no longer coalesced. Fixed the test
   data to use the note's own IRI — the correct, realistic representation (a note is referenced by its own
   `Id`).

Both fixes are strictly more correct: they make the production code do what its doc comments already
claimed (read the cached doc's `publicKey.id`; coalesce a note with its boost by object IRI).

## Tests

7 new `IriTests` (fragment-aware semantics):
- `Equality_IsFragmentAware_DistinctFragmentsAreDistinct` — `#key-1` ≠ `#key-2` ≠ `#main-key` ≠ bare actor; `==`/`!=` agree.
- `Equality_FragmentAware_SameFragmentIsEqual` — same fragment + same base ⇒ equal (value + hash).
- `Equality_FragmentAware_DiffersFromBareActorOnlyByFragment` — the key-management case: `{actor}#key-1` ≠ `{actor}`.
- `Equality_PublicAudienceIsDistinctFromBareIri` — a fragment-bearing IRI is never equal to its fragment-stripped form.
- `Equality_DefaultIri_IsNullSafeAndDistinctFromRealIris` — `default(Iri)` doesn't throw; two defaults equal; a default ≠ any real IRI.
- `Equals_Object_FragmentAware` — `object?` equality routes through the fragment-aware `Equals`.
- `GetHashCode_IsStableAndFragmentSensitive` — identical IRIs hash the same; fragment-differing IRIs hash differently.

Plus the 2 pre-existing tests that now pass for the *right* reason:
`MoveActivityHandlerTests.HandleAsync_NonStandardKeyFragment_InvalidatesActualKeyIri` and
`FeedServiceTests.Feed_CreateOfTwoObjectsPlusAnnounceOfOne_NoOverDedup`.

**Suite: 1595 passed / 0 failed / 1 skipped** (up from 1588; +7 `IriTests`, no regressions).

## Decision: global fragment-awareness vs. per-usage

82.3 chose the surgical fix (an explicit comparer on the key stores) to avoid a global change that would
break 25 tests. This slice revisited that and found the global change was actually **safe and correct**:
the codebase already compares by `Value` (fragment-aware) at the boundaries that matter (the key stores
via `IriEqualityComparer`, the Postgres store via string comparison, `IriExtensions`' private
`IriComparer`). The 25-test prediction was a worst-case estimate; the real breakage was 2 latent bugs +
a `default(Iri)` null-safety gap, all now fixed. Making `Iri` fragment-aware globally is the right
default because an ActivityPub IRI's fragment is part of its identity; any site that genuinely wants
fragment-blind comparison (none found) can use a stripped comparison explicitly.

## Follow-up

None. Phase 83.2 (deployment hardening + config validation) is next.
