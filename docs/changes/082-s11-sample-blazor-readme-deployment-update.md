# 082 — Phase 8 S11: sample Blazor README + DEPLOYMENT.md update (docs finish)

> 2026-08-30 · Phase 8 (Sample) · Slice S11 (sample Blazor README + DEPLOYMENT.md update — the docs finish)

## What was built

The last Phase 8 slice: the **documentation** that finishes the sample's "boot + explore + interop" story.

- **`samples/SampleBlazorClient/README.md`** (new) — documents the Blazor WebAssembly **server explorer**
  (Deliverable B): what it is (a real browser client UI over the `Iris.Client` pipeline, not a mock), the
  quick start (Docker `iris-ui` on `8090` + a local no-Docker run against a local `SampleServer`), the
  **screens** (Home/logon, Instance, Actors, Actor detail, Object, Community, Compose) each mapped to the
  `IActivityPubClient` calls it makes, the **logon + base-URL/IRI-host rule** (SAMPLE_PLAN §4.4, [change
  074](074-base-url-vs-iri-host-config.md)), the **external-instance mechanism** (runtime-supplied WebFinger
  address + password + a browser-reachable base URL; the `InstanceBaseUrls` map pre-fills a known local
  host, and an unknown host uses the user-typed base as-is), how it is tested (in-process
  `SampleBlazorClient.Tests` + the Docker smoke path), and a **manual-exploration checklist** (the browser
  path the smoke test cannot click, SAMPLE_PLAN §6.2).
- **`docs/reference/DEPLOYMENT.md`** — the "Smoke test" section and the "Real follow/post federation" note
  were stale (pre-S10). Updated to reflect that the smoke test now asserts **signed cross-container
  federation** (a signed Follow delivered + validated on the remote, via the `FederatedActorDocumentFetcher`
  and the `tools/IrisSigner` helper) + the **proxy fallback**, not just WebFinger reachability; the
  deployment guarantee is now stated as "deployment + network + signed federation".

This is the slice the SAMPLE_PLAN §6.2 / §10 names as the docs finish: S9–S10 wired the **stack** + the
**smoke path**, and S11 finishes the **docs** so the full story is complete.

## The external-instance mechanism (documented, no real dev FQDN committed)

The README documents how the explorer is pointed at an instance that is **not** one of the two local Docker
instances: at logon the user supplies a **WebFinger address** (`user@example.com`), the actor's **password**,
and a **browser-reachable base URL**. Because the shipped sample registers the explorer with an **empty**
`InstanceBaseUrls` map (`Program.cs` → `AddIrisExplorer()`), the user-typed base URL is used as-is for an
unknown host; the WebFinger-resolved actor IRI (whose host is the *external* host) becomes the client's
`actorIriOverride` (what it authenticates as + signs as) while the transport dials the user-typed base URL —
the base-URL/IRI-host separation that makes an external instance work exactly like a local one. The read +
follow + **proxy-fallback** paths all run against the external instance through this one mechanism.

> **No real dev FQDN is committed.** Any external base URL / FQDN is operator-supplied at logon (runtime, in
> the browser) and is never written into the repo — the README documents the mechanism with placeholders only.

## What the README states honestly

- The **raw-JSON inspector** and **proxy-fallback** paths are pinned in-process by
  `tests/SampleBlazorClient.Tests` ([change 079](079-explorer-raw-inspector-and-proxy-fallback.md)) and are
  **not** separate Blazor pages in this sample (the proxy-fallback *behavior* is enabled on the client and is
  what makes the external-instance read + follow paths work). The README says so rather than overclaiming.
- The browser behaviors (log on, explore, write, moderate, switch instance, external instance) are the
  **manual-exploration checklist** the smoke test cannot drive (SAMPLE_PLAN §6.2 — the smoke test asserts at
  the HTTP/network boundary).

## Verification

- `samples/SampleBlazorClient/README.md` exists and documents the explorer + the external-instance mechanism
  (no real dev FQDN committed).
- `docs/reference/DEPLOYMENT.md` reflects the post-S10 smoke test (signed federation + proxy fallback) and
  the 3-service topology (unchanged from S9, now accurate for the smoke section).
- The local no-Docker run is verified: `dotnet run --project samples/SampleBlazorClient --no-build` starts the
  Blazor dev server (listening on `http://[::]:8080`) against a local `SampleServer` (`http://localhost:5000`),
  so the README's quick start is accurate.
- Full solution builds with **0 warnings**; **883 tests green**.

## Decisions

- **Document the mechanism, not a real FQDN.** The SAMPLE_PLAN §7 risk ("Dev FQDNs leak into the repo") is
  mitigated by documenting the external-instance mechanism with placeholders only; the base URL / FQDN is
  runtime, operator-supplied, and never committed.
- **Honest about what is a page vs. in-process.** The inspector + proxy-fallback are in-process-pinned
  (S8) and not Blazor pages; the README states this plainly so a reader is not misled into looking for a page
  that does not exist.
- **S11 is docs-only.** It adds no code and no tests; it finishes the phase by making the story discoverable
  (README) and accurate (DEPLOYMENT.md). The Phase 8 acceptance criteria (SAMPLE_PLAN §10) are all satisfied
  once S11 lands.
