# Phase 115.3 — Communities: Live Verification + Matrix Reconciliation

## What

Live-verified the communities web integration and reconciled its D-column
(web-integration) matrix entries. The feature was **already fully implemented**;
this slice closes the verification gap and updates the feature matrix.

## What was verified (Playwright, signed in as alice)

### Browse / directory of communities
- `/communities` lists local communities: **5 rows** live-verified
  (owner-test-5428, piefed-test, technology, test-882, test-community-541),
  each linking to `/community?iri=…`.
- A "Create a community" form (name/handle/description) + button is present.

### View a community
- `/community?iri=…` detail page renders a header card + member count, an
  in-community search box, and a tab list.
- **Non-creator view** (Technology, created by andrew): Feed + Members tabs + a
  **Join** button + "＋ Post to this community".
- **Creator view** (owner-test-5428, created by alice): all **5 tabs**
  (Feed / Members (0) / Owners / Peers / Requests) + an **Edit community**
  button + "This is your community." banner.

### Create a community
- The "Create a community" form + button are present on `/communities`. The
  server + client (`CreateCommunityAsync`) are integration-tested (A/C); the
  UI path is live-verified present.

### Join / leave
- `JoinButton` on the community header: **live-verified toggle** — clicking
  "Join" switches it to "Leave" (join delivered); clicking "Leave" switches it
  back to "Join" (leave delivered). State reverted after the test.

### Post to a community
- "＋ Post to this community" link → `/compose?community={iri}`. The
  `Compose.razor` `PostToCommunityAsync` path is A/C-tested; the link is
  live-verified present.

### Member list
- The **Members** tab renders (via `PagedCollection` on `{community}/members`)
  with the correct empty state ("No members yet.") for a community with 0
  members. Per-member moderation buttons are creator-only (wired to
  `ILocalModerationClient`, A/C-tested).

### Community moderation
- The **creator-only tabs** (Owners / Peers / Requests) + the **Edit community**
  button render for the community creator. The moderation controls (remove/mute/
  block member, promote/demote owner, accept/reject join requests, add/unfollow
  peers) are wired to `ILocalModerationClient` and integration-tested (A/C).

## Matrix reconciliation (D-column)

All seven Communities rows flipped ☐ → ✅:

| Item | Before | After |
|---|---|---|
| Browse/directory of communities | ☐ | ✅ |
| View a community | ☐ | ✅ |
| Create a community | ☐ | ✅ |
| Join / leave | ☐ | ✅ |
| Post to a community | ☐ | ✅ |
| Member list | ☐ | ✅ |
| Community moderation | ☐ | ✅ |

(Community search was already ✅ from Phase 99.)

## Why a reconciliation slice

A and C were already ✅ with integration coverage. D was ☐ because the web
integration had never been live-verified and the matrix never reconciled. This
turn does exactly that — no new functional code.

## Verification

- `dotnet build` clean; `Iris.Server.Tests` 1105/0; `Iris.Web.Tests` 95/95.
- Live Playwright verification as above (5 communities listed, creator +
  non-creator detail views, Join→Leave→Join toggle, all 5 creator tabs, Members
  tab empty state). No console errors.

## Files

- `docs/plans/production-app-feature-matrix.md` (7 D-column rows reconciled).
