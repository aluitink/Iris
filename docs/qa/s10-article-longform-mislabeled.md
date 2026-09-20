# S10 — Article "(long-form)" is mislabeled

- **Class:** UX / feature-gap — **Severity:** S2
- **Status:** fixed (2026-09-20, verified Pass 38 on rebuilt container post-`bdc0e66`)
- **Found:** Pass 17 (2026-09-20) — re-confirmed Passes 27, 35; **fixed + verified Pass 38**

## Symptom

Selecting **"Article (long-form)"** in compose posts a valid `Article` (stored `ObjectType=Article`, IRI `…/articles/{id}`, detail page renders clean, 0 errors) — but it is **mislabeled**: there is **no title field**, the body is **still capped at 500 chars** (the `@Content.Length/@MaxChars` counter at `Compose.razor:97-98` is shared with Note), and it **renders identically to a Note** (plain paragraph, no long-form/typography treatment). `PostArticleAsync` (`Compose.razor:1718-1794`) just wraps `Content.Trim()` into `Article.Content`.

## Fix

Either implement real long-form (title field + larger character limit + distinct rendering) or drop the "(long-form)" label from the type selector.

## Re-verify

Post an Article: if the label stays, the title field + larger limit + distinct detail render all work; if the label is dropped, the selector says "Article" and behavior matches the (plain) implementation.

**Re-verification evidence (Pass 27, 2026-09-20, andrew):** the Pass-17 Article (`…/articles/06GBS2Z3GAJSJTZ2DETZ1XGZNW`) still renders as a **plain paragraph** — no title field, no long-form typography, 0 console errors. STILL OPEN.

**Re-verification evidence (Pass 35, 2026-09-20, andrew, deployed `bb28dcf`):** compose selector still shows "Article (long-form)"; char counter still **0/500** (same as Note); no title field appears on type switch. The Pass-33 Article (`…/articles/06GBV1G8MYD7WMRGWB1FFSGFW4`) renders as a **plain paragraph** with no long-form typography, 0 console errors. STILL OPEN.

**Re-verification evidence (Pass 38, 2026-09-20, andrew, rebuilt container post-`bdc0e66`):** compose selector now says **"Article"** (no "(long-form)"); formatting tip updated to "Notes and Articles share the same character limit; Articles are stored as long-form objects."; compose hint is type-aware: **"An article addressed to the public — it lands in your outbox and appears in your followers' timelines."** (was "A note addressed to the public…"). Char counter still 0/500 (shared limit, now honestly stated). **FIXED** — the label is no longer misleading.
