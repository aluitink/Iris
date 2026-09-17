# Phase 156 (Slice C) — Inbound tag normalizer (resolve `@mention` / `#hashtag` tokens in incoming notes)

**Status:** COMPLETE (live-verified end-to-end, build clean 0/0, core tests 447/447, client tests 187/187, web tests 106/106, 0 console errors)
**Date:** 2026-09-17

## What

Slices A and B covered Iris-**authored** notes (the client composes linkified `content` HTML + a structured
`tag` array, and the display side linkifies plain-text notes that already carry a `tag` array). Slice C
covers the remaining gap: an **incoming** note (a remote peer → local actor/community inbox) whose `content`
carries `@handle` / `@user@domain` / `#hashtag` tokens that have **no matching `tag` reference** (and no
`cc`/`to` mention). Such a note previously had those tokens inert — the mention/hashtag were neither
discoverable nor linkifiable, because nothing resolved them into structured `tag` entries.

Slice C adds a backend **inbound tag normalizer** that runs at ingestion (pre-store) and resolves those
unresolved tokens into `Mention` / `Hashtag` tags, so the note is fully linkified + searchable downstream.

This work also surfaced and **fixed a real interop gap** in the mention read path: the server's ingestion
normalization stores a mention `tag` as a **compact IRI string** (a bare `Link`), not the full
`{type:"Mention", href}` object. The display-side linkify (Slice B) reads mentions via
`IriExtensions.GetMentionIris`, which only matched `tag is Mention` — so it silently missed every compact
mention (a pre-existing quirk affecting Iris-authored and many remote mentions). `GetMentionIris` now
recognizes the compact form, so plain-text notes' mentions are actually linkified.

## How

### `src/Iris.Server/Inbox/IInboundTagNormalizer.cs` (NEW)

- **`IInboundTagNormalizer`** — `Task<IObject?> NormalizeAsync(IObject? note, CancellationToken ct)`:
  resolves any `@mention` / `#hashtag` tokens in `note.content` that have no matching `tag` reference into
  structured `Mention` / `Hashtag` tags, and returns the (possibly) mutated note. Also exposes
  `int MaxMentionResolutions` (the cap on federated WebFinger lookups per note, default 5).

### `src/Iris.Server/Inbox/InboundTagNormalizer.cs` (NEW)

- **`sealed partial class InboundTagNormalizer : IInboundTagNormalizer`** — constructed with
  `IAccountResolver`, `IOptions<ActivityPubServerOptions>`, and an optional
  `ILogger<InboundTagNormalizer>`.
- **`[GeneratedRegex]` token patterns** (compiled at runtime, no reflection cost):
  - `BareMentionToken()` — `(?<![\w@.])@(\w+)` — a local `@handle` (no `.` in the token).
  - `FederatedMentionToken()` — `(?<![\w@.])@(\w+)@([\w.-]+)` — a federated `@user@domain`.
  - `HashtagToken()` — `(?<!\w)#(\w+)` — a `#hashtag`.
  - `HtmlTag()` — `<[^>]+>` — for stripping HTML tags when extracting plain text.
- **`NormalizeAsync`**:
  1. Extracts the content text (joins `content` parts, strips HTML tags) — returns the note unchanged if
     empty.
  2. Collects the already-present mention IRIs (via `GetMentionIris`) and hashtag names (via
     `GetHashtagTags`) from `note.tag`.
  3. Finds the **missing** mention tokens (`MentionTokens`, deduplicated, federated first then bare — a bare
     token that is actually the local half of a federated token is skipped) and missing `#hashtag` tokens.
  4. Resolves each missing mention: bare `@handle` → the local actor IRI (`{baseUrl}/ap/v1/u/{handle}`,
     no network / no existence check); federated `@user@domain` → `IAccountResolver.ResolveAsync`
     (WebFinger, cached, best-effort — a failure yields `null`, never throws), capped at
     `MaxMentionResolutions` federated lookups per note.
  5. Adds a `Mention { Href = resolvedIri }` tag for each resolved mention and a `Hashtag` tag (via
     `BuildHashtagTag`) for each missing hashtag. A hashtag `href` is the author's instance
     `{origin}/search?q=%23{tag}` when the author origin is known (from `attributedTo`), else name-only.
  6. Never mutates `cc` / `to` (out of scope — the `to`/`cc` delivery audience is the author's
     responsibility). Idempotent: a token whose mention IRI / hashtag name is already in `tag` is skipped.
     The entire body is wrapped in a `try/catch` that logs + returns the note unchanged on any failure —
     the normalizer is defensive and can never break ingestion.
- **Helpers**: `MentionTokens(text)` (yields deduplicated `(Token, Handle, Domain?)`), `ResolveMentionAsync`,
  `BuildHashtagTag(name, origin)`, `AuthorOrigin(obj)` (`attributedTo[0]` → `Uri.GetLeftPart(Authority)` if
  http(s)), `ContentText(obj)`, `NormalizeHashtagName(name)` (lower-cased, leading `#` stripped).

### `src/Iris.Server/Inbox/CreateActivityHandler.cs` (MODIFIED)

