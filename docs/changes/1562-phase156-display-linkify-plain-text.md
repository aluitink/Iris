# Phase 156 (Slice B) — Display-side linkify of plain-text mention/hashtag content

**Status:** COMPLETE (live-verified, build clean 0/0, web tests 106/106, core tests 446/446, 0 console errors)
**Date:** 2026-09-17

## What

Slice A made the **client** compose Mastodon-faithful `content` HTML (linkified `@mention` / `#hashtag`)
at post time, so Iris-authored notes render tappable links. Slice B covers the **display side** for notes
whose `content` is **plain text / Markdown** (not pre-rendered HTML) but which carry the mention/hashtag
targets in their structured `tag` array — the shape some remote authors emit (plain-text body + `tag`
references, no inline `<a>` markup). Such a note previously rendered its `@handle` / `#tag` tokens as
inert text; now they render as tappable accent-colored links, with the link targets taken from the note's
own `tag` array (authoritative for a remote note).

Pre-rendered HTML content (Iris-authored via Slice A, or remote authors that inline their own
`<a class="mention">` links) is emitted **verbatim** and is **never** re-linkified — so no note ends up
with doubled links.

## How

### `src/Iris.Core/Rendering/MentionLinkify.cs`

- **New `LinkifyPlain(string? plainText, string? instanceOrigin, IReadOnlyList<Iri>? mentionIris,
  IReadOnlyList<(string Name, Iri? Href)>? hashtags)`** — the display-side entry point:
  1. Scans the body for `@handle` / `@user@domain` tokens (`MentionToken()` regex) and `#tag` tokens
     (`HashtagToken()` regex), both word-boundary-guarded (a leading `@`/`#` at start-of-text or after a
     non-word char, trailing word boundary — so `100%#off` is not matched).
  2. For each `@` token, matches against the declared `mentionIris` **by the actor handle** (the IRI
     path's last segment, e.g. `…/u/alice` → `alice`, `…/users/Gargron` → `Gargron`), case-insensitively.
     A match yields a `Mention(display=token, Iri=matched)`.
  3. For each `#` token, matches against the declared `hashtags` **by name** (case-insensitive, with or
     without the leading `#`). A match yields a `Hashtag(display=token, Href=tagHref)`, where the href is
     the tag's declared `href`, else `{instanceOrigin}/search?q=%23{tag}` when an origin is supplied.
  4. Renders the body through `Markdown.ToHtml` (HTML-escaping it, so it is safe) and runs the existing
     `Linkify` to wrap the matched tokens. A token with **no** matching declared tag is left as plain text
     — an arbitrary note is not sprinkled with dead links.
- **Fixed a latent bug** in `Linkify`: the existing-`<a>` placeholder was substituted as a bare index
  (`"" + i + ""`) but the restore regex (`ProtectedPlaceholder`) expects `\x01{index}\x01`. With both a
  markdown link and a mention/hashtag present, the placeholder would never be restored (the `<a>` would
  vanish). The placeholder is now written with the `\u0001` delimiters so it matches the restore regex.
  (Slice A's live test did not hit this — its body had no pre-existing `<a>` — but it was a real latent
  defect.)

### `apps/Iris.Web.Client/Components/ObjectView.razor.cs`

- **New `RenderObjectContent(IObject? obj, string content)`** — the single display decision:
  - `obj.IsPreRenderedHtmlContent()` → return `content` **verbatim** (it already carries its own links).
  - otherwise → `MentionLinkify.LinkifyPlain(content, origin, obj.GetMentionIris(), obj.GetHashtagTags())`,
    where `origin` is the author's instance origin (`SafeOrigin` of the `attributedTo` IRI) — used only as
    the fallback base for a href-less hashtag search (a remote hashtag's search lives on the author's
    instance).
- **All 5 content render sites** now route through it (each previously inlined
  `IsPreRenderedHtmlContent() ? content : Markdown.ToHtml(content)`):
  `RenderedContent`, `ParentContent`, `LikedContent`, `ActivityContent`, `RenderBoostedContent`.
  The `.razor` markup is unchanged — it already delegated to these code-behind properties.

## Decision

- **Gate linkify on the non-pre-rendered branch only.** Pre-rendered HTML is self-linking (Slice A for
  Iris-authored notes; remote Mastodon/Pluralsite/Pleroma authors inline their own `<a class="mention">`).
  Re-running the linkify over it would risk double-wrapping or clobbering foreign markup. Emitting it
  verbatim (the prior behavior) is the safe, no-op path; the linkify only adds value to plain-text bodies.
- **Match tokens against the declared `tag` array, not re-resolve.** For a remote note the `tag` array is
  the authoritative source of the mention actor IRIs and hashtag hrefs (the remote server already resolved
  them). Re-resolving via WebFinger on the client would be slow, CORS-bound, and redundant. Matching by
  handle (IRI path last segment) / name is cheap and covers the common `@handle` / `@user@domain` forms.
- **Href-less hashtags fall back to the author's instance search.** A hashtag tag without a declared
  `href` still deserves a link; pointing it at `{authorOrigin}/search?q=%23{tag}` is the closest analog to
  what the composing surface does for its own hashtags. No origin (a non-http attributedTo) → the token is
  left as plain text rather than dead-linked.
- **No new CSS.** The `.object-content a.mention` / `a.hashtag` rules from Slice A already style these
  links (accent color, underline-on-hover, weight 600); the display-side links use the same classes.

## Verification

- **Build:** `dotnet build -c Release` (full solution) — 0 warnings / 0 errors.
- **Tests:**
  - `dotnet test tests/Iris.Web.Tests -c Release --no-build` — 106/106 passed, 0 failures.
  - `dotnet test tests/Iris.Core.Tests -c Release --no-build` — 446/446 passed, 0 failures.
  - (No new coded tests per the WASM manual-test policy — Phase 45+ verification is live Playwright.)
- **Live (docker, `irisweb-iris-web-1`, `irisweb-db-1`):**
  - **Build gotcha (recurring):** `docker compose build --no-cache iris-web` then `up -d --force-recreate`;
    a **fresh browser context** (close + reopen) busts the browser's cache of the content-hashed WASM.
  - **Display-side linkify (target case):** a note inserted directly into the DB with a **plain-text**
    `content` (`Plain text note with @alice and #plainlinkify tokens (no inline html).` — no `<`) and a
    `tag` array carrying a `Mention` object (`{type:"Mention", href:"…/u/alice", name:"@alice"}`) + a
    `Hashtag` object (`{type:["Hashtag","Object"], href:"…/search?q=%23plainlinkify", name:"#plainlinkify"}`)
    → the object page renders `@alice` as a link to `…/u/alice` and `#plainlinkify` as a link to
    `…/search?q=%23plainlinkify`. Computed style confirmed `color:#5b8cff` (= `--accent`),
    `text-decoration:none`, `font-weight:600`. (The note was deleted after verification.)
  - **No double-linkify (regression check):** the Slice-A pre-rendered note
    (`Phase 156 mention test: @alice and #phase156 hashtag.`) still renders exactly **1** `a.mention` +
    **1** `a.hashtag` (not doubled) — confirming pre-rendered HTML is emitted verbatim and untouched.
  - **0 console errors** across the whole session (login → object pages).

## Note

Remote notes in the current cache overwhelmingly ship **pre-rendered HTML** with inline
`<a class="mention">` / `<a class="hashtag">` links (Mastodon, Pluralsite, Pleroma, Misskey all emit them),
so they take the verbatim path and already render tappable links. The plain-text-body + `tag`-array shape
that Slice B targets is a valid but currently under-represented ActivityPub shape — Slice B makes Iris
defensive against it so such notes (if they arrive) render correctly rather than as inert text.

## Files

- `src/Iris.Core/Rendering/MentionLinkify.cs`
- `apps/Iris.Web.Client/Components/ObjectView.razor.cs`
- `PLAN.md`, `docs/ROADMAP.md`, `docs/plans/production-app-feature-matrix.md`
