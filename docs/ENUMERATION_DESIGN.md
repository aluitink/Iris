# Real-User Enumeration Design (Phase 9)

> Phase 9 is **ideation + preparation only** — no live reconnaissance runs here. This document is the
> **real-user enumeration design** (ROADMAP bullet 3): the read-only plan for discovering and
> enumerating real users/communities on *other* instances (Mastodon, Lemmy, Pleroma, Threads, …). It
> is the reconnaissance we will run in Phase 13 once the operator-provided FQDN is live, and it feeds
> the compatibility matrix (the interop scenarios to verify against the discovered targets).
>
> It is grounded in the **real** Iris client surface — what `IActivityPubClient` and `IWebFingerResolver`
> can already do today — so the plan states precisely which steps are built-in and which require a
> thin primitive (via `SendAsync`/`GetObjectAsync`). Companion docs: [DEPLOYMENT_PREP.md](DEPLOYMENT_PREP.md)
> (FQDN/TLS + bootstrap) and [DEPLOYMENT.md](DEPLOYMENT.md) (the Phase 8 reference topology).

## 1. What "enumeration" means here

Read-only discovery of **who and what exists** on target instances, to (a) pick realistic Phase 13
interop targets (real actors/communities to follow and post to) and (b) inventory each ecosystem's
discovery surface (which of the mechanisms below a given platform actually supports). It is **not**
registration, not messaging, and not any write — every operation is a GET (or a WebFinger query),
signed by our instance actor where the platform requires it.

## 2. The real client surface (what Iris can do today)

The enumeration plan builds on these existing, tested capabilities — no new library code is required
for the core reconnaissance; the gaps (§4) are covered by the generic `SendAsync`/`GetObjectAsync`
primitives.

| Capability | Real API | Returns | Notes |
|---|---|---|---|
| **Handle → actor IRI** (WebFinger) | `IWebFingerResolver.ResolveActorAsync(string account, ct)` (`src/Iris.Client/IWebFingerResolver.cs`) | `Iri?` (the actor document IRI, or null) | Accepts `@user@host`, `user@host`, or `acct:…`. GETs `https://{host}/.well-known/webfinger?resource=acct:…` and returns the first `rel="self"` activity link's `href`. **Read-only, standard (RFC 8410)** — works against any conformant platform. |
| **Fetch an actor's document** | `IActivityPubClient.GetActorAsync(Iri actorId, ct)` | `Actor?` | The actor's `publicKey`, `inbox`, `outbox`, `followers`, `following`, `preferredUsername`, type (`Person`/`Group`/…). A `Group` (community) document is the same shape. |
| **Fetch any object by IRI** | `IActivityPubClient.GetObjectAsync(Iri objectId, ct)` | `IObject?` | Generic; use for community documents, notes, or any IRIs the other mechanisms discover. |
| **Enumerate a paged collection** | `IActivityPubClient.GetCollectionItemsAsync(Iri collectionId, CollectionQuery?, ct)` | `IAsyncEnumerable<IObjectOrLink>` | The client does the full page iteration (follows `next` until exhausted or `CollectionQuery.Limit`). Works for **outbox / followers / following** via the derived IRIs below. |
| **IRI derivation for collections** | `IriExtensions.OutboxOf / FollowersOf / FollowingOf / InboxOf / FeedOf` (`src/Iris.Core/IriExtensions.cs`) | `Iri` | `{actor}/{segment}` appenders. So a discovered actor's followers = `GetCollectionItemsAsync(actorIri.FollowersOf(), …)`. |
| **Generic signed/raw request** | `IActivityPubClient.SendAsync(HttpRequestMessage, ct)` | `HttpResponseMessage` | The escape hatch for any endpoint with no dedicated method — used for NodeInfo, directory, and search (§4). |
| **Raw signed object fetch** | (compose `SendAsync` + `ActivityJson.Deserialize<IObjectOrLink>`) | — | For non-actor JSON-LD documents the dedicated methods don't cover. |

**Built-in pipeline** (no composition code needed): handle → `ResolveActorAsync` → actor IRI →
`GetActorAsync(actorIri)` → `Actor`. The two steps are composed by the caller (there is no single
"resolve and fetch" method).

## 3. The reconnaissance plan (read-only, per target instance)

