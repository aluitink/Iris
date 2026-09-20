# S5 — Search lists a stale orphaned local actor (localhost IRI)

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** fixed (2026-09-20, `456b0d9`)
- **Found:** Pass 13 (2026-09-20) — re-confirmed Passes 15, 27, 31; **fixed + live-verified Pass 32**

## Symptom

Search for "alice" returns **two `alice` local actors**: the good one (`https://iris.luit.ink/ap/v1/u/alice`, 16 posts) **and** a stale orphaned one (`http://localhost:8088/ap/v1/u/alice`, **0 Objects + 0 Edges**, `PreferredUsername=alice`). Clicking the stale card → `/actor?iri=http%3A%2F%2Flocalhost…` → the client proxies it → server **502** (can't reach `localhost:8088` from inside the container) → page shows **"Actor not found."** for an actor that *does* exist.

## Root cause

A record persisted under the dev `http://localhost:8088` base URL instead of the public `BaseUrl`. Confirmed in the store: exactly **1** actor row has a `localhost:8088` IRI (of 3,718; 21 use the public `iris.luit.ink` IRI; Objects has 0 localhost). The stale actor does **not** surface in Directory — it's specific to the Search path.

## Fix

1. Delete the orphan row (verified safe: 0 Objects + 0 Edges).
2. Guard so local actors are stored/served under the public `BaseUrl` (never `localhost`).
3. And/or: the search handler filters local actors whose IRI host ≠ the public origin.

## Re-verify

Search "alice" → exactly one alice card (the public-IRI one); clicking it renders the actor detail, no proxy 502.

**Re-verification evidence (Pass 27, 2026-09-20, andrew):** search "alice" → **26 results** including both `alice` cards — the good public-IRI one **and** the stale `http://localhost:8088/ap/v1/u/alice` orphan. STILL OPEN.

**Re-verification evidence (Pass 31, 2026-09-20, andrew):** search "alice" → **100 results**; the stale `http://localhost:8088/ap/v1/u/alice` orphan is the **first** result. Clicking it → `/actor?iri=http%3A%2F%2Flocalhost:8088%2Fap%2Fv1%2Fu%2Falice` → 2 console errors → page shows **"Actor not found."** STILL OPEN.

**Re-verification evidence (Pass 32, 2026-09-20, andrew, deployed `456b0d9`):** search "alice" → **20 results**; the stale `http://localhost:8088/ap/v1/u/alice` orphan is **gone** — only the canonical `https://iris.luit.ink/ap/v1/u/alice` actor card appears (first result). Clicking it → actor detail renders (banner, "Posts (17)" tab, Follow button) with **0 console errors** on initial load (one unrelated 504 on a remote `iris-dev1.luit.ink` note proxy, not S5-related). **FIXED.**
