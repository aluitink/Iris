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

## Root cause hypothesis

The "Look up" button's `disabled` state is not bound to the textbox value (or the binding is broken). The button should enable when the textbox is non-empty and contain a valid handle/IRI format. The WASM UI may have a missing or broken `@bind` / event handler that updates the button's enabled state on input change.

## Fix

The "Look up" button should enable when the textbox contains a non-empty value that matches a valid handle (`@host`) or IRI (`https://…`) pattern. The "Follow as this community" button should enable after a successful "Look up" resolves the target actor/community.

## Re-verify

1. On a community page → Peers tab, type a valid handle (e.g., `ii-a1@qa-iris-a.luit.ink`) in the "Follow a community or actor" textbox.
2. Verify the "Look up" button **enables**.
3. Click "Look up" — verify the target actor resolves and displays.
4. Verify the "Follow as this community" button **enables**.
5. Click "Follow as this community" — verify the community's `following` collection includes the target.
6. Verify the target appears in the Peers list.
7. 0 console errors.
