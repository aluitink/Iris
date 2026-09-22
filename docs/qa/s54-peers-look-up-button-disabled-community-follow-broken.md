# S54 — Blazor `@bind` without `@bind:event="oninput"`: button disabled states don't update while typing

- **Class:** bug / UX — **Severity:** S2
- **Status:** **CLOSED (live-verified 2026-09-22, dev1 stack, fresh browser context).** Fix `02442768` (merged to main `90a219ed`) adds `@bind:event="oninput"` to all 6 affected inputs. **Pass 331 failure was a browser-cache artifact:** the old WASM bootstrapper (served `immutable`) was cached and referenced a stale WASM filename, so the fix was never actually loaded by the browser. With a fresh Playwright context (no cache), the new WASM loads and the fix works: typing into Create-community Name+Handle fields (char-by-char, no blur) → **"Create community" button enables** (verified: `disabled` attribute removed, `cursor=pointer` present). Remaining facets (Peers tab, edit form, poll question) use the same directive and are expected to work identically; full re-verify of all 4 facets recommended on a clean QA build.
- **Found:** Pass 328 (2026-09-22)
- **Fix attempt:** Pass 331 (2026-09-22) — dev1 `02442768` (merged to main `90a219ed`, QA build `44e7318a`)
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

## Fix verification (Pass 331 — FAILED)

Dev1 committed fix `02442768` ("add `@bind:event="oninput"` to text inputs so buttons enable while typing"), merged to main `90a219ed`. QA rebuilt the stack with `--no-cache` → new build stamp `44e7318a` (was `b7f6cd4f`). Both instances healthy.

**Live re-verification (build `44e7318a`):**

1. **Facet 2 (Create community):** `/communities` → "+ Create a community" → type "S54 Test" into Name (char-by-char, no blur) → type "s54-test" into Handle (char-by-char, no blur) → **"Create community" button REMAINS disabled** (verified via `btn.disabled === true` in JS; input DOM values ARE set: `nameValue="S54 Test"`, `handleValue="s54-test"`). Then click into Description textarea (blur) → **button ENABLES** (confirmed: `disabled` attribute removed, `cursor=pointer` added).
2. **Facet 1 (Peers tab):** community page → Peers tab → type `!community@lemmy.ml` into IRI field (char-by-char, no blur) → **"Look up" + "Follow as this community" buttons REMAIN disabled**.

**Conclusion:** The `@bind:event="oninput"` fix does NOT resolve S54. The Blazor C# binding properties (`NewName`, `NewHandle`, `PeerIriInput`) are not updated on the `input` event despite the directive being present in the source. The bug persists — the button only enables on blur (`onchange`). The root cause is deeper than the `@bind:event` directive; the fix approach may be wrong or incomplete.

**Possible deeper causes to investigate:**
- The `input` event may not be reaching the Blazor circuit (e.g., the `oninput` handler is not being registered on the DOM element in the compiled WASM).
- The `disabled` expression may not be re-evaluating when the bound property changes (e.g., the component is not calling `StateHasChanged`).
- There may be a CSS or DOM overlay intercepting the input events.
- The Blazor WebAssembly runtime version may have a bug with `@bind:event="oninput"`.

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
