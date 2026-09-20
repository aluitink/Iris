# S15 — Compose visibility hint is misleading for Followers/Direct

- **Class:** UX / cosmetic — **Severity:** S3
- **Status:** open (re-confirmed Pass 37, 2026-09-20, deployed `bb28dcf`)
- **Found:** Pass 22 (2026-09-20) — re-confirmed Passes 24, 28, 37

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

**Re-verification evidence (Pass 28, 2026-09-20, andrew):** in compose, the hint renders **"A note addressed to the public…"** for **Public, Followers, AND Direct** (all three Note-variant cases wrong), and the **Poll** variant renders **"A poll addressed to the public…"** (also hard-coded). Only the Public case is correct; the other five are misleading. STILL OPEN.

**Re-verification evidence (Pass 37, 2026-09-20, andrew, deployed `bb28dcf`):** in compose, the hint renders **"A note addressed to the public — it lands in your outbox and appears in your followers' timelines."** for **Public, Followers, AND Direct** (all three Note-variant cases wrong), and the **Poll** variant renders **"A poll addressed to the public — it lands in your outbox and appears in your followers' timelines."** (also hard-coded, verified with Direct visibility still selected). Only the Public case is correct; the other five are misleading. STILL OPEN.
