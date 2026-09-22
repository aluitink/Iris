# S54 — Blazor `@bind` without `@bind:event="oninput"`: button disabled states don't update while typing

- **Class:** bug / UX — **Severity:** S2
- **Status:** open
- **Found:** Pass 328 (2026-09-22)
- **Related:** S4 (communities Following tab — fixed), S30 (cross-instance community join — fixed)

## Symptom

**Facet 1 — Community Peers tab (found Pass 328):**
1. On a community page → **Peers** tab, the "Follow a community or actor" textbox accepts input (e.g., `ii-a1@qa-iris-a.luit.ink`).
2. The **"Look up" button remains disabled** even after typing a valid handle/IRI.
3. The "Follow as this community" button is also disabled.
4. The feature is **completely unusable** — a community owner cannot follow any actor or community on behalf of their community.
5. 0 console errors.

**Facet 2 — Community creation form (confirmed Pass 328):**
1. On `/communities`, click "+ Create a community".
2. Type a name in the "Name" textbox (e.g., "QA Pass 328 Test Community").
3. The **"Create community" button remains disabled** even after typing a valid name + handle.
4. The user must **blur the field** (Tab out or click elsewhere) for the button to enable.
5. This makes the community creation UX confusing — the form appears broken.

**Facet 3 — Community edit form (confirmed Pass 328):**
1. On a community page, click "Edit community".
2. Change the name in the "Name" textbox.
3. Click "Save".
4. The UI still shows the **old name** — the edit silently fails.
5. The DB is **not updated** (verified via psql).
6. Root cause: the edit form's `@bind="EditName"` (line 89) uses `onchange` (blur), so the C# property isn't updated while typing. When "Save" is clicked, the stale `EditName` value is submitted.

## Root cause

Multiple Blazor text inputs use plain `@bind="Property"`, which defaults to `@bind:event="onchange"` — the C# property is only updated when the input **loses focus**, not on every keystroke. Buttons that check `string.IsNullOrWhiteSpace(Property)` in their `disabled` expression never enable while the user is typing.

**Affected inputs (confirmed via code review):**

| File | Line | Input | Controls |
|---|---|---|---|
| `CommunityDetail.razor` | 249 | `#peer-iri` (`@bind="PeerIriInput"`) | "Look up" + "Follow as this community" buttons |
| `CommunityDetail.razor` | 89 | `#edit-community-name` (`@bind="EditName"`) | Community edit save (submits stale value) |
| `CommunityDetail.razor` | 94 | `#edit-community-summary` (`@bind="EditSummary"`) | Community edit save (submits stale value) |
| `Communities.razor` | 31 | `#community-name` (`@bind="NewName"`) | "Create community" button |
| `Communities.razor` | 35 | `#community-handle` (`@bind="NewHandle"`) | "Create community" button |
| `Compose.razor` | 133 | `#compose-poll-question` (`@bind="PollQuestion"`) | Poll submission (sibling option inputs at lines 140-141 **do** have `@bind:event="oninput"`) |

**Contrast:** the community feed search box (`CommunityDetail.razor:160`) and poll option inputs (`Compose.razor:140-141`) correctly use `@bind:event="oninput"`.

## Fix

Add `@bind:event="oninput"` to all affected text inputs:
- `CommunityDetail.razor:249` — `#peer-iri`
- `CommunityDetail.razor:89` — `#edit-community-name`
- `CommunityDetail.razor:94` — `#edit-community-summary`
- `Communities.razor:31` — `#community-name`
- `Communities.razor:35` — `#community-handle`
- `Compose.razor:133` — `#compose-poll-question`

## Re-verify

**Peers tab:**
1. On a community page → Peers tab, type a valid handle (e.g., `ii-a1@qa-iris-a.luit.ink`) in the "Follow a community or actor" textbox.
2. Verify the "Look up" button **enables while typing** (no blur required).
3. Click "Look up" — verify the target actor resolves and displays.
4. Verify the "Follow as this community" button **enables**.
5. Click "Follow as this community" — verify the community's `following` collection includes the target.
6. Verify the target appears in the Peers list.
7. 0 console errors.

**Community creation:**
1. Navigate to `/communities` → "+ Create a community".
2. Type a name and handle.
3. Verify the "Create community" button **enables while typing** (no blur required).
4. Click "Create community" — verify the community is created.
5. 0 console errors.

**Poll question:**
1. Compose a Poll post.
2. Type a poll question.
3. Verify the question text is available when submitting (no stale value).
