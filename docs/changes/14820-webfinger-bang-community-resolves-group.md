# 14820 — WebFinger `acct:!{handle}@{host}` resolves the community (Group)

## Summary

The WebFinger handler now resolves the **`!`-prefixed** community form
`acct:!{handle}@{host}` to the local Group document `…/ap/v1/c/{handle}`,
mirroring the person form `acct:{handle}@{host}` → `…/ap/v1/u/{handle}`.

## Finding

[S29 — community (Group) not resolvable via WebFinger](../qa/s29-community-webfinger-404.md)
(S2-sev, discovery). The Group document is correct and public at
`/ap/v1/c/{handle}`, but a remote instance resolving the community via WebFinger
gets a **404**. Remote software (Lemmy/Mastodon) addresses a community as
`acct:!{handle}@{host}` — the leading `!` marks the group namespace. The person
form (`acct:{handle}@{host}`) was unaffected.

## Root cause

`WebFingerHandler` (`ActivityPubServerExtensions.cs`) extracts
`handle = acct[..at]`, so `acct:!devs@host` yields `handle = "!devs"` (the
leading `!` is retained). It then builds:

- `BuildActorIri(baseUrl, "!devs")` → `…/ap/v1/u/!devs` — actor-store miss.
- `BuildCommunityIri(baseUrl, "!devs")` → `…/ap/v1/c/!devs` — community-store miss.

Neither lookup strips the `!`, so the IRI segment contains a literal `!` that
no stored document matches, and the handler falls through to `Results.NotFound()`.

The existing Phase 136.2 community test covered only the **bare** form
`acct:devs@host` (no `!`), so the `!`-prefixed path — the one remote instances
actually send — was untested and broken.

## Fix

In `WebFingerHandler`'s community fallback branch, strip the leading `!` before
building the community IRI:

```csharp
var communityName = handle.TrimStart('!');
var communityIri = BuildCommunityIri(baseUrl, communityName);
```

- `acct:!devs@host` → `…/ap/v1/c/devs` (the fix).
- `acct:devs@host` → `…/ap/v1/c/devs` (unchanged; `TrimStart` is a no-op).
- The person branch is unchanged: a person `!`-handle still 404s (no `!`-person
  exists), and the instance-handle / actor-store lookups are untouched.
- `TrimStart('!')` is idempotent and only affects the community fallback, so it
  cannot alter the person or instance-actor resolution paths.

## Tests

`tests/Iris.Server.Tests/ServerEndpointIntegrationTests.cs` (seeded community
`devs`, host `a.domain.local`):

- `WebFinger_ResolvesBangPrefixedCommunityHandleToGroupIri` — `acct:!devs@host`
  → **200**, self `href` → `…/ap/v1/c/devs` (the fix).
- `WebFinger_UnknownBangPrefixedCommunityHandle_Returns404` — `acct:!nobody@host`
  → **404** (the `!` is stripped, the unknown name misses; no spurious bare-form match).

## Verification

- `dotnet build`: 0 Warning(s), 0 Error(s) (`TreatWarningsAsErrors` on).
- Fast suite (`Category!=Slow`): **1409 passed, 0 failed** (was 1408, +2).
- `Iris.Web.Tests`: **108 passed, 0 failed**.

## Decision

Stripping is confined to the **community** branch rather than done at handle
extraction, so the person and instance-actor paths are provably untouched. The
`!` is a community-namespace marker only; there is no `!`-person form to worry
about, so the narrow fix is correct and minimal.
