# Interop Manual Test Suites (QA agent, MCP Playwright)

> Manual test cases for verifying **ActivityPub interop** between the Iris instance and its
> peers: **Iris↔Iris**, **Iris↔Mastodon**, **Iris↔Lemmy**. Written for the **QA agent** to execute
> with **MCP Playwright** (`playwright-mcp2_*` tools): every step is a browser action the agent can
> drive, every assertion is either a **visual inspection** (screenshot + snapshot) or a **wire
> check** the agent can perform via `browser_evaluate` fetch or the app's own UI surface.
>
> **Fresh-stack assumption:** all tests assume the three systems were **just spun up** — no
> accounts, follows, or content exist yet. Every test is **self-provisioning**: it creates the
> accounts and content it needs as its first steps and leaves behind a known-good end state.
> Tests within a suite run **in order** (later tests depend on earlier ones' accounts/follows);
> the three suites are independent of each other.

## Files

| Suite | File | Peer | Accounts created |
|---|---|---|---|
| Iris ↔ Iris | [iris-iris.md](iris-iris.md) | two fresh Iris instances | `ii-a1`, `ii-a2` (instance A), `ii-b1` (instance B) |
| Iris ↔ Mastodon | [iris-mastodon.md](iris-mastodon.md) | Mastodon 4.7 | `im-user` (Iris), `imuser` (Mastodon) |
| Iris ↔ Lemmy | [iris-lemmy.md](iris-lemmy.md) | Lemmy 0.19 | `il-user` (Iris), `iluser` (Lemmy) |

## Environment

| System | URL | How it runs |
|---|---|---|
| **Iris (A / primary)** | `https://qa-iris-a.luit.ink` | shared federation stack — `iris-a` service (container `qa-iris-a`) |
| **Iris (B / second instance)** | `https://qa-iris-b.luit.ink` | shared federation stack — `iris-b` service (container `qa-iris-b`) |
| **Mastodon** | `https://qa-mastodon.luit.ink` | shared federation stack — `mastodon-*` services (front: `qa-mastodon-proxy`) |
| **Lemmy** | `https://qa-lemmy.luit.ink` | shared federation stack — `lemmy*` services (front: `qa-lemmy-proxy`) |

**One stack, two environments.** All four systems live in a single parameterized compose file —
`environments/stack/docker-compose.yml` — that serves both dev and QA. The only differences
(FQDNs, host ports, container-name prefix, DB passwords, Mastodon crypto keys) come from a
per-environment `.env`:

```
# QA (this suite):
docker compose -f environments/stack/docker-compose.yml --env-file environments/qa/.env -p qa up -d --build
environments/stack/provision-mastodon.sh qa          # creates the Mastodon suite accounts

# Dev:
docker compose -f environments/stack/docker-compose.yml --env-file environments/dev/.env -p dev up -d --build
```

The `-p <project>` flag scopes every named volume + network under the project name (`qa_*` vs
`dev_*`), so dev and QA never share state. The compose service names are unprefixed
(`iris-a`, `mastodon-web`, `lemmy`, …); the `${ENV_PREFIX}` (`dev-`/`qa-`) is applied only to
`container_name` for `docker ps` clarity. `https://iris.luit.ink` is the production/public FQDN and
is **not** part of this stack (production Iris packaging lives in `apps/Iris.Web/`).

All four systems sit on the stack's single `federation-net` bridge network; federation traffic
flows over the network, not through the browser. The browser is used only for **driving**
(actions) and **observing** (UI state, console, network tab).

**Fresh-stack preconditions (verify before the first test of a suite):**

1. Each system answers its public FQDN over HTTPS (the external proxy terminates TLS).
2. Iris: `GET /.well-known/webfinger?resource=acct:<any>` behaves (404 is fine on a fresh stack —
   it proves the route exists, no 5xx).
3. Mastodon: `/` renders the login page; Lemmy: `/` renders the login page.
4. No accounts from prior runs exist — if they do, either wipe the relevant volume (`down -v`)
   or note it and adapt handle names (the suites use fixed handles; a collision means the stack is
   **not** fresh).

**Playwright session rules (from QA_LOOP.md):** fresh browser context per suite (close + reopen
between suites), hard reload / `networkidle` before reading any state after a federation action,
caching disabled.

## How the QA agent executes a test

Each test follows one shape:

1. **Preconditions** — what must already exist (from earlier tests in the same suite).
2. **Steps** — numbered browser actions. The agent executes them literally with MCP Playwright:
   - `browser_navigate` + `browser_wait_for` (wait for `networkidle`-equivalent text to settle)
   - `browser_snapshot` (accessibility tree — the primary source of truth for element refs)
   - `browser_click` / `browser_type` / `browser_fill_form` with the snapshot refs
   - `browser_take_screenshot` **with no `filename`** (auto-saves to `tmp/.playwright-mcp/`,
     returns the path) — required at every step marked **📸 visual**
   - `browser_console_messages` + `browser_network_requests` at steps marked **🌐 network**
   - `browser_evaluate` for the steps marked **🔌 wire** (fetch of an AP resource — public GETs
     need no auth; the page origin has no CORS restriction on these)
3. **Assertions** — each has a **type** the agent checks mechanically:
   - **visual** — "the screenshot shows X": compare against the stated expectation (element
     present/absent, text readable, layout sane). Fail on missing/malformed rendering.
   - **wire** — "the JSON response contains X": the agent fetches the IRI (via `browser_evaluate`
     fetch, or `browser_network_requests` if the app itself fetched it) and checks fields.
   - **behavioral** — an action produced the expected state transition (button label flipped,
     item appeared/disappeared in a list) **after** a hard reload (federation is async — always
     re-navigate + wait before asserting remote effects, and retry once after ~10 s if the
     federation delay may not have elapsed).
4. **Result** — record PASS/FAIL + evidence (screenshot paths, the relevant JSON field values,
   console errors) in the run log (see below). A FAIL produces a finding doc in `docs/qa/`
   (next S-number) with this test's repro.

**Federation delay note:** signed delivery + remote processing is not instantaneous. Any
assertion about **the remote system's** state should be made after a hard reload of the remote
system's page, and retried once after ~10 s. Iris-side assertions (outbox, local stores surfaced
in UI) are immediate.

