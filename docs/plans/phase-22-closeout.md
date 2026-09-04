# Phase 22 closeout — deep-dive plan

> Referenced from [PLAN.md](../../PLAN.md)'s "Up Next". This is the detailed scope for the items that close out Phase 22 (the functional explorer rebuild). PLAN.md keeps only one-line pointers to the items below — update their status here, not there.

## 1. External-FQDN verification

The main remaining gap is validating the explorer and federation flow over the public reverse-proxy/FQDN route, which is not reachable in the current environment. This includes:
- resolver reachability and route correctness
- public instance navigation and cross-server behavior
- production-shape browser verification over the live hostnames

## 2. Live federation and interop checks

The remaining live checks are still the most important external validation items:
- follow scenarios against public Mastodon accounts
- post/receive flows and delivery visibility
- signature validation and content-type conformance
- pagination and collection compatibility
- community interaction and membership flows in real two-instance or public-federation setups

## 3. UI verification and final manual pass

The Phase 22 work closes with a final manual browser pass against the explorer, verifying:
- object/actor/community/instance screens render cleanly
- no dead-end raw JSON or broken navigation paths remain
- write flows produce correct outbox entries and signed payloads
- caches, refresh paths, and empty/error states behave consistently

## 4. Extension API surface audit and namespacing review

Before finalizing the public extension surface, perform a deep review of every custom endpoint and extension property that is surfaced through ActivityPub objects or actor/community documents. The goal is to ensure that anything not part of the core ActivityPub vocabulary is clearly identified as an Iris extension and is namespaced, documented, and reviewed for protocol purpose.

Scope of the review:
- audit all custom routes and endpoints returned as data from ActivityPub documents, not just the obvious local-control endpoints
- classify each property as either: core AP, ecosystem de facto convention, or Iris extension namespace
- treat accepted ecosystem conventions like `likes` and `shares` as the explicit exception: they are not AP-standard fields but are widely used across the ecosystem and should not be reworked into a custom Iris shape
- identify all remaining custom properties as `iris:` extension terms and evaluate their purpose, authorization model, lifecycle, and how they are used over ActivityPub
- consolidate or rename any extension terms that are too abstract, redundant, or ambiguous before they become locked in
- document expected request and response payloads for each extension, including the Add/Remove semantics when an extension mutates state or exposes an operation surface

This audit will feed a final pass on extension naming and transport boundaries, and will inform the server README guidance for implementers who need to work with our extension endpoints and payloads.

## 5. Future follow-on work

After the explorer rebuild, extension audit, and live interop pass are clean, the next phase shifts to broader federation maturity and live-account remediation, particularly:
- Mastodon compatibility issues
- edge-case moderation propagation
- additional community and follow-flow hardening
- continued C2S invariants verification with live external peers
