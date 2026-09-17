# Phase 156 (Slice A) — Compose mention/hashtag content HTML

**Status:** COMPLETE (live-verified, build clean 0/0, web tests 106/106, core tests 446/446, 0 console errors)
**Date:** 2026-09-17

## What

When composing a post or a reply, the `@mention` and `#hashtag` tokens the user typed previously landed in
the note's `content` as **plain text** (e.g. `@alice` / `#phase156`) and were only turned into links by the
display side, which had to guess the target. This slice makes the **client compose Mastodon-faithful
`content` HTML at post time**: each `@mention` / `#hashtag` token is wrapped in a real `<a>` link in the
stored `content`, so the tokens render as **tappable links in the note body** on every reader (local feed,
object detail, reply preview, federation) without the display side re-deriving the target — and in
addition to the structured `tag` array the note already carried.

- `@mention` → `<a class="mention" href="https://instance/ap/v1/u/{handle}">@alice</a>` (the actor's
  ActivityPub IRI, resolved for same-instance or cross-instance `@handle@domain` tokens).
- `#hashtag` → `<a class="hashtag" href="https://instance/search?q=%23{tag}">#phase156</a>` (the instance's
  hashtag-search URL).

Posts with **neither** a mention nor a hashtag are unchanged (still raw text, rendered as Markdown on
display). Only posts that actually contain tokens get Markdown-rendered + linkified at compose time.

## How

### `src/Iris.Core/Rendering/MentionLinkify.cs` (new)

Static class `MentionLinkify` with `Linkify(string? html, IReadOnlyList<Mention>? mentions,
IReadOnlyList<Hashtag>? hashtags)` and two records, `Mention(string Display, Iri Iri)` and
`Hashtag(string Display, string Href)`.