- New optional constructor parameter `IInboundTagNormalizer? tagNormalizer = null` (before `logger`) +
  `_tagNormalizer` field.
- **`StoreEmbeddedObjectAsync`**: after `EnsureConversationIdAsync` and **before** `PutObjectAsync`, calls
  `_tagNormalizer?.NormalizeAsync(embedded, ct)` — so every **inbound** Create (remote peer → local
  actor/community inbox) is normalized pre-store. The `?` means a null normalizer (unit-test construction)
  is a no-op. Local outbox posts do **not** go through this path — they already carry their `tag` array, so
  the normalizer is a no-op for them even if they did.

### `src/Iris.Server/ActivityPubServerExtensions.cs` (MODIFIED)

- `services.TryAddSingleton<IInboundTagNormalizer, InboundTagNormalizer>()` (after the `IAccountResolver`
  registration) — the normalizer is resolved for the `CreateActivityHandler` at composition time.

### `src/Iris.Core/Identity/IriExtensions.cs` (MODIFIED) — the interop fix

- **`GetMentionIris`**: previously matched only `tag is Mention` (the full `{type:"Mention",href}` object).
  The server's ingestion normalization stores a mention tag as a **compact IRI string** (a bare `Link`), so
  `tag is Mention` silently missed every compact mention — the display-side linkify (Slice B) could not link
  mentions on plain-text notes. A compact `Link` in `tag` is spec-correctly a mention (a hashtag tag, by
  contrast, is an `Object` with `type: Hashtag`, not a link), so `GetMentionIris` now matches **any `ILink`
  tag entry** (a `Mention` or a compact `Link`), resolved via the shared `ResolveObjectIri` helper. Hashtag
  objects are still excluded (and read by `GetHashtagTags`).

### `tests/Iris.Core.Tests/Identity/IriExtensionsTests.cs` (MODIFIED)

- **`GetMentionIris_IgnoresNonMentionTags`** updated: the non-mention tag is now modeled as a `Hashtag`
  **object** (the spec-correct form), not a bare `Link` — a bare `Link` in `tag` is a mention, so the old
  assertion (a `Link` is ignored) was asserting the pre-fix, incorrect behavior.
- **New `GetMentionIris_IncludesCompactLinkMentions`**: asserts a compact `Link` mention (a bare IRI string
  in `tag`) **is** included in the mention set, alongside a full `Mention` object.

## Verification

- **Build**: full solution `dotnet build -c Release` → 0 warnings / 0 errors (`TreatWarningsAsErrors` on).
- **Core tests**: `dotnet test tests/Iris.Core.Tests` → **447/447** (446 original + 1 new
  `GetMentionIris_IncludesCompactLinkMentions`; the updated `GetMentionIris_IgnoresNonMentionTags` passes).
- **Client tests**: `dotnet test tests/Iris.Client.Tests` → **187/187**.
- **Web tests**: `dotnet test tests/Iris.Web.Tests` → **106/106**.
- **Diagnostic** (`.tmp-test/tagcheck/`, gitignored, not committed): 7/7 PASS — hashtag added + href,
  idempotent, bare mention → local IRI, federated unresolved no-throw, mixed, HTML content detection,
  no-tokens no-op.
- **Live end-to-end** (fresh WASM after `docker compose build --no-cache` + `up -d --force-recreate`;
  deployed `/app/Iris.Server.dll` confirmed to contain `InboundTagNormalizer`):
  - Obtained a Bearer token for `andrew` via the auto-approving OAuth2 code flow
    (`GET /ap/v1/oauth2/authorize?client_id=andrew&…` → 302 code → `POST /ap/v1/oauth2/token`).
  - Delivered a raw **Create** to `andrew`'s inbox (`POST /ap/v1/u/andrew/inbox`, Bearer) with a Note whose
    `content` is plain text `Inbound normalizer test: @alice and #slivec tag (no tag array).` and **no
    `tag` array** → **202 Accepted**.
  - The stored note's `tag` array now carries the `@alice` mention (compact IRI
    `https://iris.luit.ink/ap/v1/u/alice`) **and** the `#slivec` `Hashtag` object
    (`href https://iris.luit.ink/search?q=%23slivec`) — exactly what the normalizer added pre-store.
  - The note **renders both as tappable accent links** in the UI (mention → `…/u/alice`, hashtag →
    `…/search?q=%23slivec`) — this is the end-to-end payoff of the `GetMentionIris` compact-mention fix.
  - **No regression**: the Slice-A pre-rendered note (`06GAXYWDX3EE0MFJVXF08SV3VR`) still renders exactly
    **1** mention + **1** hashtag link (the normalizer is a no-op for it — its tokens already have matching
    tags); 0 console errors across all pages.

## Scope note / follow-up

- The normalizer fires only on the **inbound Create** path (remote peer → local inbox). It does not run on
  local outbox posts (they already carry their tags) or on other activity types (follow/accept/announce) —
  those do not carry note content. Extending it to `Update` (an edited note) is a possible follow-up.
- Federated mention resolution is best-effort and capped at 5 per note; an unresolvable federated handle
  yields no tag (the token stays inert) rather than a dead link.
