# 995: Failed-Proxy Post Fallback Text

## Summary

Improved the fallback text for remote posts whose content cannot be fetched (404/418).

## Problem

When a remote post's content cannot be fetched (e.g. the remote instance returns 404 or 418), the UI showed "View boosted post →". This text was confusing because:
1. It didn't explain WHY the content was missing
2. It was the same text used for actor boosts (which is a different case)

## Fix

Changed the fallback text from "View boosted post →" to "Content unavailable — view original post" for the two cases where the content is genuinely unavailable:
- When `BoostedRenderTarget` is not null but has no content (line 269 in `ObjectView.razor`)
- When `BoostedRenderTarget` is null (line 298 in `ObjectView.razor`)

The text for actor boosts (line 255) is unchanged, as that's a different case (boosting an actor, not a post).

## Files Changed

- `apps/Iris.Web.Client/Components/ObjectView.razor` — updated fallback text in 2 locations

## Verification

- Build: 0 warnings, 0 errors
- Tests: 1304 passed, 0 failed, 8 skipped (fast filter)
- Live verification: Not performed (this is a text-only change; the logic is unchanged)