- **Protects existing `<a>` elements** first: each `<a …>…</a>` is replaced with a `\u0001{index}\u0001`
  placeholder (so a link's text/inner HTML is never re-scanned and double-wrapped), the token wrapping runs,
  then the placeholders are restored.
- **Wraps each token** — first occurrence, word-boundary-guarded (so `100%#off` does not link `#off`, and a
  token already inside a protected link is skipped) — in `<a class="mention" href="{iri}">{display}</a>` or
  `<a class="hashtag" href="{href}">{display}</a>`.
- Returns the input unchanged (or `""`) when there is nothing to linkify.

Unit-validated (6 cases in `.tmp-test/asmcheck`): mention + hashtag in one body; no tokens → unchanged;
blank → `""`; a pre-existing markdown `<a>` link is protected (not re-wrapped); `100%#off` → `#off` not
linked; cross-instance `@user@example.com` → linked to the resolved actor IRI.

### `apps/Iris.Web.Client/Components/Pages/Compose.razor`

- **New `DetectMentionsWithDisplayAsync(string text, Iri actorId)`** → `Task<List<(Iri Iri, string Display)>>`.
  Resolves each `@handle` (same- or cross-instance) token to the actor's IRI **and** returns the raw typed
  token (e.g. `@bob`, `@bob@domain.tdl`) as the link display text.
- **`DetectMentionsAsync`** (the IRI-only variant used elsewhere) now delegates to the new method and
  projects the IRIs, so there is a single detection path.
- **`PostAsync`** now:
  1. calls `DetectMentionsWithDisplayAsync` → builds `linkifyMentions` (`MentionLinkify.Mention` list) and
     `linkifyHashtags` (`MentionLinkify.Hashtag` list, href = `{instance}/search?q=%23{tag}`);
  2. calls `BuildLinkedBody(rawContent, linkifyMentions, linkifyHashtags)` → `bodyHtml`;
  3. passes `bodyHtml` (not the raw `Content.Trim()`) to **both** the note path (`ComposeNote.Build`) and
     the reply path (`PostReplyAsync`).
- **New private static `BuildLinkedBody(rawContent, mentions, hashtags)`:** returns `rawContent` unchanged
  when the post has no mentions/hashtags; otherwise
  `MentionLinkify.Linkify(Markdown.ToHtml(rawContent), mentions, hashtags)`.
- `@using Iris.Core.Rendering` (already present) supplies `MentionLinkify` and `Markdown` unqualified.

### CSS (both `app.css` copies — kept identical)

- `.object-content a.mention` and `.object-content a.hashtag`: `color: var(--accent)`, `font-weight: 600`,
  `text-decoration: none` (underline on `:hover` only) — distinct from the plain web-link styling
  (`.object-content a` = `--success`, always underlined). Matches the Mastodon convention of mentions and
  hashtags being accent-colored, underline-on-hover tokens.

## Decision

- **Compose-time linkify (not display-time).** The display side (`ObjectView`) renders `content` verbatim
  when it is pre-rendered HTML (`IsPreRenderedHtmlContent`) and as Markdown otherwise. By emitting
  linkified HTML at compose time, the stored document is self-describing: the link target is authoritative
  and travels with the note across federation, and the display side needs no new linkify pass for
  Iris-authored notes. (Display-side linkify of *incoming remote* notes that ship plain-text tokens is a
  separate slice — Phase 156 Slice B.)
- **Markdown-render only when needed.** `Markdown.ToHtml` HTML-escapes its input and emits block-level
  markup; running it on every post would change the stored shape of plain-text posts. Gating the
  render+linkify on "has mentions/hashtags" keeps posts without tokens byte-for-byte as they were.
- **First-occurrence, word-boundary wrapping.** A note may name the same handle several times; linking the
  first occurrence (and guarding word boundaries) mirrors what the display linkify did and avoids
  over-linking fragments like `100%#off`.

## Verification

- **Build:** `dotnet build -c Release` (full solution) — 0 warnings / 0 errors.
- **Tests:**
  - `dotnet test tests/Iris.Web.Tests -c Release --no-build` — 106/106 passed, 0 failures.
  - `dotnet test tests/Iris.Core.Tests -c Release --no-build` — 446/446 passed, 0 failures.
  - (No new coded tests per the WASM manual-test policy — Phase 45+ verification is live Playwright.)
- **Live (docker, `irisweb-iris-web-1`, `irisweb-db-1`):**
  - **Build gotcha (recurring):** `docker compose build --no-cache iris-web` then `up -d --force-recreate`;
    a **fresh browser context** (close + reopen) is required to bust the browser's cache of the
    content-hashed WASM/CSS. Verified the new code is in the deployed wasm via a string search for
    `DetectMentionsWithDisplayAsync` / `BuildLinkedBody` (found in `Iris.Web.Client.*.dll`) and
    `MentionLinkify` (found in `Iris.Core.*.wasm` + `Iris.Web.Client.*.wasm`).
  - **Note path:** logged in as `andrew`, composed `Phase 156 mention test: @alice and #phase156 hashtag.`
    → HTTP 202. The stored note
    (`https://iris.luit.ink/ap/v1/u/andrew/notes/06GAXYWDX3EE0MFJVXF08SV3VR`) carries
    `content` =
    `<p>Phase 156 mention test: <a class="mention" href="https://iris.luit.ink/ap/v1/u/alice">@alice</a> and <a class="hashtag" href="https://iris.luit.ink/search?q=%23phase156">#phase156</a> hashtag.</p>`
    and `tag` = `["https://iris.luit.ink/ap/v1/u/alice", {"href":"…/search?q=%23phase156","name":"#phase156","type":["Hashtag","Object"]}]`.
  - **Reply path:** replied to that note with `Replying with @bob and #replytag.` → HTTP 202. The stored
    reply carries the same linkified `content` shape, `tag` with the `@bob` IRI + the `#replytag` Hashtag,
    and the correct `inReplyTo`.
  - **Display:** the profile feed renders the note's `@alice` and `#phase156` as tappable links; computed
    style confirmed `color: #5b8cff` (= `--accent`), `text-decoration: none`, `font-weight: 600`. The reply
    preview blockquote renders the parent's stored HTML verbatim (links included).
  - **0 console errors** across the whole session (login → compose note → profile → reply → profile).

## Note (follow-up, not blocking)

The `Mention` tag currently serializes as a **compact IRI string** in the stored document
(`"https://iris.luit.ink/ap/v1/u/alice"`) rather than the full `{type:"Mention","href":"…"}` object. This is
the **server's ingestion normalization** and is **pre-existing** (the same shape is present on Iris-authored
notes from before this change — e.g. alice→bob, 2026-09-08), not a regression introduced here. The `content`
HTML (this slice's focus) and the `Hashtag` object are unaffected. Confirming interop parity with remote
Mastodon for the compact-string mention tag is a follow-up.

## Files

- `src/Iris.Core/Rendering/MentionLinkify.cs` (new)
- `apps/Iris.Web.Client/Components/Pages/Compose.razor`
- `apps/Iris.Web.Client/wwwroot/css/app.css`, `apps/Iris.Web/wwwroot/css/app.css`
- `PLAN.md`, `docs/ROADMAP.md`, `docs/plans/production-app-feature-matrix.md`
