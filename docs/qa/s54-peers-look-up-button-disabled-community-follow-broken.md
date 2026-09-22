# S54 — Peers "Look up" button permanently disabled: cannot follow actors/communities as a community

- **Class:** bug / feature-gap — **Severity:** S2
- **Status:** open
- **Found:** Pass 328 (2026-09-22)
- **Related:** S4 (communities Following tab — fixed), S30 (cross-instance community join — fixed)

## Symptom

On a community page → **Peers** tab:

1. The "Follow a community or actor" textbox accepts input (e.g., `ii-a1@qa-iris-a.luit.ink`).
2. The **"Look up" button remains disabled** even after typing a valid handle/IRI.
3. The "Follow as this community" button is also disabled.
4. The feature is **completely unusable** — a community owner cannot follow any actor or community on behalf of their community.
5. 0 console errors.

**The community's Peers feature is non-functional.**

## Root cause

`CommunityDetail.razor:249` — the peer IRI textbox uses plain `@bind="PeerIriInput"`, which defaults to `@bind:event="onchange"` in Blazor. The C# property is only updated when the input **loses focus**, not on every keystroke. The "Look up" button (line 267) checks `string.IsNullOrWhiteSpace(PeerIriInput)` in its `disabled` expression, which is re-evaluated during render — but `PeerIriInput` is never updated while typing, so the button never enables.

**Contrast:** the community feed search box in the same file (line 160) uses `@bind="FeedQueryInput" @bind:event="oninput"` — so its Search button enables as you type.

## Fix

Add `@bind:event="oninput"` to the `#peer-iri` input (line 249), matching the feed-search pattern at line 160:
```razor
<input id="peer-iri" type="text" @bind="PeerIriInput" @bind:event="oninput" ... />
```

## Re-verify

1. On a community page → Peers tab, type a valid handle (e.g., `ii-a1@qa-iris-a.luit.ink`) in the "Follow a community or actor" textbox.
2. Verify the "Look up" button **enables**.
3. Click "Look up" — verify the target actor resolves and displays.
4. Verify the "Follow as this community" button **enables**.
5. Click "Follow as this community" — verify the community's `following` collection includes the target.
6. Verify the target appears in the Peers list.
7. 0 console errors.
