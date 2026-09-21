# S35 — Cross-instance actor discovery fails: remote Mastodon actor unresolvable (Iris proxy → upstream 404 on the Mastodon AP actor doc)

- **Class:** bug / discovery / federation — **Severity:** S1 (blocks following + rendering a remote Mastodon actor; the headline interop path)
- **Status:** open
- **Found:** Interop suite M1/M2/M3 (Iris↔Mastodon), 2026-09-21, fresh QA cluster (Iris A `qa-iris-a.luit.ink` ↔ Mastodon 4.7.2 `qa-mastodon.luit.ink`)
- **Related:** S2 (signed-out proxy 401), S5 (search orphan actor), S7 (directory external lookup), S14 (signed-out actor detail CSP). This one is specifically **cross-instance actor *discovery* via the signed proxy against a Mastodon peer.**

## Symptom

On Iris, searching for a **known-good remote Mastodon** handle fails to find the actor, even though the handle is resolvable:

- Iris `im-user` searches `imuser@qa-mastodon.luit.ink` → **"0 result(s). No matches found."** + **1 console error (404)**.
- Wire (signed session, from Iris's own proxy):
  1. `POST https://qa-iris-a.luit.ink/ap/v1/proxy/https%3A%2F%2Fqa-mastodon.luit.ink%2F.well-known%2Fwebfinger%3Fresource%3Dacct%3Aimuser%40qa-mastodon.luit.ink` → **200** (webfinger resolves; Iris correctly obtains the `self` IRI `https://qa-mastodon.luit.ink/ap/users/117306213651189335`).
  2. `POST https://qa-iris-a.luit.ink/ap/v1/proxy/https%3A%2F%2Fqa-mastodon.luit.ink%2Fap%2Fusers%2F117306213651189335` → **404** (21-byte JSON error, `content-type: application/json`, `iris-version: 1`). Iris then reports "no matches".

So the **webfinger leg works** but the **actor-doc fetch leg 404s**, and the whole discovery collapses.

## Root cause (suspected) — attribution: **Mastodon-side (this cluster), not Iris**

The 404 is **not** an Iris signature/routing defect:

- The **same URL** 404s when fetched **directly** from the Mastodon server (no Iris in the loop):
  - `GET https://qa-mastodon.luit.ink/ap/users/117306213651189335` (with `Accept: application/activity+json`, `-L`) → **404**, empty body.
  - `GET …/ap/users/117306213651189335` (no Accept header) → **404**.
- Iris's proxy sends the correct `Accept: application/activity+json` (it does for every AP fetch) — so the 404 is the **Mastodon server** rejecting the path, not a missing/incorrect header from Iris.
- **Corroborating Mastodon-side anomaly — every route for this account 404s**, yet the account demonstrably exists:
  - `GET /ap/users/117306213651189335` (the webfinger `self`) → **404** (AP accept, direct **and** via Iris proxy).
  - `GET /users/imuser` → **404**; `GET /users/imuser/activity` → **404**.
  - `GET /api/v1/accounts/imuser` → **404**; `GET /api/v1/accounts/117306213651189335` → **404**; `GET /api/v1/accounts/imuser/statuses` → **404**.
  - `GET /@imuser` → **404**.
  - **But:** `GET /api/v1/timelines/public` → **200** `[]`, `GET /api/v1/instance/activity` → **200**, `stats.user_count: 1`, webfinger `acct:imuser@…` → **200**.
  - So the account **exists** and is **federated-resolvable via webfinger**, yet its **AP actor doc, public-API records, and profile page are all 404**. A healthy Mastodon account serves all of these.

**Conclusion:** the pre-seeded `imuser` account's **ActivityPub actor document is not served** (404 on the canonical `self` IRI), and its public-API records are likewise missing. This makes the account **federally unusable as a follow/render target** regardless of the peer. It is a **Mastodon provisioning/cluster** issue, not an Iris defect. **Iris behavior is correct** given the input (it correctly 404s and surfaces no match).

> **Action for operator:** fix the Mastodon cluster so `/ap/users/{id}` (and `/api/v1/accounts/{handle}`, `/@{handle}`) serve the account's AP doc — e.g. re-provision the pre-seeded account or the container. Until then, **M2–M12 that require reading the Mastodon UI are BLOCKED** (see `iris-mastodon.md` run log). If the operator instead supplies the pre-seeded `imuser` password, M2.2/M4.2/etc. can still be UI-verified on the Mastodon side, but the **Iris→Mastodon discovery/follow** (M2.1/M3.1) will remain blocked by this 404.

- **Iris actor page (second discovery entry point) fails identically:** navigating to `Iris A /actor?iri=https://qa-mastodon.luit.ink/ap/users/117306213651189335` (signed in) → alert **"Actor not found."** + 2 console errors (the same proxy 404). So **both** fediverse-search and direct-actor-page discovery collapse on the same upstream 404.

## Secondary (Iris UX, minor)

Even on a clean 404, Iris's search shows "No matches found." with **no error toast** beyond the console 404 — a remote-actor 404 during fediverse search is silent to the user (they can't tell *why* nothing was found). The actor page does at least surface "Actor not found." Minor, but a "could not reach {host}" hint would help. (Fold into S5/S7 if those cover search/discovery UX.)

## Re-verify (once Mastodon cluster is fixed)

1. `GET https://qa-mastodon.luit.ink/ap/users/117306213651189335` (`Accept: application/activity+json`) → **200** AP `Person` doc. ← the Mastodon-side fix
2. `GET /api/v1/accounts/imuser` → **200** (public lookup works).
3. Iris: search `imuser@qa-mastodon.luit.ink` → actor appears (no 404 in network), clickable profile renders name/avatar/summary.
4. Iris: follow `imuser@qa-mastodon.luit.ink` → outbox `Follow` with `object` = the Mastodon actor IRI; Mastodon `/@imuser` Followers lists `im-user@iris.luit.ink`.
