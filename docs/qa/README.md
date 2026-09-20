# QA Findings

One document per QA finding from the recurring **General UI/UX review** (Playwright-driven passes against `https://iris.luit.ink`). This folder is where new QA findings live — **not** in `PLAN.md`.

## Process

1. During a QA pass, every finding is written (or updated) as its own document in this folder using the template below.
2. `PLAN.md` carries only a one-line pointer to this section, plus the count of open findings and the top-priority one.
3. When a finding is fixed and re-verified from a clean entry, set its Status to `fixed (date, commit)` and record the re-verification evidence in the doc.
4. The pass-by-pass narrative lives in [passes.md](passes.md) (append-only; PLAN.md no longer carries pass narratives).

## Template

```markdown
# S<n> — <short title>

- **Class:** bug | UX | perf | data-integrity | feature-gap — **Severity:** S1 (blocker) | S2 | S3
- **Status:** open | fix committed (hash), not yet live | fixed (date, commit)
- **Found:** Pass <n> (<date>)
- **Related:** links to sibling findings / plans / change docs

## Symptom
What the user sees (page, control, states, console errors, counts).

## Root cause
Code-level cause with file:line references.

## Fix
The agreed fix approach.

## Re-verify
Clean-entry steps that prove the fix (no evidence, no `fixed`).
```

## Open findings

| ID | Title | Class | Sev | Status | Doc |
|---|---|---|---|---|---|
| S2 | Signed-out remote reads bypass the proxy (CORS/blank avatars) | bug | S2 | open (proxy 401s unsigned GETs) | [s02](s02-signed-out-proxy-bypass.md) |
| S3 | Object-detail 404s a local post's collections (Create-activity IRI) | bug | S3 | open | [s03](s03-object-detail-create-iri-404.md) |
| S4 | Communities "Following" tab drops followed REMOTE communities | UX / bug | S2 | open | [s04](s04-communities-following-remote.md) |
| S5 | Search lists a stale orphaned local actor (localhost IRI) | bug / data-integrity | S2 | fixed (2026-09-20, `456b0d9`) | [s05](s05-search-localhost-orphan-actor.md) |
| S6 | Join on a remote community is a silent no-op (CSP-blocked browser POST) | bug | S2 | fix committed (`68ae703`), not yet live | [s06](s06-remote-join-csp-blocked.md) |
| S7 | Directory external lookup stuck on the spinner forever | bug | S2 | fixed (2026-09-20, `456b0d9`) | [s07](s07-directory-external-lookup-stuck.md) |
| S8 | Communities "All on this instance" list is incomplete/inconsistent | bug / data | S2 | open | [s08](s08-communities-all-tab-incomplete.md) |
| S9 | Report/flag is a silent no-op (no feedback, duplicate flags) | UX / bug | S2 | fixed (2026-09-20, Pass 38) | [s09](s09-report-silent-noop.md) |
| S10 | Article "(long-form)" is mislabeled | UX / feature-gap | S2 | fixed (2026-09-20, Pass 38) | [s10](s10-article-longform-mislabeled.md) |
| S11 | Poll broken (silent no-op w/o body + invisible in "Your posts") | bug | S2 | fixed (2026-09-20, Pass 38) | [s11](s11-poll-silent-noop-and-outbox.md) |
| S12 | @mention linkify (case-sensitive dead link + autocomplete mismatch) | bug | S2 | fixed (2026-09-20, Pass 38) | [s12](s12-mention-case-and-autocomplete.md) |
| S13 | Remote Lemmy object-detail logs expected proxy 404s | UX / bug | S3 | fixed (2026-09-20, Pass 39) | [s13](s13-remote-lemmy-404-noise.md) |
| S14 | Signed-out remote actor-detail is CSP-blocked (S2 facet) | bug | S2 | open | [s14](s14-signed-out-actor-detail-csp.md) |
| S15 | Compose visibility hint is misleading for Followers/Direct | UX / cosmetic | S3 | fixed (2026-09-20, Pass 39) | [s15](s15-visibility-hint-misleading.md) |
| S16 | Poll votes are not persisted | bug / data-integrity | S3 | fixed (2026-09-20, Pass 37); UX badge gap remains | [s16](s16-poll-votes-not-persisted.md) |
| S17 | Profile tabs over-fetch the entire outbox (on load + every tab switch) | perf / request-spam | S2 | open | [s17](s17-profile-tabs-overfetch-outbox.md) |
| S18 | Following a local account: follow "succeeds" but follower's Home timeline stays empty | bug / data-integrity | S2 | partially fixed (Pass 39 — state persists, timeline populates; follow request not auto-approved) | [s18](s18-local-follow-timeline-empty.md) |
| S19 | Community "Requests" tab always fails to load (no request fires, no retry) | bug | S2 | open (Pass 39 — new facet: follow requests in notifications lack Accept/Decline UI) | [s19](s19-community-requests-tab-fails.md) |
| S20 | Home feed "Communities" tab is non-functional (no API call, same content as Posts) | bug / feature-gap | S2 | open (found Pass 42) | [s20](s20-home-feed-communities-tab-nonfunctional.md) |

**7 open, 1 fix-committed-not-live (S6), 8 fixed (S5, S7, S9, S10, S11, S12, S13, S15), 1 partially-fixed (S18), 1 core-fixed-UX-gap (S16). No S1/blockers.**

> Note: S1 was the Pass-10 defect set (signed-out 401 spam, proxy 500, Lemmy misclassification) — all fixed and verified Pass 11; it is recorded in [docs/changes/997-ui-ux-review.md](../changes/997-ui-ux-review.md), not here.

## Pass log

See [passes.md](passes.md) for the archived pass-by-pass narrative (Passes 10–25).
