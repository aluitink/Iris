# S45 — @mention of a hyphenated handle is truncated at the hyphen (broken link)

- **Class:** bug — **Severity:** S2
- **Status:** open (found Pass 308, 2026-09-22)
- **Found:** Pass 308 (2026-09-22)
- **Related:** [S12](s12-mention-case-and-autocomplete.md) (mention linkify case-sensitivity + autocomplete, fixed Pass 38); the composing surface builds the correct `Mention` tag (see below) but the render-time re-linkify truncates the token.

## Symptom

Signed-in (ii-a1@A). Compose a note containing a mention of a **hyphenated** handle, e.g. `@ii-a2` (every QA test account uses hyphenated handles: `ii-a1`, `ii-a2`, `ii-b1`, …). After posting, on the object-detail page the mention renders as:

- a link `@ii` pointing to `https://qa-iris-a.luit.ink/ap/v1/u/ii` (the handle **truncated at the hyphen**), followed by the literal text `-a2` (the rest of the handle, not linked).
- Clicking the `@ii` link navigates to the actor page, which shows **"Actor not found."** and the console logs a **404** for `GET /ap/v1/u/ii`.

Hashtag parsing is **correct** in the same post: `#testtag` and `#qatag` both render as proper links to `/search?q=%23testtag` / `/search?q=%23qatag`. Only the mention path is affected, and only for handles containing a hyphen.

Repro (clean entry):
1. Compose → type `… @ii-a2 …` → Post (HTTP 202).
2. Open the object-detail page for the new note.
3. Observe the `@ii` link (→ `/ap/v1/u/ii`) + literal `-a2`; click it → "Actor not found." + console 404.

## Root cause

The **render-time** mention linkify regex does not allow a hyphen in the local handle.

`src/Iris.Core/Rendering/MentionLinkify.cs:246`:

```csharp
[GeneratedRegex(@"(?<![\w/""'])@([A-Za-z0-9_]+)(?:@([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+))?")]
private static partial Regex MentionToken();
```

The bare-handle capture group is `[A-Za-z0-9_]+` — it matches letters/digits/underscore but **stops at `-`**. For input `@ii-a2` the match is `@ii` (group 1 = `ii`); the trailing `-a2` is left as plain text. The linkify then wraps `@ii` in an `<a href="…/ap/v1/u/ii">`, producing the broken link.

Two contributing facts:
1. **The composing surface is correct.** `src/Iris.Core/Compose/ComposeNote.cs` and `src/Iris.Client/ActivityPubClient.cs` (`…/u/{handle}`) build the `Mention` tag with the **full** handle IRI (e.g. `…/ap/v1/u/ii-a2`), and the `HandleRules` allow a hyphen in a local handle. So the stored note's `tag` array carries the right mention IRI — the defect is purely in the **display-time re-linkify** that re-derives the visible token from the raw text with the over-narrow regex.
2. **The server-side inbound normalizer has the same bug.** `src/Iris.Server/Inbox/InboundTagNormalizer.cs:36` uses the identical bare-handle class `[A-Za-z0-9_]+` (its federated variant at :42 does include `-` in the *domain* but the local handle part still excludes it). So a hyphenated `@mention` arriving from another instance is likewise truncated when the inbound tag is normalized.

Contrast: the domain part of the federated regex **does** include `-` (`[A-Za-z0-9-]+`), and the hashtag regex (`#([A-Za-z0-9_]+)`) is unaffected because tags don't contain hyphens in practice.

## Fix

Widen the local-handle character class to include the hyphen, in both the render-time and inbound linkify regexes:

- `src/Iris.Core/Rendering/MentionLinkify.cs:246` — change `@([A-Za-z0-9_]+)` → `@([A-Za-z0-9_-]+)`.
- `src/Iris.Server/Inbox/InboundTagNormalizer.cs:36` — change `@([A-Za-z0-9_]+)` → `@([A-Za-z0-9_-]+)`.

The trailing boundary in `LinkToken` (`(?![\w])`, MentionLinkify.cs:276) already permits a following hyphen to remain outside the match only when the token genuinely ends; with `-` inside the class the full `ii-a2` is consumed and the boundary then correctly stops at the following space. (Verify no over-matching for a handle immediately followed by a hyphen-then-word, e.g. `@ii-a2-more`, which should link `@ii-a2-more` as one token only if that is a valid handle; otherwise confirm the desired tokenization.)

Add/adjust a unit test asserting `Linkify("… @ii-a2 …", mentions=[ii-a2])` produces `<a …>…/ap/v1/u/ii-a2</a>` and no residual `-a2` text.

## Re-verify

1. Rebuild the QA stack from the fixed commit.
2. Signed-in (ii-a1@A): compose a note `hello @ii-a2 #tag` → Post.
3. Open the object-detail page: the mention must render as a single link `@ii-a2` → `…/ap/v1/u/ii-a2`; clicking it opens ii-a2's profile (200, no "Actor not found.", no console 404). No literal `-a2` text.
4. Cross-check a hyphen-free handle (`@ii`) still links correctly, and a federated mention `@user@remote.host` still resolves.
5. Inbound: have B post a note mentioning `@ii-a1`; on A confirm the inbound note's mention renders as the full `@ii-a1` link (exercises `InboundTagNormalizer`).
