# S9 — Report/flag is a silent no-op (no feedback, duplicate flags)

- **Class:** UX / bug — **Severity:** S2
- **Status:** fixed (2026-09-20, verified Pass 38 on rebuilt container after `d729c66` deploy)
- **Found:** Pass 16 (2026-09-20) — re-confirmed Passes 27, 35; **fixed + verified Pass 38**

## Symptom

Clicking **Report** (actor detail or post card) fires the flag correctly — `FlagAsync` → `POST …/outbox` → **202**, and it **is** recorded (shows under **Settings → Moderation → "Reported"** with the actor IRI) — but there is **no toast, no in-context state change, no reason prompt, and the button never changes/disables** → zero confirmation the report landed, and it can be clicked again to file **duplicate** flags. Contrast with Block/Mute, which visibly toggle.

## Root cause

- `FlagAsync` (`ModerationActions.razor:197-217`) only sets a private `_flagIri` — never rendered, no `StateHasChanged`/toast.
- `CardReportAsync` (`ObjectView.razor.cs:1228-1249`) swallows the result in an empty `catch` with no feedback.
- Neither prompts for a reason nor disables the button after a successful flag.

## Fix

Surface a confirmation after a successful flag (toast or "Reported ✓" state), disable / de-dupe the button once reported, and optionally prompt for a short reason.

## Re-verify

Report an actor (detail page) and a post (card): a confirmation is shown, the control reflects the reported state and can't be re-clicked into a duplicate, and the entry appears under Settings → Moderation → Reported.

**Re-verification evidence (Pass 27, 2026-09-20, andrew):** flagged bob from his post card → **no toast, no state change, 0 console errors**; the flag **was** recorded (visible under Settings → Moderation → Reported) and was subsequently cleaned up. STILL OPEN.

**Re-verification evidence (Pass 35, 2026-09-20, andrew, deployed `bb28dcf`):** flagged bob from his actor-detail page → **no toast, no button state change, 0 console errors**; clicked Report again (duplicate accepted, no de-dupe). Settings → Moderation → "Loading moderation lists…" **stuck indefinitely** (could not confirm the flag was recorded; 0 console errors). STILL OPEN.

**Re-verification evidence (Pass 38, 2026-09-20, andrew, rebuilt container post-`d729c66`):** flagged bob from his actor-detail page → button immediately changes to **"Reported ✓"** and becomes **disabled** (can't re-click). 0 console errors. **FIXED.**
