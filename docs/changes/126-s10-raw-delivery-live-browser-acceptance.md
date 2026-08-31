# 126 — Sample Explorer: S10 raw-delivery live-browser acceptance

**Status:** DONE — the new `/deliver` page (change [125](125-s10-raw-delivery-screen.md)) is verified
against the live compose stack (iris-a/iris-b/iris-ui) with Playwright. Both the success (202 + edge
recorded) and 404 (unknown inbox) paths are confirmed in the browser.

## What was verified

The iris-ui image was rebuilt `--no-cache` (it had been serving a pre-S10 WASM — the container predates
the S10 commit) and the MCP browser cache cleared via CDP (`Network.clearBrowserCache` + cache/service-worker
clear + a `?fresh=1` cache-buster) so the new `SampleBlazorClient.pcm2zlk1vk.wasm` loaded.

| Behavior | How verified | Result |
|---|---|---|
| **Raw Follow → a real actor's inbox** | Log on as `alice@localhost` (base `http://localhost:8081`) → nav "Raw delivery" → target `…/u/bob` → "Deliver Follow to target's inbox" | **`Status: 202 (success)`** + the signed `Follow` JSON shown (`actor` = alice, `object` = bob, `type: Follow`, deterministic IRI `…/alice/follows/…/bob`). |
| **The follow edge is recorded** | `GET /ap/v1/u/bob/followers` after the delivery | bob's followers now include `…/u/alice` — the raw delivery was recorded by the inbox processor, not just accepted. |
| **Raw Follow → an unknown actor's inbox** | target `…/u/ghost` → deliver | **`Status: 404`** (not success) — the inbox endpoint's "recipient must exist" check, surfaced by the page. |

This closes the last §3.1 item's acceptance: `DeliverAsync` is exercised **standalone in the browser**
(202 + recorded edge on the happy path; 404 on a bad target), not only through the high-level helpers and
not only in-process.

## Notes

- **The signed JSON shown is the exact payload `DeliverAsync` sends** — the screen serializes the
  `Follow` with `ActivityJson.Serialize` and POSTs that same object; the browser confirms the payload the
  server signature-validated (202) and recorded.
- **The 404 console error** after the ghost delivery is the expected network-level 404 (the page reports
  it as `Status: 404`); it is not a JS fault.
- The full §3.1 client-method gap list is now both **wired in the UI** (S9 `GetActorAsync`, S10
  `DeliverAsync`, plus S2–S8) **and live-browser-verified** (S10 here; S1–S8 in change
  [123](123-sample-explorer-live-browser-acceptance.md)). `GetFollowFeedAsync` remains client-tested
  (the typed method can't carry the `next`-link the paginated Feed page needs — the page keeps the paged
  `GetCollectionAsync`).