## Run log

Per suite, the agent appends to the run-log section at the bottom of the suite file:

```
| Test | Result | Evidence (screenshot paths / JSON values) | Date |
|---|---|---|---|
```

Findings are recorded in `docs/qa/` (one doc per defect, next S-number) and cross-referenced in
the run log. The suites themselves are **not** edited when a test fails — they are the spec.

## Conventions used in the suites

- **📸 visual** — take a screenshot; assert on what it shows.
- **🌐 network** — capture `browser_network_requests` (counts + statuses) and console messages.
- **🔌 wire** — fetch the named IRI (public AP GET) and assert on JSON fields.
- **⟳ reload** — hard reload / re-navigate + wait before asserting (fresh state, no cache).
- **Handles are lowercase** (Iris handle rules). Mastodon and Lemmy handles are also lowercase
  where the platform allows; each suite lists its exact handles in its header.
- **Post body convention:** every created post body contains a unique token
  (`II-A1-1`, `IM-A1-1`, `IL-A1-1`, …) so the agent can find "the" post in any feed/timeline by
  text match — no ambiguity when feeds contain multiple posts.
- **Community convention:** each suite creates one community per platform involved
  (e.g. `ii-a-comm` on instance A) unless a test states otherwise.
- **Pass/fail discipline:** a test PASSES only when **every** assertion passes. One failed
  assertion = test FAIL (record which), but continue with the remaining steps to maximize
  diagnostic value, marking subsequent steps' results as `skipped(cascade)`.