Run against each target instance (host) in the compatibility matrix. All steps are GETs; sign as the
instance actor (`InstanceActorId`) only where the platform requires an authenticated request (some
directory/search endpoints are public; some are not).

### 3.1 Instance discovery (per host)

1. **NodeInfo** — `GET https://{host}/.well-known/nodeinfo` (discovery link) → `GET https://{host}/nodeinfo/2.0` (or the `href` in the link). Yields software name/version, protocols, and `usage.users.total` (a population signal). **Mechanism: NodeInfo spec.** Use `SendAsync` (Iris has no NodeInfo client method — it only *serves* NodeInfo). Record the platform (Mastodon/Lemmy/Pleroma/…) + version — this is what the compatibility matrix keys on.
2. **Well-known probes** — confirm `/.well-known/webfinger` is reachable (the entry point for every per-account lookup). A 200 with a JRD body means WebFinger-based enumeration is available.

### 3.2 Seed accounts (per host)

We cannot enumerate an entire instance's users from a public endpoint on most platforms (no global
user directory in the ActivityPub spec), so we start from **seed accounts** — known handles gathered
out-of-band (e.g. a platform's public web UI, a directory, or a prior run's results). Each seed is a
`handle@host` that step 3.3 resolves.

### 3.3 Resolve a seed → actor document

1. `Iri? actorIri = await webFinger.ResolveActorAsync("handle@host", ct);` — if null, the handle does
   not exist (record as a miss; move on).
2. `Actor? actor = await client.GetActorAsync(actorIri, ct);` — read `actor.Type`
   (`Person` → user, `Group` → community), `preferredUsername`, `inbox`, `outbox`, `followers`,
   `following`. A `Group` is a candidate community target; a `Person` is a candidate user target.

### 3.4 Expand via graph traversal (the actual enumeration)

From each resolved actor, traverse the social graph **read-only** to discover more real users and
communities:

1. **Followers / following** — `await foreach (var item in client.GetCollectionItemsAsync(actorIri.FollowersOf(), new CollectionQuery(Limit: N), ct))`. Each item is a link to another actor; resolve the interesting ones (step 3.3) to classify them and continue the BFS. This is the primary enumeration mechanism: a handful of well-connected seed accounts (high-follower accounts) reach a large fraction of a platform's active users in a few hops.
2. **Outbox (recency signal)** — `client.GetCollectionItemsAsync(actorIri.OutboxOf(), …)` shows an actor's recent activity (a liveness/engagement signal for target selection). Read-only; bounded by `CollectionQuery.Limit`.
3. **Community members** — for a discovered `Group`, the platform's members collection (where exposed) enumerates its members directly (Mastodon: `/groups/{id}/members`-style or the `Group`'s `endpoints`; Iris's own `/c/{handle}/members` is the Iris analogue). Classify each member as a user/community target.

BFS with a per-hop `CollectionQuery.Limit` and a global budget (max accounts, max hops, max requests) keeps the reconnaissance bounded and polite.

### 3.5 Platform-specific discovery surfaces (where the spec is silent)

The ActivityPub spec standardizes only WebFinger + NodeInfo + object/collection fetch. Real user
*discovery* is platform-specific; these are the extra surfaces to probe (all via `SendAsync`, all
read-only), recording which the target supports for the compatibility matrix:

