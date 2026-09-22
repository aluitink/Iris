# S41 — Authless home-feed boost cards render "Content unavailable" (bare-link Announce target never resolved when signed out)

- **Class:** UX / bug (authless landing feed degraded) — **Severity:** S3 (annoying — every cross-actor boost on the public landing page renders as a dead link instead of a content preview)
- **Status:** open
- **Found:** Pass 275 (2026-09-22), `qa-iris-a.luit.ink`, build `2686795a`
- **Related:** [s02](s02-signed-out-proxy-bypass.md) (same signed-out proxy seam, actor facet), [s14](s14-signed-out-actor-detail-csp.md)
- **Surface:** signed-out `GET /` (the anonymous public feed, `PagedCollection` → `ObjectView` per item)

## Symptom

On the **signed-out** root page (`https://qa-iris-a.luit.ink/`), every **Announce** (boost) item whose
target is a **bare IRI link** renders the fallback:

> **Content unavailable — view original post**

instead of the boosted note's author + text preview. The affected items are real, resolvable public
notes — e.g. `ii-b1`@qa-iris-b boosting `ii-a1`'s note
`https://qa-iris-a.luit.ink/ap/v1/u/ii-a1/notes/06GCGV9ZFKC24Q60Z9AQCJTBA0`, which:

- serves **200** with full content at `GET /ap/v1/u/ii-a1/notes/06GCGV9ZFKC24Q60Z9AQCJTBA0` (anonymous),
- appears in the anonymous `GET /ap/v1/public/feed` wire payload as an `Announce` whose
  `object` is the **bare string IRI** (no embedded object),
- opens correctly on `/object?iri=…` **when signed in** (0 console errors, full content + tabs).

**0 console errors, 0 failed network requests** — the card silently renders the fallback.

Signed-in, the same Announce cards resolve their bare-link targets fine (the 121.7 fetch path works
there), so this is specific to the **signed-out** circuit.

## Root cause (static trace)

1. `PagedCollection.razor:74` renders each public-feed item via `<ObjectView Item="item" />`.
2. `ObjectView.razor.cs:1331-1347` (the 121.7 bare-link Announce resolution): when the Announce's
   target is a bare link, it calls `Ui.GetContentObjectAsync(announceIri)` to fetch the target and
   render a content preview.
3. `UiContext.GetContentObjectAsync` (`Ui/UiContext.cs:403-406`):

   ```csharp
   if (_session.Client is null)
   {
       return null;
   }
   ```

   and `IActorSessionAccessor.Client` (`Accounts/IActorSessionAccessor.cs:459-462`) is **null when
   signed out** (the signing client only exists for a signed-in actor).
4. So the target fetch is **silently skipped** when signed out → `BoostedObject` stays null →
   `ObjectView.razor:277-279` renders the "Content unavailable — view original post" fallback.

The fix pattern already exists in the same file for **actors**: `FetchActorAsync`
(`Ui/UiContext.cs:494-507`) routes the signed-out read through the **same-origin anonymous proxy seam**
(`GET /ap/v1/proxy/{target}`, S2/S14 — `FetchViaAnonymousProxyAsync`, `:625-641`), which relays an
unsigned public GET (cache-first). The **object** read path (`GetContentObjectAsync`, `:395-453`)
never got the equivalent: when `_session.Client is null` it returns null instead of consulting the
anonymous proxy. Note it already *does* try `FetchViaProxyAsync` (the signed POST proxy) for remote
IRIs (`:415-429`) — that also returns null signed out because it's only reached after the
`_session.Client is null` early-return is bypassed… (it is not bypassed; the early return at
`:403-406` precedes it), so signed-out visitors get **no** proxy read for objects at all.

## Why it matters

The authless root page is the instance's public landing (SEO + first impression). Its feed is
supposed to preview boosted posts (the `IsContentItem` filter in `Home.razor:83-112` deliberately
keeps `Announce` items in the feed). Instead, every boost by a non-author whose target arrives as a
bare link (the common outbox shape) is a dead link, with no content and no author.

## Suggested fix

In `UiContext.GetContentObjectAsync` (`apps/Iris.Web.Client/Ui/UiContext.cs:395`), when
`_session.Client is null` (signed out), fall through to the **anonymous same-origin proxy seam**
(`FetchViaAnonymousProxyAsync`, `GET /ap/v1/proxy/{target}`) — the same pattern
`FetchActorAsync` uses — before giving up. The proxy is cache-first and serves public objects to
unsigned reads, so the boost card then resolves the target and renders the preview. (A direct
same-origin `GET` on a **local** object IRI would also work as a last resort, mirroring
`FetchActorDocumentAnonymousAsync`.)

## Repro

1. Signed out, open `https://qa-iris-a.luit.ink/`.
2. Scroll to any "Boosted by ii-b1" card (e.g. the 3h/5h-ago cards targeting
   `…/ii-a1/notes/06GCGV9ZFKC24Q60Z9AQCJTBA0` / `…/06GCG0FRQGDTVK2TZQGH4P821G`).
3. Card body is "Content unavailable — view original post" (no author, no text), 0 console errors.
4. Sign in as `ii-a1` → same Announce cards (in `/home`/object detail) render full content.
