# Sample Explorer Enhancement — Second-Round Build Plan

> Status: planned · Part of the [Iris plan](../PLAN.md). Detailed plan for the **second round** of the
> Blazor WASM "server explorer" sample; the [Roadmap](ROADMAP.md) carries only the waypoint/checkbox and
> the root [PLAN.md](../PLAN.md) carries only the status row. Per the
> [doc-lean rules](reference/AUTONOMOUS_LOOP.md#keeping-the-docs-lean), heavy build notes for each slice
> land in [changes/](changes/README.md) as they complete.
>
> **Goal:** close the gap between the Iris client library's public API and what the sample explorer
> actually exercises, and fix the one broken end-to-end write path. The first round (Phase 8,
> [SAMPLE_PLAN.md](SAMPLE_PLAN.md), S1–S11) built the explorer shell, logon, and the primary read/write
> screens. This second round is driven by an **API-coverage audit** (library surface vs. what the UI
> calls) that found: a broken compose path, whole library features with no UI (relays, home timeline,
> paged collections), dead anchors everywhere (no navigation), and several rough edges. It is the
> standing "interop bug-hunting + feature-coverage" routine for the sample.

---

## 1. Context and non-goals

### Context

The Phase 8 enhancement (S1–S11) is **complete**: `docker compose up --build` boots `iris-a`/`iris-b`/
`iris-ui`; the UI logs on by WebFinger address, switches instances, and exercises logon, the actor
directory/detail, note view, compose, follow/unfollow, community, like, moderation (mute/block/flag),
instance switching, and OAuth2 logon. Verified end-to-end against the compose stack with Playwright.

But a **library-coverage audit** (every public `IActivityPubClient` method + every public `IriExtensions`
extension, compared against every `.razor` page) found that a meaningful slice of the library is
**unreachable from the UI**, and that the one screen that should prove the write path (Compose) is
**broken end-to-end**. This plan closes those gaps.

The audit's two deliverables:

1. **A gap list** (§3) — library features the sample does not exercise, with the exact method names.
2. **An enhancement list** (§4) — sample improvements, highest-value first.

### Non-goals (out of scope for this round)

- **New library features.** Every method in the gap list already exists and is unit/integration-tested.
  This round is **UI + sample** work (and the one client/server signature fix in S1). If a screen needs a
  genuinely new library method, that is a separate slice with its own change doc + a
  [ROADMAP](ROADMAP.md) note.
- **Live external-instance interop** (Mastodon/Lemmy/Threads). That is Phase 13/14 (blocked on Phase 9
  FQDN + real partners). This round targets the **two local instances** (`iris-a`/`iris-b`).
- **Real persistence / TLS / FQDNs.** Non-goals of the sample (see [SAMPLE_PLAN §1](SAMPLE_PLAN.md#1-purpose-and-non-goals)).
- **A new NuGet package.** Per [CODING_STYLE](reference/CODING_STYLE.md) no new package without a
  [ROADMAP](ROADMAP.md) note + justification.

---

## 2. The broken write path (the #1 priority)

**Compose does not work end-to-end.** When the user posts a note in the browser, the note is never
created:

- The **direct** path (the WASM client signs the `Create` with WebCrypto — `WebCryptoSigningKey` via the
  `Iris.WebCrypto` library) is **rejected 401** by the server's `SignatureValidationMiddleware`.
- The client's `ProxyFallbackHandler` then retries through the home instance's proxy, which **succeeds
  (200)** — but the proxy **always forwards as a `GET`** (by design, it is a read-only relay), so it
  returns the outbox collection rather than creating the note.
- The UI shows "StatusCode 200" but the outbox still holds only the original seeded note. The post is
  silently lost.

**Root cause (unresolved, to be diagnosed in S1):** a **signature-base mismatch** between the client's
[`SigningHandler.ToMetadata`](../src/Iris.Client/Pipeline/SigningHandler.cs) and the server's
[`HttpSignatureValidator.ToMetadata`](../src/Iris.Server/Security/HttpSignatureValidator.cs). Both build
an `HttpRequestMetadata` and pass it to
[`Signatures.BuildSignatureBase`](../src/Iris.Core/Signing/Signatures.cs); the signed bytes must be
identical or the server's `VerifyBase` fails. The likely divergent components are:

- **`content-type`** — the client signs `request.Content.Headers.ContentType.MediaType`
  (`application/activity+json`); the server reads `request.ContentType` (which may carry a charset, or be
  normalized differently).
- **`digest`** — the client computes `sha-512=base64(SHA512(body))` over the bytes it sends; the server
  re-computes over the bytes it receives. A body re-serialization (e.g. JSON-LD compaction re-encoding)
  between the two would break the digest.
- **`host`** — the client uses `uri.Authority`; the server uses `request.Headers.Host`.

The **BCL-signed** path (server-to-server, and the console `dotnet run` smoke) works — only the
**WebCrypto-signed** browser path 401s — so the divergence is in how the *browser* request is signed
vs. how the server reconstructs the base, not in the base algorithm itself.

> This is the highest-value item: it is the only screen that proves the write path, and it is currently
> misleading the user (a 200 that created nothing).

---

## 3. Library features the sample does not exercise (gap list)

Full inventory: `IActivityPubClient` has **33 public members** (32 methods + `Dispose`); `IriExtensions`
has **23 public extensions**. The sample exercises a subset. The gaps:

### 3.1 Client methods with no UI

| Method | What it does | Why it matters |
|---|---|---|
| `GetRelaysAsync(actorId)` | Enumerate an actor's relays (F-06, the `star` set) | **The entire relay feature is untestable from the UI.** |
| `SubscribeRelayAsync(actorId, relayId)` | Subscribe an actor to a relay (local Basic-auth) | No relay UI at all. |
| `UnsubscribeRelayAsync(actorId, relayId)` | Unsubscribe from a relay (local Basic-auth) | No relay UI at all. |
| `GetFollowFeedAsync(actorId, query?)` | The **home timeline** (union of followed actors' outboxes, newest-first, de-duplicated) | No way to see the followed timeline — a core user journey. |
| `GetActorAsync(actorId)` | Fetch an actor as a typed `Actor?` (null if not an `Actor`) | Actor docs always come through `GetObjectAsync`; the typed path is unused. |
| `GetCollectionAsync(collectionId, query?)` | **Paged** enumeration (yields `CollectionPage`, follows `next`) | Only the flattening `GetCollectionItemsAsync` is used, so **pagination / `next`-link walking is invisible** to the user. |
| `DeliverAsync(inboxId, activity)` | Raw signed activity to an inbox (ServerToServer profile) | All writes go through high-level helpers; the escape hatch is unused. |
| `SendAsync(request)` | Raw request through the signed pipeline | (Used by the raw-JSON inspector, S8 — keep.) |
| `MuteAsync`/`UnmuteAsync`/`SubscribeRelayAsync` **`ProxyCredentials` overloads** | Explicit-credential variants | Only the built-in local-moderation path is hit. |

### 3.2 `IriExtensions` unused by the UI

Only `OutboxOf()` is used (in `ActorDetail.razor`). Unused:

`InboxOf`, `FollowersOf`, `FollowingOf`, `LikedOf`, `BlocksOf`, `FlagsOf`, `MutesOf`, `RelaysOf`,
`FeedOf`, `RepliesOf`, `SearchOf`, `ToIri` (string/Uri), `ToLibraryId`, `ToLinkHref`,
`ResolveObjectIri`, `GetParentIri`, `GetMentionIris`, `GetAttachmentIris`, `ResolveCollectionIri`,
`BuildTombstone`, `ExtractEmbeddedObject`.

> These are **utility** helpers (no HTTP). They will be exercised *implicitly* once the deep-linking and
> feed screens are built (S2, S3) — e.g. `ResolveObjectIri`/`GetParentIri` to render threaded replies,
> `LikedOf`/`RelaysOf` on the actor-detail page. No dedicated "IriExtensions screen" is warranted.

### 3.3 Not a gap — confirmations

- **No unlike / undo-like** exists in the client API (only `LikeAsync`). The `Like` type is from
  `KristofferStrube.ActivityStreams`; Iris does not model an `Undo(Like)`. So the Object page's Like has
  no inverse — **this is a library surface decision, not a sample bug.** (If an unlike is wanted it is a
  *new* library slice, out of scope here.)
- **No delete / tombstone client method** — `BuildTombstone` exists in `IriExtensions` but there is no
  `DeleteAsync` on the client. Also a library surface decision, out of scope.

---

## 4. Sample enhancements (highest value first)

Each item is a slice; each lands a change doc. Ordered so the stack stays green and deployable throughout.

### S1 — Fix the Compose write path (the #1 priority) — **DONE (change [114](changes/114-s1-compose-write-path-fix.md))**

The diagnosis found the real defects were **not** a signature-base mismatch (the WebCrypto crypto is
byte-identical to the BCL — the in-process repro `WebCryptoComposeSigningTests` proves sign→verify
round-trips). They were: (1) WebFinger discovery dialed the wrong authority (port 443 vs. the explicit
`dialBaseUri` port); (2) a direct signed attempt to the *advertised* host always 401s (the browser's
signature can't be validated against the advertised host); (3) the proxy **dropped the write** (it
forwarded only as a bodyless `GET`). Fixed via a `dialBaseUri` discovery overload, an **AlwaysProxy**
mode (signed writes skip the guaranteed-401 direct attempt and go straight through the home proxy), and
making the `ProxyHandler` **relay the client's method + body** (reads still forward as `GET`). The
proxy already **re-signs** the forwarded request (change [081](changes/081-s10-signed-federation-proxy-smoke-test.md)), so
relaying a *signed write* (POST) with its body is consistent with that design — the "read-only relay"
assumption behind the original "do not forward the method" guidance applied to *unsigned reads* only.
Verified in the browser: post → `202`, note lands in the outbox.

1. **Diagnose the signature-base mismatch.** Add temporary, scoped logging (or an in-process test) that
   captures the exact signature base bytes on **both** sides for the same request:
   - Client: [`SigningHandler.ToMetadata`](../src/Iris.Client/Pipeline/SigningHandler.cs) → the
     `HttpRequestMetadata` → `Signatures.BuildSignatureBase`.
   - Server: [`HttpSignatureValidator.ToMetadata`](../src/Iris.Server/Security/HttpSignatureValidator.cs)
     → the same.
   Diff the two and identify the divergent component (`content-type` / `digest` / `host` / body).
2. **Prove it in a test first.** Write an in-process test that signs an `HttpRequestMetadata` the way
   `SigningHandler.ToMetadata` builds it and verifies it the way `HttpSignatureValidator.ToMetadata`
   builds it (a WebCrypto key for the sign side, or a BCL key to isolate the transport). A green test
   isolates the bug to the *actual HTTP transport* (header normalization, body re-serialization); a red
   test pins the exact base mismatch.
3. **Fix the mismatch** in the client or the server (whichever is wrong per the diff) so the signed base
   is byte-identical. Prefer fixing the **server's** `ToMetadata` to read exactly the headers the client
   signs (or vice-versa) — do **not** change the signature algorithm or the header set (that is
   conformance-tested).
4. **Re-verify compose in the browser** (Playwright): post a note → it appears in the outbox → it is the
   note just posted (not the seeded one). Confirm no 401 and no proxy fallback on the direct path.

> **Resolved (change [114](changes/114-s1-compose-write-path-fix.md)):** the proxy now relays the client's
> method + body for **signed writes** (POST/PUT via the `X-Iris-Proxy-Method` header); **reads still
> forward as `GET`**. This is consistent with the proxy **re-signing** the forwarded request (change
> [081](changes/081-s10-signed-federation-proxy-smoke-test.md)) — it re-signs whatever method it forwards, so a
> re-signed POST is valid. All proxy tests remain green (the 4 that guarded the GET-forward behavior still
> pass; the new `Proxy_Write_PostWithBody_IsRelayedAsPostToTarget` covers the write path).

### S2 — Deep-linking (dead anchors → navigation)

The single biggest usability win. `ObjectView.razor` renders every object/actor IRI as a dead
`<a href="#">`. Make IRIs navigable so search results, feeds, outboxes, and replies are clickable.

1. **`ObjectView.razor`:** render an object's IRI as a link to `/object?iri={iri}` and an actor's IRI
   (or a `Link` with an actor href) as a link to `/actor?handle={handle}` (or `/actor?iri={iri}`).
   Use `ResolveObjectIri` / `GetParentIri` to resolve the target.
2. **`ObjectPage.razor` / `ActorDetail.razor`:** read the `?iri=` / `?handle=` query param (via
   `[SupplyParameterFromQuery]`) and load on `OnInitializedAsync` so a click navigates + loads.
3. **Threaded replies:** render each reply's author as a link to the actor page and the reply's parent
   (`GetParentIri`) as a link to the parent object — this exercises `GetParentIri` + `ResolveObjectIri`.
4. **Mentions/attachments:** render `GetMentionIris` / `GetAttachmentIris` as links (exercising those
   helpers).

> In-process test: a `BlazorServerApp`/`TestServer` host (the S3–S8 pattern) that renders `ObjectView`
> with an actor + object and asserts the emitted `<a href>` values point at the right routes.

### S3 — Home timeline (followed feed)

Add a **Feed** page using `GetFollowFeedAsync` — the union of the logged-on actor's followed actors'
outboxes, newest-first, de-duplicated. Currently there is no way to see the followed timeline.

1. **New `Pages/Feed.razor`** (`@page "/feed"`): `GetFollowFeedAsync(Session.ResolvedActorIri)` → render
   each item via `<ObjectView>` (now deep-linked per S2).
2. **Nav link** in `MainLayout.razor`.
3. **Pagination (S3 also surfaces `GetCollectionAsync`):** the follow feed is a paged collection. Add a
   "Load more" that continues the `IAsyncEnumerable` (or pages via `GetCollectionAsync` with a
   `CollectionQuery.Limit`) so the user sees `next`-link walking.

> In-process test: seed a follow edge + outbox content on two actors, log on as the follower, assert the
> feed yields the followed actor's outbox items (newest-first, de-duplicated).

### S4 — Relay page (F-06) — **DONE (change [117](changes/117-s4-relay-page.md))**

The **entire relay feature** (subscribe/unsubscribe/list) had no UI. Added a **Relays** section on the
actor-detail page (and threaded `BypassCache` → `?refresh=true` through the client GET so a post-write
re-read observes the updated page).

1. **`ActorDetail.razor`** (or a new `Pages/Relays.razor`): a relays card showing `GetRelaysAsync(actor)`
   (the actor's current relays), a relay-IRI input, and **Subscribe** (`SubscribeRelayAsync`) /
   **Unsubscribe** (`UnsubscribeRelayAsync`) buttons — mirroring the existing moderation card.
2. The local Basic-auth path (no `ProxyCredentials` overload) is the default; the explicit-credential
   overloads are an option if the UI exposes a "use these credentials" toggle (optional).

> In-process test: subscribe an actor to a relay → assert the relay is in the actor's relays collection;
> unsubscribe → assert it is gone.

### S5 — Home page: show the community feed, not just the count — **DONE (change [118](changes/118-s5-home-community-feed.md))**

`Home.razor` called `GetCommunityFeedAsync` but discarded the items, showing only `FeedCount`. Now the
community card renders the actual recent items (via the deep-linked `<ObjectView>`, per S2; capped by
`CollectionQuery.Limit` so the landing page stays light).

### S6 — Actor detail: show the logged-on actor's own moderation — **DONE (change [119](changes/119-s6-my-moderation.md))**

`ActorDetail.razor` showed the **target** actor's mutes/blocks/flags collections (via
`GetMutesAsync`/`GetBlocksAsync`/`GetFlagsAsync`) while the write buttons act **as the logged-on actor**.
Add the logged-on actor's own moderation state (their `MutesOf`/`BlocksOf`/`FlagsOf` collections) so the
user sees what *they* have muted/blocked/flagged, and the buttons' effect is visible.

### S7 — Compose options (audience + visibility) — **DONE (change [120](changes/120-s7-compose-audience.md))**

`PostNoteAsync`'s optional `to` (audience) parameter was never populated. Now exposed: an optional **audience**
input (comma-separated actor IRIs, or `Public`) and pass it through. (Media/attachment upload is a
larger lift — note it as a follow-up, not this round.)

### S8 — Cleanup dead code + wire the base-URL config

1. **`Home.razor` OAuth2 state:** `PendingOAuthState` / `PendingOAuthHandle` / `PendingOAuthDialBase`
   statics are effectively non-operational (the state CSRF check is always null on the real first
   callback pass because the full-page redirect wipes statics). Either make the state check work (persist
   the state to a URL-fragment / `localStorage` via JS interop) or remove the dead fields and document
   the limitation.
2. **`InstanceBaseUrls`** (`IrisClientOptions` / `AddIrisExplorer`) is never populated in the shipped
   app — wire a default (the two local instances' advertised host → FQDN base URL) so the dial-base
   pre-fill actually works, or remove the surface.

---

## 5. Work breakdown (slices, each vertically complete: impl + tests)

Each slice is one autonomous-loop turn (see [AUTONOMOUS_LOOP.md](reference/AUTONOMOUS_LOOP.md)); each
lands a change doc in [changes/](changes/README.md). Ordered so the stack stays green throughout.

- [x] **S1 — Fix the Compose write path** (the #1 priority). **DONE (change [114](changes/114-s1-compose-write-path-fix.md)).**
  The real defects were WebFinger authority + the proxy dropping the write (not a signature-base mismatch).
  Fixed via a `dialBaseUri` discovery overload + AlwaysProxy (signed writes skip the 401 direct attempt) +
  the proxy relaying the client's method + body (reads still `GET`). Browser-verified: post → `202`, note
  lands in the outbox.
- [x] **S2 — Deep-linking.** **DONE (change [115](changes/115-s2-deep-linking.md)).** `ObjectView` IRIs →
  `/object?iri=` + `/actor?iri=` links (author, parent, mentions, attachments); object/actor pages read the
  query param and auto-load. In-process bUnit tests assert the emitted `<a href>` values; browser-verified
  (click an actor/note → the right page loads).
- [x] **S3 — Home timeline (followed feed) + pagination.** **DONE (change [116](changes/116-s3-home-timeline-followed-feed.md)).**
  New `Feed` page enumerates `{actor}/feed` via paged `GetCollectionAsync`; "Load more" walks the page's
  `NextPage` IRI (surfaces `next`-link walking). In-process test: a follower sees the followed actor's outbox
  items, and the paged collection carries a `next` link (`page=2` of 3).
- [x] **S4 — Relay page (F-06).** **DONE (change [117](changes/117-s4-relay-page.md)).** `ActorDetail`
  Relays card: `GetRelaysAsync` list + `SubscribeRelayAsync` / `UnsubscribeRelayAsync` (local Basic-auth).
  Fixed the client to thread `BypassCache` into the GET as `?refresh=true` (the server re-renders the cached
  collection page only on `?refresh=true`, so a post-write re-read observed a stale page). In-process tests
  on subscribe/unsubscribe.
- [x] **S5 — Home page shows the community feed items** (not just the count). **DONE (change
  [118](changes/118-s5-home-community-feed.md)).** `Home`'s community card now renders the recent items via
  deep-linked `<ObjectView>` (capped by `CollectionQuery.Limit`), not just `FeedCount`.
- [x] **S6 — Actor detail shows the logged-on actor's own moderation** (`MutesOf`/`BlocksOf`/`FlagsOf`).
  **DONE (change [119](changes/119-s6-my-moderation.md)).** Actor detail now shows the logged-on actor's own
  mutes/blocks/flags counts ("My moderation") alongside the target's, and refreshes them (bypass-cache) after
  a mute/block/flag write.
- [x] **S7 — Compose audience** (`PostNoteAsync`'s `to` parameter). **DONE (change [120](changes/120-s7-compose-audience.md)).**
  Compose exposes an audience input (Public or comma-separated actor IRIs) and passes it through to
  `PostNoteAsync` / `PostReplyAsync`'s `to`.
- [ ] **S8 — Cleanup** dead OAuth2-state statics + wire the `InstanceBaseUrls` default.

> S1 is the gate: it must land first (the broken write path undermines every write screen). S2–S4 are the
> feature-coverage items (relays, home timeline, navigation). S5–S8 are polish + cleanup. Each slice
> leaves the full solution green (`dotnet build` 0 warnings + `dotnet test`).

---

## 6. Acceptance criteria (definition of done for this round)

- [ ] **Compose works end-to-end in the browser:** posting a note creates it (it appears in the outbox;
      it is the note just posted). No 401 on the direct WebCrypto-signed path; no reliance on the proxy
      fallback for writes.
- [ ] **Navigation works:** clicking an object/actor IRI anywhere (search results, feeds, outbox,
      replies) navigates to the right page and loads it.
- [ ] **Home timeline:** the followed feed is viewable + paginated.
- [ ] **Relays (F-06):** subscribe/unsubscribe/list relays from the UI.
- [ ] **Home page** shows recent community items (not just a count).
- [ ] **Actor detail** shows the logged-on actor's own moderation state.
- [ ] **Compose** accepts an optional audience (`to`).
- [ ] **No dead OAuth2-state statics** (or the state check is made to work).
- [ ] `InstanceBaseUrls` is either wired with a default or removed.
- [ ] Full solution `dotnet build` (0 warnings, `TreatWarningsAsErrors`) + `dotnet test` green; the
      Playwright manual-exploration checklist (log on → feed → navigate → post → relay) passes against
      the live stack.

---

## 7. Pointers

- Library client surface: [PROJECTS.md](reference/PROJECTS.md) (`Iris.Client`, `Iris.Client.Extensions`).
- Library server surface + endpoints: [PROJECTS.md](reference/PROJECTS.md) (`Iris.Server`),
  [ARCHITECTURE.md](reference/ARCHITECTURE.md).
- Signing (the S1 fix): [ARCHITECTURE.md](reference/ARCHITECTURE.md) (HTTP signatures),
  [Signatures.cs](../src/Iris.Core/Signing/Signatures.cs),
  [SigningHandler.cs](../src/Iris.Client/Pipeline/SigningHandler.cs),
  [HttpSignatureValidator.cs](../src/Iris.Server/Security/HttpSignatureValidator.cs).
- First-round sample plan (S1–S11): [SAMPLE_PLAN.md](SAMPLE_PLAN.md).
- Coding rules (binding): [CODING_STYLE.md](reference/CODING_STYLE.md).
- Doc-lean rules: [AUTONOMOUS_LOOP.md](reference/AUTONOMOUS_LOOP.md#keeping-the-docs-lean).
