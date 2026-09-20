# S12 — @mention linkify (two parts)

- **Class:** bug — **Severity:** S2
- **Status:** open
- **Found:** Pass 18 (2026-09-20)

## S12a — Case-sensitive same-instance mention → dead link

**Symptom:** typing `@Alice` (the local actor is `alice`) produces a mention tag + link to `https://iris.luit.ink/ap/v1/u/Alice`, which **404s** (confirmed live: console `Failed to load resource … 404 @ …/ap/v1/u/Alice`) — so the mention is not actually addressed to anyone.

**Root cause:** `DetectMentionsWithDisplayAsync` (`Compose.razor:477,495`) matches `@([a-zA-Z0-9_]+)` and resolves a same-instance mention **verbatim** to `{base}/ap/v1/u/{handle}` (no case normalization); the actor lookup is a **case-sensitive** exact match (`EfActorStore.TryGetActorAsync:52` — `e.Id == actorIri.Value`; `BuildActorIri:6575` builds the IRI verbatim).

**Fix:** normalize same-instance mention handles to the canonical stored case (case-insensitive preferredUsername lookup → canonical IRI), or make the actor-doc route/lookup case-insensitive.

## S12b — Autocomplete candidate and resolved target disagree

**Symptom:** the compose `@` autocomplete (`GetMentionCandidatesAsync:813`) surfaces a **remote** actor ("Alice McFlurry :bc:") for a same-instance `@alice` query, and accepting it leaves the raw `@alice` in the text (the post then resolves to local `/u/Alice`) — the suggested candidate and the resolved target disagree.

**Fix:** scope same-instance autocomplete to local actors only (or label remote candidates and resolve to the candidate's IRI on accept).

## Re-verify

1. Type `@Alice` (mixed case) and post: the mention resolves to the canonical `…/ap/v1/u/alice` (200), renders as a working link.
2. Type `@alice` in compose: the candidate list does not offer a remote actor for a same-instance handle (or remote candidates are labeled and resolve to their own IRI on accept).
