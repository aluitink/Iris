# S44: "Mute" button in "More options" menu doesn't work

**Severity:** S3 (feature-broken)
**Status:** CLOSED (QA re-verified Pass 330, 2026-09-22, build `0d7307e5`)
**Reported:** 2026-09-22, Pass 304
**Build:** `345286cc` (reported) / `0d7307e5` (re-verified)

## Summary

The "Mute" button in the "More options" menu on a post doesn't actually mute the actor. The mute feature itself works (clicking "Mute" in the profile header correctly mutes the actor), but the "Mute" button in the "More options" menu is broken.

## Steps to Reproduce

1. Sign in as ii-a1@A.
2. Navigate to a remote actor's profile (e.g., `/actor?iri=https://qa-iris-b.luit.ink/ap/v1/u/ii-b1`).
3. Find a post and click the "More options" button.
4. Click "Mute" in the menu.
5. Navigate to `/settings` → Account tab → Moderation section.
6. Observe that the actor is NOT in the "Muted" section.

## Expected Behavior

Clicking "Mute" in the "More options" menu should mute the actor, and the actor should appear in the "Muted" section of the Settings → Moderation page.

## Actual Behavior

Clicking "Mute" in the "More options" menu does nothing. The actor is NOT muted, and does NOT appear in the "Muted" section of the Settings → Moderation page.

## Notes

- The "Mute" button in the profile header works correctly (it mutes the actor and the button changes to "Unmute").
- The "Block" and "Report" buttons in the "More options" menu work correctly.
- 0 console errors when clicking "Mute" in the "More options" menu.

## Suggested Fix

The "Mute" button in the "More options" menu should call the same mute logic as the "Mute" button in the profile header.

## Resolution — CLOSED (Pass 330, 2026-09-22, build `0d7307e5`)

Re-verified on the QA stack: the "Mute" button in the post "More options" menu (the `EngagementBar` moderation menu, `EngagementBar.razor:81` → `MuteAsync()` `EngagementBar.razor:421` → `ILocalModerationClient.MuteAsync` → `POST /local/v1/u/{handle}/mutes/{target}`) now works end-to-end:

1. Signed in as `ii-a1`@A, opened a `ii-b1`@B post in the `ii-a8-community` feed, clicked **More options → Mute**.
2. `POST /local/v1/u/ii-a1/mutes/https://qa-iris-b.luit.ink/ap/v1/u/ii-b1` → **204 No Content**.
3. DB confirmed the mute edge was recorded: `public."Edges"` `Kind=7` (Mute) `Source=…/ii-a1` → `Target=…/ii-b1` (1 row).
4. Settings → Account → **Moderation → Muted** now lists `ii-b1` with an **Unmute** button.
5. The home feed filters the muted actor: `FeedService.cs:303-305` excludes any follow whose IRI is in the actor's `GetMutesAsync` set, so `ii-b1`'s content is hidden from `ii-a1`'s home timeline.
6. Clicked **Unmute** → `?unmute=true` path → the edge was removed (DB `Kind=7` row count back to **0**) and the Settings Muted section returned to "You have not muted anyone."

The earlier "does nothing" observation no longer reproduces. Mute, feed filtering, and Settings management all work. No console errors. **S44 CLOSED.**
