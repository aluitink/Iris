# S41 — Authless home-feed boost cards render "Content unavailable" (bare-link Announce target never resolved when signed out)

- **Class:** UX / bug (authless landing feed degraded) — **Severity:** S3 (annoying — every cross-actor boost on the public landing page renders as a dead link instead of a content preview)
- **Status:** open
- **Found:** Pass 275 (2026-09-22), `qa-iris-a.luit.ink`, build `2686795a`
- **Related:** [s02](s02-signed-out-proxy-bypass.md) (same signed-out proxy seam, actor facet), [s14](s14-signed-out-actor-detail-csp.md)
- **Surface:** signed-out `GET /` (the anonymous public feed, `PagedCollection` → `ObjectView` per item)

## Symptom

On the **signed-out** root page (the public feed), every feed item that is an **Announce**
(Boost) whose target is a bare IRI — the common shape, the feed's Announce carries only a link,
no embedded object — renders:

> **Content unavailable — view original post**

…a dead link to `/object?iri=…` (which itself redirects to `/login` for a signed-out visitor),
instead of a content preview (author, text, media). Observed on `GET /` with `ii-b1`@B's boosts
of `ii-a1`@A's notes (`…/ii-a1/notes/06GCGV9ZFKC24Q60Z9AQCJTBA0` and
`…/ii-a1/notes/06GCG0FRQGDTVK2TZQGH4P821G`), and on the signed-out remote-actor page
(`/actor?iri=…/qa-iris-b.luit.ink/ap/v1/u/ii-b1`) whose feed shows the same Announces — 7
"Content unavailable" cards. The signed-in views of the same items render the full preview.

**Not** an auth gate, **not** a data problem, **not** a console error:

- 0 console errors, 0 failed requests — the target fetch is **silently skipped**, never attempted.
- The target note serves **200 anonymously** on the wire (`GET …/ii-a1/notes/06GCGV…` = 200, full
  content), and the same-origin anonymous proxy relays it: `GET /ap/v1/proxy/{note}` = **200** signed-out.
- Signed-in, the identical Announce cards render the resolved content (121.7 fetch runs and succeeds).

## Repro (signed-out, clean entry)

1. Open `https://qa-iris-a.luit.ink/` signed out (no cookies).
2. The public feed shows `ii-b1`'s boosts of `ii-a1`'s notes.
3. Every one of those boost cards renders **"Content unavailable — view original post"** with a
   Like/Reply action bar and no preview text.
4. Same result on the signed-out remote-actor page `/actor?iri=…/qa-iris-b.luit.ink/ap/v1/u/ii-b1`.
5. Sign in as `ii-a1` → the same cards now show the full note preview.

## Expected

A signed-out visitor on the public feed sees the **same content preview** for a boost as a signed-in
visitor (the boosted note's author, text, media) — the note is public and anonymously readable.

## Root cause (confirmed from code + wire)

`apps/Iris.Web.Client/Components/ObjectView.razor.cs:1331-1347` (the 121.7 Announce-target
resolution) calls:

```csharp
var announced = await Ui.GetContentObjectAsync(announceIri);
if (announced is { } announcedObj) { _announcedObject = announcedObj; }
```

`UiContext.GetContentObjectAsync` (`apps/Iris.Web.Client/Ui/UiContext.cs:395`) bails out early
when the visitor is signed out:

```csharp
if (_session.Client is null)
{
    return null;          // <- signed out: never even attempts a fetch
}
```

(`_session.Client` is null exactly when signed out — `IActorSessionAccessor.Client`.) So the
boost-target fetch is **never issued** and the card falls to the "Content unavailable" branch
(`ObjectView.razor:278,306`).

The infrastructure to fix this **already exists and is proven** for the actor facet (S2/S14):
`FetchViaAnonymousProxyAsync` (`UiContext.cs:625`) performs a cookie-less same-origin
`GET /ap/v1/proxy/{target}` (the server relays a public read, cache-first, and archives the result
— server tests `ProxyFallbackIntegrationTests.Proxy_AnonymousGetOfRemoteNote_RelaysAndArchivesNote`
+ `…OfRemoteActor_…` cover exactly this seam), and `FetchActorAsync` (`UiContext.cs:500`) already
uses it for signed-out **remote actor** reads:

```csharp
doc = _session.Client is not null
    ? await FetchViaProxyAsync(actorIri)
    : await FetchViaAnonymousProxyAsync(actorIri);
```

`GetContentObjectAsync` simply never wires the same seam into the signed-out **object** read path.

Live wire evidence (signed-out, Pass 275/276):

| Request | Result |
|---|---|
| `GET /ap/v1/u/ii-a1/notes/06GCGV9ZFKC24Q60Z9AQCJTBA0` (direct) | 200, full content |
| `GET /ap/v1/proxy/https%3A%2F%2Fqa-iris-a.luit.ink%2Fap%2Fv1%2Fu%2Fii-a1%2Fnotes%2F06GCGV…` | 200 |
| `GET /ap/v1/proxy/https%3A%2F%2Fqa-iris-b.luit.ink%2Fap%2Fv1%2Fu%2Fii-b1` (remote actor, same seam) | 200 |

## Suggested fix

In `GetContentObjectAsync` (`apps/Iris.Web.Client/Ui/UiContext.cs:395`), when `_session.Client is
null` (signed out), do **not** return null outright — mirror the `FetchActorAsync` signed-out seam:

- **remote** object IRI (`IsRemoteObjectIri(objectIri)`): `await FetchViaAnonymousProxyAsync(objectIri)`
  (the proven anonymous proxy GET; the server relays + archives, so later signed-in reads are cache hits);
- **local** object IRI: a plain unsigned `GET` of the note's own IRI (same-origin, public — the
  same shape as `FetchActorDocumentAnonymousAsync` for local actors);
- non-2xx / failure → fall back to returning null (the card's existing "Content unavailable"
  behavior is the correct degradation).

Keep the existing per-circuit cache (`_contentObjects`) + in-flight coalescing (`_contentInFlight`)
around the fetch so N cards pointing at the same target still collapse into one request (147.1).

A minimal variant would route **all** signed-out reads through `FetchViaAnonymousProxyAsync`
(same-origin, so it also works for a local IRI — the proxy relays the GET to its own store) — but
the direct local GET avoids the proxy hop for the common local case.

## Re-verify steps (after the fix lands on a fresh QA build)

1. Clean entry (fresh browser context, no cookies) on `https://qa-iris-a.luit.ink/`.
2. The public feed's `ii-b1`-by-`ii-a1` boost cards render the **full note preview** (author, text,
   "QA pass273 S28 repro…"), not "Content unavailable — view original post".
3. Signed-out `/actor?iri=…/qa-iris-b.luit.ink/ap/v1/u/ii-b1` — the feed's boost cards also render
   the preview (remote-object target, anonymous proxy seam).
4. 0 console errors, 0 failed requests; the network panel shows the target fetch (direct local GET
   or `/ap/v1/proxy/{note}`) returning 200.
5. Sign in — the same cards still render correctly (no regression to the signed-in 121.7 path).
6. A boost of a **deleted/404** note still degrades to "Content unavailable — view original post"
   (the fallback is intact).
