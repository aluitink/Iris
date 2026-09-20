# 1576 — Report/flag silent no-op (S9)

**Severity:** S2
**Status:** Fixed

## Problem

Clicking Report (actor detail or post card) fired the flag correctly (202, recorded under
Settings → Moderation → Reported) but gave zero feedback: no toast, no state change, the
button never disabled. Users could click again to file duplicate flags.

## Root cause

- `ModerationActions.razor` (`FlagAsync`): set a private `_flagIri` that was never rendered;
  no `StateHasChanged()` call after the flag landed.
- `ObjectView.razor.cs` (`CardReportAsync`): swallowed the result in an empty `catch`;
  no "reported" state tracked; `CardModerationButtons` had no way to show a reported state.

## Fix

**`ModerationActions.razor`** (actor detail):
- Added `IsReported` property; reset on target change.
- `FlagAsync` now sets `IsReported = true` on success and calls `StateHasChanged()` in `finally`.
- The Report button shows "Reported ✓" and is disabled when `IsReported` is true.
- `FlagAsync` also guards against re-entry when `IsReported` is already true (dedup).

**`ObjectView.razor.cs`** (post cards):
- Added `_reportedAuthorIri` field to remember which author this card has reported.
- `CardReportAsync` now checks `result.IsSuccess` and sets `_reportedAuthorIri` on success.
- Guards against re-entry when the same author is already reported (dedup).

**`CardModerationButtons.razor`**:
- Added `IsReported` parameter.
- The Report button shows a checkmark SVG and is disabled when `IsReported` is true.
- The `title`/`aria-label` show "Reported" instead of "Report @handle" when reported.

**`ObjectView.razor`**:
- All three `CardModerationButtons` sites now pass `IsReported` based on
  `_reportedAuthorIri == modAuthor`.

## Verification

- Build clean (0 warnings, 0 errors).
- `Iris.Web.Tests` 106/106 pass.
- Live-verified (signed-in, fresh browser context):
  - Actor detail (alice): Report → "Reported ✓" + disabled.
  - Post card (bob, via search): Report → checkmark + disabled; other authors unaffected.
