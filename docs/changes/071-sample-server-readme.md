# 071 — Phase 8 S2: SampleServer README (Deliverable A)

> 2026-08-29 · Phase 8 (Sample) · Slice S2

## What was built

`samples/SampleServer/README.md` (Deliverable A of [SAMPLE_PLAN.md](../SAMPLE_PLAN.md)) — a pointer-style
README that **documents the implemented features** of the federation-ready sample server and where each
one lives in the library and in the spec/decision record. Docs-only slice; no production or test code
changed, and the suite stays green (build 0 warnings / 0 errors, 633 tests).

## Structure (per SAMPLE_PLAN §3.3)

- **What it is** — one paragraph: a runnable Iris ActivityPub server used as the sample's "home"
  instance; `CreateWebHostBuilder` is the single composition root (runnable `Program.Main` + the
  in-process `TestServer` suites).
- **Quick start** — local `dotnet run` (with `--Iris:…` CLI overrides) and the Docker stack
  (`docker compose up` → `iris-a` host:8081 / `iris-b` host:8082, `./scripts/docker-smoke-test.sh`);
  the logon credential table (handle : `iris-sample` for alice/bob/carla) and the local base URIs.
- **Implemented features** — a 15-row feature → endpoint(s) → library type(s) → pointer-doc table,
  covering the actor document, WebFinger, NodeInfo, paged collections, the followed feed, community
  doc/members/feed/search/collections/inbox, replies, global search, the signed inbox, the object
  document + tombstone, the proxy fallback, local moderation (mute/relay), and `iris:capabilities` —
  plus a short **Federation (inbound)** note on `UseSignatureValidation` and the local-vs-remote
  fetcher boundary.
- **Configuration** — the `Iris:` section keys (`HostName`, `Port`, `Https`, `Actor`) and what each
  sets (bind **and** advertised IRI).
- **Seeded data** — the actor/community/note inventory (alice/bob RSA local, carla Ed25519 remote-host
  stand-in, the `Group` community, follow edges, and the note/reply/like outbox content).
- **How it is tested** — pointers to `tests/SampleServer.Tests` (Phase 7 pipeline + the S1 federation
  suite) and `scripts/docker-smoke-test.sh`.

## Accuracy checks

Every endpoint in the feature table was verified against the real route registrations in
`ActivityPubServerExtensions.MapActivityPubEndpoints` (the `/ap/v1` group, the `{collection}` regex,
the `/c/{name}/…` family, the object-document `{**path}` catch-all, and the proxy/mute/relay
catch-alls), and every referenced library type name (`ActorDocumentHandler`, `WebFingerHandler`,
`NodeInfoHandler`, `CollectionEndpointHandler`, `FollowFeedHandler`/`IFollowFeedService`,
`Community*Handler`, `GlobalSearchHandler`/`GlobalSearchService`, `ObjectDocumentHandler`,
`ProxyHandler`, `LocalMuteHandler`/`LocalRelayHandler`, `IModerationStore`/`IRelayStore`) and every
pointer-doc path (decisions 010/028/031/036/037/048/053, changes 054/056/062/063/070, ARCHITECTURE,
DEPLOYMENT) was confirmed to exist.

## Decisions

- **Docs-only, single `docs:` commit.** S2 is documentation (Deliverable A); there is no implementation
  to pair with a test, so the README, the per-change doc, and the S2 checkbox/status updates in
  ROADMAP/SAMPLE_PLAN/PLAN land together in one `docs:` commit rather than being split.
