# S5 — Search lists a stale orphaned local actor (localhost IRI)

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** open
- **Found:** Pass 13 (2026-09-20) — re-confirmed Pass 15

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
