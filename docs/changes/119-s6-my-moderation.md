# 119 — S6: Actor detail shows the logged-on actor's own moderation

**Status:** DONE — full solution green (`dotnet build` 0 warnings; `dotnet test` 866/866).

## Objective

`ActorDetail.razor` showed the **target** actor's mutes/blocks/flags counts while the write buttons
(Mute/Block/Flag) acted **as the logged-on actor**. The user could not tell the two apart, and the buttons'
effect was invisible. S6 adds the logged-on actor's **own** moderation state (their `mutes`/`blocks`/`flags`
collections) so the user sees what *they* have muted/blocked/flagged.

## Changes

### `samples/SampleBlazorClient/Pages/ActorDetail.razor`

- **Relabeled** the existing counts card from "Moderation" to **"Moderation (this actor)"** with a note that
  those are the target actor's own collections (not your moderation of them).
- **Added a "My moderation (you)" card** showing the logged-on actor's own mutes/blocks/flags counts
  (`MyModeration`), with a note that the Mute/Block/Flag buttons below change these.
- **`MyModeration`** state: `(int Mutes, int Blocks, int Flags)?`, loaded in `LoadByIriAsync` from the
  logged-on actor's own `Session.ActorIri` collections (`GetMutesAsync`/`GetBlocksAsync`/`GetFlagsAsync`
  on the self IRI), and reset on load.
- **`RefreshMyModerationAsync(bypassCache)`** re-reads the self collections; called from
  `FollowUnfollowAsync` (the Mute/Block/Flag/Unmute/Unblock/Unflag path) with `bypassCache: true` after a
  write so the update is visible immediately (the server re-renders the cached collection page only on
  `?refresh=true`, which the client's `BypassCache` now sends — the S4 fix).

## Test coverage

- `S6MyModerationTests` (2 new) — in-process, logged on as `alice`, target `bob`:
  - **MyModeration_Mute_Block_Flag_AppearInOwnCollections** — starts 0/0/0; mute (204) → own mutes = 1;
    block (202) → own blocks = 1 (mutes still 1); flag (202) → own flags = 1 (all 1/1/1), each read
    bypassing the page cache.
  - **MyModeration_Unmute_Unblock_Unflag_RemoveFromOwnCollections** — seeds all three (1/1/1), removes each,
    asserts the own counts return to 0/0/0.
- Full solution green: **866/866** (SampleBlazorClient.Tests now 71: 69 → +2).

## Notes

- The self collections are the same `mutes`/`blocks`/`flags` collections the target card reads, just for the
  logged-on actor's IRI — so the page now shows both the target's own state and the viewer's own state.
- Logged-in *browser* verification is blocked by the environment's orphaned root-owned 8081 server (CORS
  locked to origin 8090) — an environment constraint, not a code defect. The in-process tests exercise the
  identical client API + server pipeline the card uses.
