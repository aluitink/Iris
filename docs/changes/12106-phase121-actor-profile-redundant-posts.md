# 121.6 — Actor profile: remove redundant "Posts" section header and description

**Date:** 2026-09-13
**Branch:** phase-32-production-app

## Problem

Below the "Posts" tab in the actor profile, `PagedCollection` rendered a second "Posts" `<h2>` heading plus an implementation-detail description: "This actor's notes and articles. Social activities (follows, boosts, moderation) are filtered out." The tab label already identifies the section, making the heading redundant and the description confusing.

## Fix

Removed the `Title="Posts"` and `Description="..."` parameters from the `PagedCollection` in `ActorDetail.razor`'s Posts tab panel. The collection now renders directly under the tab without the duplicate header.

## Verification

- Live-verified on andrew's profile: only the "Posts" tab label remains, no duplicate heading or description.
- 0 console errors.
- `dotnet build` + `dotnet test` green.
