# 90.1 — Improve the Feed (reply context, recipient line, boosts)

**Phase:** 90 — Improve the Feed
**Date:** 2026-09-12
**Status:** Complete
**Commit:** `bc6b51d`

## Objective

Close three feed-rendering gaps from the 90 investigation:
1. **Reply context** — a reply should render the message it replies to as part of the card, so the reader has context for what is being answered.
2. **Email-like card** — the card should read more like an email, with the recipient at the top of the note.
3. **Boosts** — a boost (Announce) should show the boosted post, not the boost wrapper.

All three are pure presentation concerns on the single shared post-card component **`ObjectView`**, which every feed/list renders through (home timeline via `PagedCollection`, profile outbox, search, object detail, community/actor detail). No server or client-API change was needed — the card already had the building blocks (the parent-fetch in `OnInitializedAsync`, the audience/parent IRIs via `IriExtensions`, and the embedded-object helpers); the work was reworking the markup + a little state.

## What was built

All in `apps/Iris.Web.Client/Components/ObjectView.razor` (+ `.razor.cs`) and `wwwroot/css/app.css`.

### 90.1 — inline "in reply to" context card

- `ObjectView.razor.cs`: `OnInitializedAsync` now stores the **fetched parent object** (`_parentObject`) and its author (`_parentAuthorIri`) instead of only a 120-char text preview. New `EffectiveParentIri` (activity branch or bare-object branch), `ParentAuthorIri`, `ParentContent` (rendered HTML/markdown, same safe-render path as the body), and `HasParentContext` helpers.
- Markup: in both the `Create` branch and the bare-object branch, when the parent is fetched the old one-line `in reply to <preview>` is replaced by a `.object-parent-context` card — an `In reply to @author` label plus the parent's content, rendered **above** the reply's own content (the same visual treatment `ObjectDetail.razor` already used for its thread context). If the parent can't be fetched (network error / remote 404), it falls back to the original one-line `in reply to <preview-or-IRI>` link.

### 90.2 — email-style recipient line at the top

- Markup: the post's `To`/`Cc` audience (which previously rendered as muted `to <handle>` lines *below* the content) is now a single `.object-recipient` line **at the top** of the card — a `To` label followed by the audience handles — so each note reads like an email with its recipient up top. The old bottom audience lines are removed (no duplication).
- Applies to both the activity branch (`ActivityAudienceIris`) and the bare-object branch (`AudienceIris`).

### 90.3 — boosts show the boosted post

- Markup: the `Announce` branch no longer leads with a `Boosted` tag + the *booster's* header. It now renders a small `Boosted by @booster` line, then the **boosted post inline** with its *own* author header (`.object-item--boosted`), title, content, media, and engagement bar — exactly like a normal post. When the boosted target is a **bare IRI** (remote content the server stored without an embedded body — common for cross-instance boosts), there is no inline body to render, so it falls back to a `View boosted post →` link (the correct, honest behavior for that data).
- `ObjectView.razor.cs`: new `AnnounceAuthorIri` (the boosted post's `attributedTo`, falling back to the embedded object's IRI) for the inline header.

## Live verification (Playwright, per the WASM manual-test policy)

Rebuilt the Docker app (`docker compose build --no-cache iris-web` + `up -d --force-recreate iris-web`) and restarted the Playwright MCP (`bash scripts/start-playwright.sh`, caching disabled) for a fresh browser. Logged in as `andrew` (rich content: replies + boosts + remote follows).

| Scenario | Result |
|---|---|
| 90.2 recipient line | ✅ Every home-timeline card shows a `To` line at the top (e.g. `To @followers, …`) — moved from the bottom, no duplicate |
| 90.1 reply context | ✅ A reply card shows an `In reply to @author` muted context card with the **parent's content** above its own body. (The parent fetched for the sample replies is genuinely the same Gargron post — the data confirms all these remote replies target `Gargron/statuses/117254912647958576`.) |
| 90.3 boost shows the post | ✅ After "Load more", boost cards render as `Boosted by @Gargron` with the original author header; the boosted target is a bare IRI (remote, no embedded body) so it correctly falls back to `View boosted post →` |
| Console errors | **0 from this change** — the only logged errors are pre-existing remote-federation proxy failures (`mastodon.social` / `ursal.zone` returning 403/404 to this instance), which predate this slice |

## Build / test

- `dotnet build -c Release` → 0 warnings, 0 errors.
- `dotnet test -c Release --no-build --filter "Category!=Slow"` → **green** (all 12 assemblies; `Iris.Server.Tests` 1084 passed / 1 skip in isolated runs).
- **0 new coded tests** (WASM manual-test policy — Phase 45+). Verification is the live Playwright pass above.
- Note: a **pre-existing flaky** `Iris.Server.Tests` test intermittently fails under full parallel-suite load (passed on isolated runs and on 3 of 4 full runs). It is unrelated to this slice — the change touches only the Blazor WASM client (`ObjectView.razor` + `app.css`), which no server test references. Not chased here; flagged for a future test-stability pass.

## Notes / decisions

- **Why presentational-only, no server/client change?** The card already had everything: the parent fetch (`GetObjectAsync` on the parent IRI), the audience/parent IRIs (`GetAudienceIris` / `GetParentIri`), and the embedded-object helpers (`ActivityEmbeddedObject`, `ActivityContent`). The gaps were purely in the markup ordering and the boost branch, so the fix lives entirely in `ObjectView`.
- **Why keep the one-line fallback for the parent?** The parent fetch is best-effort (a remote parent may 404 or the proxy may reject it). Falling back to the original `in reply to <preview>` link keeps the context cue present even when the full parent can't be shown, rather than hiding it.
- **Why "Boosted by @booster" + inline post rather than the old "Boosted" + booster header?** The old layout attributed the card to the *booster* and hid the original post's author. The new layout attributes the card to the *boosted post* (its author), with the boost relationship as a small tag — matching how Mastodon/Pleroma present boosts and what the 90 ask ("show the boosted post, not the boost itself") requires.
- **Why the `View boosted post →` fallback is correct, not a gap:** cross-instance boosts are stored on the server as an `Announce` whose `object` is a bare IRI link (no embedded body), so there is literally no content to inline. Linking to the post is the only honest rendering for that data shape; a boost whose target *is* embedded renders fully inline.
- **Two `app.css` files:** the served copy at `apps/Iris.Web/wwwroot/css/app.css` is kept byte-identical to the client source (`apps/Iris.Web.Client/wwwroot/css/app.css`); both are committed so the built app serves the new styles.
