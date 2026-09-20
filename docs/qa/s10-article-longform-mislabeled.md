# S10 — Article "(long-form)" is mislabeled

- **Class:** UX / feature-gap — **Severity:** S2
- **Status:** open
- **Found:** Pass 17 (2026-09-20)

## Symptom

Selecting **"Article (long-form)"** in compose posts a valid `Article` (stored `ObjectType=Article`, IRI `…/articles/{id}`, detail page renders clean, 0 errors) — but it is **mislabeled**: there is **no title field**, the body is **still capped at 500 chars** (the `@Content.Length/@MaxChars` counter at `Compose.razor:97-98` is shared with Note), and it **renders identically to a Note** (plain paragraph, no long-form/typography treatment). `PostArticleAsync` (`Compose.razor:1718-1794`) just wraps `Content.Trim()` into `Article.Content`.

## Fix

Either implement real long-form (title field + larger character limit + distinct rendering) or drop the "(long-form)" label from the type selector.

## Re-verify

Post an Article: if the label stays, the title field + larger limit + distinct detail render all work; if the label is dropped, the selector says "Article" and behavior matches the (plain) implementation.
