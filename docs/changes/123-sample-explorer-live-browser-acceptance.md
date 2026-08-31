# 123 — Sample Explorer: live-browser acceptance verification (closes §6)

**Status:** DONE — all 10 §6 acceptance criteria verified. 7 in the live browser (Playwright) against the
compose stack; 2 confirmed by code inspection (S8); 1 (build + test green) re-verified this turn.

## Objective

The S1–S8 slices ([114](114-s1-compose-write-path-fix.md)–[122](122-browser-signing-cross-origin-sample.md))
were each verified in-process (and several browser-verified at the time), but the plan's **§6 Acceptance
criteria** checklist was never closed out as a set. This change runs the full manual-exploration checklist
against the live stack and checks the boxes.

## Environment note (the earlier blocker is gone)

The prior sessions were blocked from logged-in browser verification by an orphaned root-owned ActivityPub
server on host port 8081 with CORS locked to origin 8090. That is no longer present: the compose stack
(`iris-a`/`iris-b`/`iris-ui`) is the only thing on those ports, and `iris-a`/`iris-b` now answer CORS
preflights for `http://localhost:8090` (the `Iris__CorsOrigins` default) — `OPTIONS → 204` with
`Access-Control-Allow-Origin: http://localhost:8090` on both 8081 and 8082. The browser checklist was
therefore runnable this turn.

## What was verified

### In the live browser (Playwright, `iris-ui` at `http://localhost:8090`, dials `iris-a` at `http://localhost:8081`)

| §6 criterion | How it was verified | Result |
|---|---|---|
| **Compose works end-to-end** | Log on as `alice@localhost` → Compose → post a note with audience `Public` → the UI reported `StatusCode = 202`; the note (`notes/6f6f392ee2b79090`) appeared in alice's outbox (`totalItems` 2→3) and in the community feed with the exact content posted. No 401. | ✅ |
| **Compose accepts an optional audience (`to`)** | The same post used audience `Public`; the resulting note carried `to: https://www.w3.org/ns/activitystreams#Public`. | ✅ |
| **Navigation works** | From the Home community feed, clicked a note's deep link → navigated to `/object?iri=…` and the object page loaded the note (alice's "Welcome…" + its 1 reply). | ✅ |
| **Home timeline (followed feed)** | Nav → Feed → the followed feed rendered (alice's Like + carla's federated note + bob's notes, newest-first, de-duplicated), 8 deep-linked items. | ✅ |
| **Home page shows community items** | After logon the Home community card rendered the actual recent items (a `Create` + 3 notes) via deep-linked `<ObjectView>`, not a count. | ✅ |
| **Actor detail shows own moderation** | Actor detail (alice) shows both "Moderation (this actor)" and "My moderation (you)". Clicked **Mute** → "My moderation" Mutes refreshed 0→1 (the bypass-cache refresh works in the browser). | ✅ |
| **Relays (F-06)** | Actor detail Relays card: typed `https://relay.example/` + **Subscribe** → the relay appeared (204); **Unsubscribe** → "No relays subscribed" (204). | ✅ |

### Confirmed by code inspection (S8, [121](121-s8-cleanup-oauth2-state.md))

| §6 criterion | Verification | Result |
|---|---|---|
| **No dead OAuth2-state statics** | `grep` for `PendingOAuthState`/`PendingOAuthHandle`/`PendingOAuthDialBase` across `*.razor`/`*.cs` → 0 matches. The state is still generated + sent (`Home.razor:294`); the limitation is documented. | ✅ |
| **`InstanceBaseUrls` wired with a default** | `Program.cs:29` calls `AddIrisExplorer(new InstanceBaseUrls(…))` with the public instance + `localhost`→`http://localhost:8081`. Both logon paths pre-fill the dial base from it. | ✅ |

### Build + test green

`dotnet build` (0 warnings, `TreatWarningsAsErrors`) + `dotnet test` → **870/870** (re-run this turn before
the browser checklist).

## Notes

- **WASM session is per full-page-load.** A full-page navigation (e.g. the browser back/forward or a direct
  URL change) wipes the logged-on session (the `ExplorerSession` statics). The checklist therefore used
  client-side routing (the nav links + in-page deep links) to stay logged in; logon itself is a fresh boot.
  This is a known WASM limitation, not a defect — the in-process tests cover the session logic.
- **The iris-ui container was rebuilt from current source** before the checklist (`docker compose build --no-cache iris-ui`);
  an earlier cached image was still serving pre-S5 markup (a count-only community card), which masked S5 until
  the no-cache rebuild + a browser cache clear.
- The seeded `iris-a` instance advertises `iris-dev1.luit.ink` (from `.env` `IRIS_A_HOST`); the browser dials
  `http://localhost:8081` (the `InstanceBaseUrls` pre-fill for `localhost`) and the S1 `dialBaseUri` discovery
  + AlwaysProxy handle the advertised-vs-dialed mismatch — confirmed working by the successful compose.