| Platform | Discovery surface | Mechanism (GET) | Notes |
|---|---|---|---|
| **Mastodon** | Directory | `/api/v1/directory?local=true&limit=N` | Public JSON (not JSON-LD); yields recent accounts (local/remote) — the best seed source. Not ActivityPub; parse as plain JSON. |
| **Mastodon** | Search | `/api/v2/search?q=…` | Account/hashtag/status search; public for logged-out with limits. |
| **Mastodon** | Tags | `/api/v1/tags`, `/api/v1/tag/{name}/statuses` | Hashtag graph as an alternate discovery axis. |
| **Lemmy** | Communities | `/api/v1/community` | Public JSON; the community list is the seed source (Lemmy is community-centric). |
| **Lemmy** | Users / posts | `/api/v1/user`, `/api/v1/post` | Public JSON; user + post enumeration. |
| **Lemmy** | Search | `/api/v3/search?q=…` | Cross-entity search. |
| **Pleroma** | Search | `/api/v1/microblog/search?q=…` | Public JSON; account/post search. |
| **Threads** | — | *(none public)* | Threads has **no public directory/search and a non-standard AP surface** (Meta's implementation). Enumeration is limited to WebFinger on known handles + graph traversal from seeds. **Flagged in the risk register** as the hardest target; likely deferred or partial in Phase 13. |

> These endpoints are **platform REST APIs**, not ActivityPub — they return plain JSON, not
> ActivityStreams. The reconnaissance parses them with `System.Text.Json` (not `ActivityJson`) and
> extracts only the `acct:`/IRI values needed to seed step 3.3. They exist to *find* accounts; all
> subsequent interop verification (Phase 13) uses the standard ActivityPub surface (WebFinger + actor
> document + collections) so the compatibility matrix tests real federation, not a platform's private
> API.

## 4. Gaps in the current client surface (design notes, not this phase's implementation)

The core reconnaissance (§3.3–3.4) needs **no new code** — it composes `IWebFingerResolver` +
`GetActorAsync` + `GetCollectionItemsAsync` + `IriExtensions.*Of`. The platform-specific probes (§3.5)
and NodeInfo fetch use `SendAsync`. If Phase 13 makes this a repeatable tool, the likely small additions
are:

- A **`NodeInfoClient`** (fetch + parse `/.well-known/nodeinfo` → `/nodeinfo/2.0`) in `Iris.Client` —
  Iris already *serves* NodeInfo; the client has no fetcher. Low effort, high reconnaissance value.
- A **`DirectoryClient`/platform-probe** abstraction over the §3.5 endpoints (each platform's seed
  source), returning `IReadOnlyCollection<Iri>` of discovered actor IRIs. Higher effort; per-platform
  parsers.
- A **`CollectionQuery` offset** (currently `Limit` + `BypassCache` only, forward-follow) if the
  reconnaissance needs to re-scan a collection from a stable position — not required for a BFS (each
  collection is read once).

These are **Phase 13 implementation notes**, not Phase 9 deliverables. Phase 9's job is to have this
design ready so Phase 13 is "fill in targets + add the two small clients," not "figure out how to
enumerate."

## 5. Safety, politeness, and scope guardrails

Enumeration hits **public third-party instances**. Guardrails the reconnaissance must enforce (and
Phase 13's harness must configure):

- **Read-only, always.** Only GET (and WebFinger queries). No POST, no follow, no delivery. The
  reconnaissance never writes to a target.
- **Bounded.** Per-run budgets: max requests, max accounts, max hops, per-host rate limit (e.g. ≥ 1 req/s,
  honor `Retry-After`/429). A runaway BFS must be impossible.
- **Gated + opt-in.** Runs only when explicitly invoked (an env flag + target config, per the Phase 9
  test-harness-extension bullet), never in a default/local test run. It is a standalone reconnaissance
  tool, not part of the normal `dotnet test` suite.
- **Respect robots/ToS.** Some platforms restrict automated access; the reconnaissance should be
  conservative (low volume, public endpoints only) and stop cleanly on 403/429.
- **No PII beyond what the platform publishes publicly.** The enumeration records public actor IRIs,
  types, and counts — not private data.

## 6. Output → Phase 13

The reconnaissance's output is a **target inventory** per platform: a list of resolved actor IRIs
(users) + community IRIs, each tagged with platform/version (from NodeInfo) and the discovery path that
found them. Phase 13 consumes this inventory to pick concrete follow/post targets for each compatibility
matrix row (Mastodon: follow a real account, post, confirm receipt; Lemmy: follow a real community;
etc.) and to record which discovery surfaces each platform actually exposed (feeding the matrix's
"discovery" column). The inventory is the bridge between "we know how to enumerate" (this doc) and
"we verified federation against real users" (Phase 13).

## 7. What this phase does NOT do

- **No live reconnaissance.** No third-party instance is contacted; no account is resolved. Phase 13
  runs this plan against real hosts, gated by the FQDN + env flag.
- **No new client code.** The §4 gaps are design notes for Phase 13; this slice is the design only.
  `dotnet build` / `dotnet test` are unchanged (444/444).
- **The remaining Phase 9 bullets** (compatibility matrix, test-harness extension, risk & gap register)
  are separate slices; the compatibility matrix consumes the platform list + discovery surfaces
  established here, and the risk register records the Threads/Lemmy unknowns §3.5 flags.
