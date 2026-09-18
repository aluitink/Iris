# 133.2 — Actor page/card: render the summary (bio) as sanitized HTML

**Date:** 2026-09-13
**Slice:** 133.2 (PLAN "Up Next" — an actor's description can contain HTML tags; render the markup correctly)
**Commits:** `f0bbe99` — `feat(web): render actor summaries as sanitized HTML (Phase 133.2)`

## Problem

An ActivityPub actor's `summary` (the bio / description shown under the name on the actor
page, the directory card, and the actor's object view) is authored as **HTML** by many
servers (Mastodon and Lemmy bios are HTML: `<p>`, `<b>`, `<a href>`, …). But the client
rendered the summary as **plain text** — `@summary` in a Blazor template, which
HTML-encodes it. So a bio that contained `<b>bold</b>` displayed the literal characters
`<b>bold</b>` instead of rendering bold text, and a link showed as raw `<a href=…>`.

The fix is to render the summary as real markup. But the summary is **untrusted HTML**: it
arrives over the network from other ActivityPub instances (and is stored raw by this
server — the `UpdateActivityHandler` merges the `summary` field verbatim). Emitting it
verbatim as markup would be a cross-site-scripting vector: a hostile remote actor could set
a `summary` containing `<script>` or `<img onerror=…>` and execute JavaScript in every
reader's browser who views that actor's profile/card. So the summary must be **sanitized**
before it is emitted as a `MarkupString`.

## Change

### New: `Iris.Core.Rendering.HtmlSanitizer`

A small, **dependency-free** HTML sanitizer (matching the codebase's dependency-free
rendering convention — `Markdown.ToHtml` is the precedent; it HTML-escapes its input and
applies safe Markdown transforms). `HtmlSanitizer.Sanitize(string?)` takes an HTML fragment
and returns a string safe to emit verbatim as Blazor markup. It works by **allow-listing**
rather than block-listing:

- **Allowed tags** survive: block/structure (`p`, `div`, `span`, `br`, `hr`, `blockquote`,
  `pre`, `code`), emphasis (`b`, `strong`, `i`, `em`, `u`, `s`, `strike`, `small`, `sub`,
  `sup`), links (`a`), lists (`ul`, `ol`, `li`), and headings (`h1`–`h6`). A tag not in this
  set is dropped, but its **inner text is kept** (so a disallowed element renders as its text,
  not as an executable element).
- **Content-is-code tags** (`script`, `style`, `textarea`, `iframe`, `object`, `embed`,
  `form`, `input`, `button`, `select`, `meta`, `link`, `svg`, `math`, …) are dropped
  **together with their content** — a `<script>`'s body is code, not displayable text, so
  `<script>alert(1)</script>` is removed wholesale (not reduced to the inert text
  `alert(1)`).
- **Allowed attributes** (`href`, `title`, `rel`, `target`, `class`, `id`) are kept; every
  other attribute is dropped. This removes `on*` event handlers (`onclick`, `onerror`, …)
  and `style` (which can carry `expression()` / layout-breaking CSS).
- **`href`/`src` are scheme-checked**: only `http`, `https`, and `mailto` survive. A
  `javascript:`, `data:`, `vbscript:`, or any other scheme is dropped (the link survives
  with no target). Control characters / whitespace that browsers strip from the scheme are
  normalized first, so `java\tscript:` cannot bypass the check. `rel` on `<a>` is
  normalized to `nofollow noopener` and `target` to `_blank` (safe external-link semantics).
- Attribute values and any literal text are HTML-escaped on emit, so a `"` inside a value
  cannot terminate the attribute.

It is a character-scanning parser (no external HTML parser — that would pull AngleSharp or a
sanitizer NuGet into the trimmed WASM payload, contradicting the 131.4 payload-reduction
work).

### New: `ActorIdentityHelper.RenderedSummary`

A shared helper in the client's `ActorIdentityHelper` (the existing home for actor-rendering
logic used by all the actor components): `RenderedSummary(string?)` runs the summary through
`HtmlSanitizer.Sanitize` and wraps the result in a `MarkupString`. Null/blank → empty string.

### The three actor-summary render sites

Each was changed from emitting the raw string to emitting the sanitized `MarkupString`:

