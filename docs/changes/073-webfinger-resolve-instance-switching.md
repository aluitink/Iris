# 073 — Phase 8 S4: Logon by WebFinger resolve + instance switching (the headline feature)

> 2026-08-30 · Phase 8 (Sample) · Slice S4

## What was built

The Blazor explorer's **headline capability** ([SAMPLE_PLAN.md](../SAMPLE_PLAN.md) §4.2) is now real: a
user logs on to an ActivityPub instance by entering a **WebFinger address** (`@alice@iris-a`), and the
client *resolves* that address to the authoritative actor IRI via the instance's
`/.well-known/webfinger` before authenticating. One-click **instance switching** (log out + log on to a
remembered address) and a recent-instances list complete the flow. This is the S3 scaffold's "log on by
address" made authoritative: the address is no longer just parsed into a best-guess IRI — it is
*discovered* against the live instance.

## What changed

- **`WebFingerClient`** — the WebFinger dial is now **scheme-aware**: `ResolveActorAsync` gains a
  `dialScheme` parameter (default `https`, the RFC 8410 / public-web norm) so a local or self-signed
  instance serving its well-known document over plain `http` resolves. The client implements **both**
  `IWebFingerResolver` (the server's outbound 2-arg `ResolveActorAsync(account, ct)` path, unchanged)
  and `IDiscoveryService` (the 3-arg `ResolveActorAsync(account, dialScheme, ct)` path). The 2-arg
  overload delegates to the 3-arg with `dialScheme: "https"`, so existing callers are unaffected.
- **`IDiscoveryService` / `WebFingerDiscoveryService`** — the contract and its WebFinger-backed
  implementation expose the 3-arg `dialScheme` overload (default `https`).
- **`IrisClientBundle.ResolveActorAsync`** — the convenience surface is now 3-arg
  `(account, dialScheme = "https", ct)`, delegating to the discovery service.
- **`SampleBlazorClient.CreateClientService`** — gains an optional `actorIriOverride` parameter. When
  supplied, the authenticator logs in as that (authoritative) IRI rather than the
  `{serverBaseUri}/ap/v1/u/{handle}` IRI. This is what lets the session authenticate as the
  WebFinger-resolved IRI whose **host may differ from the dial base** for local instances (the browser
  dials a host-published port; the advertised IRI carries the service-name host).
- **`ExplorerSession`** — `LogOnAsync` now: (1) resolves the address via WebFinger over the same
  injected transport the session uses to reach the instance (the dial scheme is the address's scheme),
  catching `HttpRequestException` (unreachable / not-a-webfinger endpoint) and falling back to the
  direct actor IRI built from the address host; (2) builds the client with that resolved IRI as the
  `actorIriOverride`; (3) stores `ResolvedActorIri` (the authoritative advertised IRI a UI displays).
  `SwitchInstanceAsync(instance, password, ct)` logs out the current identity and logs on to a
  remembered recent instance by its address. `DisposeCurrent` clears the resolved IRI.
- **`Pages/Home.razor`** — shows the resolved actor IRI and a "Recent instances" card: select a
  remembered instance, enter the password, and switch (one-click instance switching, §4.2).
- **`wwwroot/css/app.css`** — `ul.recents` / `input.inline` styles for the switching card.
- **Tests** — `tests/SampleBlazorClient.Tests/ExplorerTests.cs` gains 4 in-process S4 facts (a
  `StartLabeledServer` helper hosts a `SampleServer` whose advertised host is a port-less label so the
  WebFinger dial host reaches it in-process): `LogOn_ResolvesAddressViaWebFinger_AndLogsIn` (WebFinger
  resolves `@alice@iris-a` → `http://iris-a/ap/v1/u/alice` and the session authenticates as that IRI),
  `LogOn_WebFingerUnavailable_FallsBackToDirectIri_AndLogsIn`, `SwitchInstance_LogsOutPrevious_AndLogsOnSelected`,
  and `WebFingerClient_DialSchemeHttp_ReachesLabeledInstance` (direct scheme-aware resolve). Existing
  WebFinger test call sites (the 2-arg → 3-arg ambiguity) and the `RecordingDiscovery` fake are updated
  to the new `dialScheme` signature.

## Decisions

- **Scheme-aware dial, default `https`.** A remote instance's well-known document is over `https`
  (RFC 8410 / ActivityPub norm); a local/self-signed sample instance serves it over `http`. Making the
  dial scheme a parameter (default `https`) keeps the public path correct while letting local instances
  resolve — without a global "force http" that would weaken the remote case.
- **Resolve, then authenticate as the resolved IRI.** The dial base URI (what the transport reaches)
  and the advertised IRI (the actor's identity) are deliberately separate (S3's `WebFingerAddress` /
  S5's base-URL-vs-IRI-host split). The session resolves the address to the authoritative IRI and passes
  it as `CreateClientService`'s `actorIriOverride`, so the Basic-auth login targets the correct actor
  even when the advertised host differs from the dial host.
- **Graceful fallback to the direct IRI.** WebFinger is a *pre-login* public GET; if the endpoint is
  unreachable (a non-WebFinger host, a network failure) the session falls back to the
  `{dialBaseUri}/ap/v1/u/{handle}` IRI rather than failing logon. This keeps a minimal instance
  (no well-known route) usable while a WebFinger-capable instance gets the authoritative resolve.
- **Two-instance switch exercised in-process.** `SwitchInstanceAsync` is "log out + log on by the
  remembered address", so the test logs on to one in-process instance, then switches to a second (and
  back), asserting the session holds exactly one active identity and remembers both (newest first,
  de-duplicated).

## Verification

- `dotnet build Iris.slnx` — 0 warnings / 0 errors; `samples/SampleBlazorClient` builds as a WASM app
  (default) **and** under `-p:ConsoleSmoke=true`.
- `dotnet test Iris.slnx` — all green: 861 total (`SampleBlazorClient.Tests` 21 → 25, 4 new S4 facts;
  `Iris.Client.Tests` / `Iris.Client.Extensions.Tests` unchanged counts, updated for the `dialScheme`
  signature).
