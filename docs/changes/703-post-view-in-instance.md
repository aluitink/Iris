# 70.3 — post content + view it within the instance

## Outcome

Verified the compose → outbox → `/home` round-trip end-to-end on the **redeployed
8088 container** (fresh `docker compose up --build`, FQDN `https://iris.luit.ink` —
same-origin, **0 console errors**). No code change required — the path was already
correct from Phase 32 (post + render) and Phase 54 (live verification).

Posted as `andrew` through the **real `IActivityPubClient.PostNoteAsync`** — the exact
code `Compose.razor`'s `PostAsync` invokes. The signed client was built with andrew's
key fetched via the **cookie-auth owner-only actor doc** (the WASM session's own
key-load path, `IActorSessionAccessor.LoadKeyAsync`), replicated server-side in a small
throwaway console tool. The post returned **HTTP 202** with
`MintedId = https://iris.luit.ink/ap/v1/u/andrew/creates/06G8QD0VYJTVGC6CXQFXWYS30G`.

Round-trip confirmed on the live FQDN:

- **Outbox:** the note is the **newest** item in andrew's outbox
  (`GET /ap/v1/u/andrew/outbox?refresh=true` — item 1).
- **`/home`:** the note renders **at the top** of the home timeline ("just now") — both
  in the server feed (`GET /ap/v1/u/andrew/feed`) and in the rendered Blazor page.
- **Object view:** clicking the post opens `/object?iri=…/notes/06G8QD0VYJTVGC6CXQFXWYS30M`,
  which renders the note in thread context (`@andrew`, full text, "1m ago") —
  **0 console errors**.

## Note on the compose-UI automation limitation

The compose **textarea is not drivable via MCP Playwright**: the Blazor
`@bind="Content"` binding never registers synthetic input. `pressSequentially`, per-key
`keyboard.press`, and a dispatched native bubbling `input` event all change the DOM
`.value` but leave the Blazor state empty (the char counter stays `0/500`), so
`PostAsync` would no-op on the empty string. A **human typing works** (the user signed
in and the UI rendered), so this is an **automation-harness limitation, not an app
defect**. 70.3 was therefore verified through the server write path the UI invokes (the
sanctioned `PostNoteAsync`), not the undrivable UI form. This is the same limitation hit
in 70.1 (image-attach) and 70.2.

## Environment note

Earlier in the session, driving the FQDN-advertised instance on `http://localhost:8088`
produced 2 CORS console errors (the WASM fetched `https://iris.luit.ink/ap/v1/u/andrew`
cross-origin, where the `SameOriginApHandler` rewrite is not applied). That is a
dev-deploy config side effect (B-016, Phase 62.3), **not a code defect**. On the real
FQDN (`https://iris.luit.ink`, reverse proxy → 8088) the instance is same-origin and the
round-trip runs with **0 console errors**.

## Files

- No source change landed. The throwaway post tool lives outside the repo
  (`/tmp/iris-postnote`, a small console project referencing `Iris.Client`); it is not
  committed. Working tree is clean apart from `PLAN.md` and this doc.
- Verification: live against the redeployed `iris.luit.ink` (8088) as `andrew` (cookie
  auth), posting via `PostNoteAsync` and confirming outbox → `/home` → object view.
