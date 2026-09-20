# 1595 — Fix: Auto-accepted follows incorrectly filtered from notifications

- **Date:** 2026-09-20
- **Status:** FIXED
- **Related:** [docs/qa/](../qa/README.md) — QA Pass 91 finding

## Summary

The notification endpoint (`GET /local/v1/notifications`) was incorrectly filtering out
**all** Follow notifications that were not in the pending follow-request queue. This meant
auto-accepted follows (from users without `manuallyApprovesFollowers` set) were never visible
in notifications, because they were never in the queue to begin with.

## Root cause

The filter logic in `WebAppFactory.cs` (lines 1027-1046) was:

```csharp
// Filter out Follow notifications that are no longer pending (already accepted/rejected).
var pendingRequests = await persistence.Follows.GetFollowRequestsAsync(account.ActorId, ct);
var pendingSet = pendingRequests.Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
filtered = filtered.Where(item =>
{
    if (item is not Activity { Type: { } t } act) return true;
    if (t.FirstOrDefault() != "Follow") return true;
    
    var requesterIri = act.Actor?.FirstOrDefault()?.ResolveObjectIri();
    return requesterIri is { } ri && pendingSet.Contains(ri.Value);  // <-- BUG
}).ToList();
```

The bug: for auto-accepting accounts (no `manuallyApprovesFollowers`), follows are never added
to the pending queue (they're auto-accepted immediately). So `pendingSet` is empty, and the
filter removes **all** Follow notifications — even the legitimate "someone followed you"
notifications that should be visible.

## Fix

The fix adds a check: only filter out Follow notifications when the account has
`manuallyApprovesFollowers` set. For auto-accepting accounts, Follow notifications remain
visible (they indicate who followed you, which is meaningful even if not actionable).

```csharp
var accountManuallyApproves = await IsAccountManuallyApprovesFollowersAsync(persistence, account.ActorId, ct);
filtered = filtered.Where(item =>
{
    // ... (same type checks) ...
    
    if (pendingSet.Contains(ri.Value)) return true;  // Pending: always show
    
    if (accountManuallyApproves) return false;  // Manually approving + not pending: hide
    
    return true;  // Auto-accepting: show (meaningful notification)
}).ToList();
```

## Tests added

- `NotificationFollowFilterIntegrationTests.AutoAcceptedFollow_RemainsVisibleInNotifications` —
  verifies that a follow from an auto-accepting account remains visible in notifications.
- `NotificationFollowFilterIntegrationTests.ManuallyApprovedFollow_FilteredOutWhenDecided` —
  verifies that a follow from a manually-approving account is filtered out once decided
  (accepted/rejected).

## Result

Follow notifications now behave correctly:
- **Auto-accepting accounts:** Follow notifications remain visible (show who followed you).
- **Manually-approving accounts:** Follow notifications are visible while pending, filtered out
  once decided (accepted/rejected) — no historical noise.
