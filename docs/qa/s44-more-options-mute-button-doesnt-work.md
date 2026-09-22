# S44: "Mute" button in "More options" menu doesn't work

**Severity:** S3 (feature-broken)
**Status:** Open
**Reported:** 2026-09-22, Pass 304
**Build:** `345286cc`

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