- `ActorCard.razor` — the `.actor-card-summary` div (directory + follower/following cards).
- `ActorProfile.razor` — the `.actor-profile-summary` div (the actor page header + the
  "Your profile" page header).
- `ObjectView.razor` (the `Item is Actor` branch) — the `.object-summary` span (the object
  view when an actor document is shown directly).

`_Imports.razor` gained `@using Microsoft.AspNetCore.Components` (the namespace of
`MarkupString` — note it is in the core `Components` namespace, **not** `Components.Web`) so
the `.razor` `@code` blocks can reference the unqualified type.

## New / changed API

- **New public type:** `Iris.Core.Rendering.HtmlSanitizer` with
  `public static string Sanitize(string? html)`.
- **New public helper:** `ActorIdentityHelper.RenderedSummary(string?) : MarkupString`.

No ActivityPub / server API surface changed. The server still stores the `summary` raw (it
now demonstrably does — see Verification); only the **rendering** is sanitized.

## Verification

**Unit tests** — 21 new tests in `tests/Iris.Core.Tests/Rendering/HtmlSanitizerTests.cs`
cover the security contract: scripts/iframes removed with their content; `onerror`/`onclick`
stripped (even on an allowed tag); `javascript:`/`data:`/`vbscript:` schemes dropped (and the
`java\tscript:` control-char bypass rejected); safe formatting (`b`, `i`, `a`) and
`http(s)`/`mailto` links preserved; `style`/disallowed tags dropped (text kept); comments
dropped; void elements self-closed; attribute quotes escaped. All 21 pass.

**Live verification** via MCP Playwright (per the web test policy — no new coded web tests;
live Docker app, real browser). The container was rebuilt `--no-cache` and recreated; the
dev stack's `Iris:Dev:CacheBypass` default (`true`) serves the SPA shell `no-store` and the
content-hashed `_framework` WASM fresh, so the new build was confirmed loaded.

Alice's (admin) bio was set, through the real UI (profile → Edit profile → Save), to:

```html
<p><b>Bold bio</b> with <a href="https://example.com">a safe link</a> and an <i>italic</i> part.</p><script>window.__xss_ran = true;</script><img src="x" onerror="window.__xss_ran = true;">
```

The **stored** actor document (fetched over the ActivityPub API) confirms the server kept it
raw — the `<script>` and `<img onerror>` sit verbatim in the DB. This is exactly the
untrusted-HTML case the sanitizer protects against.

Then, in all three render sites (`.actor-profile-summary`, `.actor-card-summary`,
`.object-summary`), the rendered DOM was:

```html
<p><b>Bold bio</b> with <a href="https://example.com">a safe link</a> and an <i>italic</i> part.</p>
```

- **Markup rendered:** real `<b>`, `<i>`, and `<a href="https://example.com">` elements
  (not escaped text) — `hasBold`, `hasItalic`, `hasLink` all `true`.
- **No XSS:** the `<script>` and `<img onerror>` were stripped entirely
  (`hasScriptTag: false`, `hasImgTag: false`) and the `window.__xss_ran` flag was **never
  set** (`xssRan: false`) — the embedded payload never executed.
- **No console errors** on any of the three pages.

After verification, the bio was reset to a plain-text string through the same UI, and its
plain-text rendering was confirmed (no spurious markup).

`dotnet build Iris.slnx` — clean. `dotnet test Iris.slnx` — all tests pass (1146 in
`Iris.Server.Tests`, 430 in `Iris.Core.Tests` including the 21 new sanitizer tests).

## Out of scope

- **Server-side sanitization** of the `summary`. The server intentionally stores the field
  raw (ActivityPub fidelity — a follower on another instance should receive the author's
  original bio). Sanitization is a **rendering** concern and lives at the render boundary,
  which is the correct layer: every consumer (this WASM client, the sample client, or a
  future server-rendered view) sanitizes before emitting to a DOM.
- **Sanitizing other untrusted HTML fields** (e.g. a note's `content` when it is pre-rendered
  HTML rather than Markdown, or tag/mention labels). Those have their own rendering paths
  (`Markdown.ToHtml` for authored notes) and are separate slices.
