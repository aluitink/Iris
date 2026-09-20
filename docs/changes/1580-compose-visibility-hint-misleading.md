# 1580 — S15: Compose visibility hint misleading for Followers/Direct

**Severity:** S3
**Status:** Fixed

## Problem

The compose visibility hint was a ternary that ignored the selected visibility setting. For
Followers and Direct it still rendered "addressed to the public" — actively misleading for
Direct (a private message). The Poll variant was also hard-coded to "addressed to the public."
All six combinations (3 types × 3 visibilities) were checked; only the Public cases were correct.

## Fix

`Compose.razor`: extracted the hint into a computed property `ComposeHint` that is
visibility-aware:
- **Public** → "addressed to the public — it lands in your outbox and appears in your
  followers' timelines."
- **Followers** → "visible to followers only — it lands in your outbox and appears in your
  followers' timelines."
- **Direct** → "sent as a direct message — not visible in public or follower timelines."

The type prefix ("A note" / "An article" / "A poll") is preserved.

## Verification

- Build clean (0 warnings, 0 errors).
- `Iris.Web.Tests` 106/106 pass.
- Live-verified (fresh browser context, all 6 cases):
  1. Note+Public: "A note addressed to the public — it lands in your outbox and appears in your followers' timelines."
  2. Note+Followers: "A note visible to followers only — it lands in your outbox and appears in your followers' timelines."
  3. Note+Direct: "A note sent as a direct message — not visible in public or follower timelines."
  4. Poll+Direct: "A poll sent as a direct message — not visible in public or follower timelines."
  5. Article+Public: "An article addressed to the public — it lands in your outbox and appears in your followers' timelines."
  6. Article+Direct: "An article sent as a direct message — not visible in public or follower timelines."
