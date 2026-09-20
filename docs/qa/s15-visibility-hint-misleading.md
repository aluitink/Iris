# S15 — Compose visibility hint is misleading for Followers/Direct

- **Class:** UX / cosmetic — **Severity:** S3
- **Status:** open (re-confirmed Pass 24)
- **Found:** Pass 22 (2026-09-20)

## Symptom

The compose **visibility hint** is **misleading for Followers and Direct**. The hint (`Compose.razor:175-183`) is a ternary that **ignores the visibility setting** — for Followers **and** Direct it still renders:

> "A note addressed to the public — it lands in your outbox and appears in your followers' timelines."

(the Poll variant, `:181`, is also hard-coded to "addressed to the public").

For **Direct** (a private message) this is actively misleading: the hint says "addressed to the public" but the post is **not** public. Delivery itself is verified **correct** (Pass 22/24: Direct = `to`/`cc` followers, not in Home timeline; Followers = `to`/`cc` followers, appears in the author's "Your posts") — only the hint text is wrong.

## Fix

Make the hint reflect the selected visibility:
- **Public** → "addressed to the public — appears in your followers' timelines"
- **Followers** → "visible to followers only"
- **Direct** → "a private message — not public"

…and drop the hard-coded "addressed to the public" from the Poll variant.

## Re-verify

In compose, switch the visibility selector through Public / Followers / Direct (Note and Poll variants): the hint text matches the selected level in all six cases.
