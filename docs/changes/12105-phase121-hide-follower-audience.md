# 121.5 — Public timeline: hide or improve "To followers" audience line for anonymous visitors

**Date:** 2026-09-13
**Branch:** phase-32-production-app

## Problem

`GetAudienceIris()` filters the AS Public sentinel but still returns follower-collection IRIs (e.g. `https://…/u/alice/followers`). On the public timeline these rendered as "To followers" — confusing for anonymous visitors who can't see the follower list and have no reason to click a followers-collection link.

## Fix

Added `FilterDisplayAudience()` to `ObjectView.razor.cs`. It strips IRIs whose path ends in `/followers` or `/following` from the rendered audience list. Both `AudienceIris` (bare-object branch) and `ActivityAudienceIris` (activity-wrapped branch) now pass through this filter.

Concrete mention IRIs (e.g. a specific user in `cc`) are unaffected and still render as "To @user".

## Verification

- Followers-only post (`to: …/followers`): no "To" line rendered.
- Public post (`to: Public`, `cc: …/followers`): no "To" line (both public and followers filtered).
- Post with both a followers cc and a concrete mention (`cc: [followers, @andrew]`): "To andrew" only — followers collection filtered, concrete mention preserved.
- 0 console errors in all cases.
- `dotnet build` + `dotnet test` green (1126 server + 95 web tests pass).
