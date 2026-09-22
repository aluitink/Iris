# S48 — Search: hyphenated terms return 0 results

- **Severity:** S2
- **Status:** Open
- **Class:** bug
- **Found:** Pass 317 (2026-09-22)

## Symptom

Searching for a hyphenated term (e.g., `II-S36`) on the Search page returns **0 results**, even though the term appears in stored notes. Non-hyphenated terms (e.g., `fresh B post`) work correctly.

## Repro

1. Sign in as ii-b1@B.
2. Go to `/search?q=II-S36`.
3. Observe: "No matches found." (0 results).
4. Go to `/search?q=fresh B post`.
5. Observe: 4 results (correct).

## Root cause

The search uses PostgreSQL full-text search with the `simple` text search config:

```sql
"SearchVector" @@ plainto_tsquery('simple', 'II-S36')
```

`plainto_tsquery('simple', 'II-S36')` produces:

```
'ii-s36' & 'ii' & 's36'
```

It requires ALL three tokens (`ii-s36`, `ii`, `s36`) to be present in the document. However, the `SearchVector` stores `II-S36-P212` as a **single token** `ii-s36-p212` (the `simple` config doesn't split on hyphens). So the query token `ii-s36` doesn't match the stored token `ii-s36-p212`.

## Fix

Either:
1. Use a text search config that splits on hyphens (e.g., `english` or a custom config with hyphen as a token boundary), or
2. Use `websearch_to_tsquery` instead of `plainto_tsquery` (it handles quoted phrases and is more lenient), or
3. Fall back to `ILIKE` on the `Document` column when the tsquery returns 0 results (the existing fallback only applies when `SearchVector IS NULL`).

## Impact

Users cannot find notes by searching for hyphenated terms. This affects any note containing hyphenated words (e.g., `II-S36`, `pass-317`, `cross-instance`).

## Test evidence

```sql
-- Query that fails:
SELECT "Id" FROM "Objects"
WHERE NOT "IsTombstoned"
  AND "SearchVector" @@ plainto_tsquery('simple', 'II-S36')
-- Returns 0 rows

-- The SearchVector for a matching note:
SELECT "SearchVector"::text FROM "Objects"
WHERE "Document"::text LIKE '%II-S36%' AND "ObjectType"='Note' LIMIT 1
-- Returns: 'ii-s36-p212':1 'fresh':5 'b':6 ...

-- plainto_tsquery breakdown:
SELECT plainto_tsquery('simple', 'II-S36')
-- Returns: 'ii-s36' & 'ii' & 's36'

-- Non-hyphenated query works:
SELECT count(*) FROM "Objects"
WHERE NOT "IsTombstoned" AND "ObjectType"='Note'
  AND "SearchVector" @@ plainto_tsquery('simple', 'fresh B post')
-- Returns: 4
```
