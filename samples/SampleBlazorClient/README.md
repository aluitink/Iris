# Iris SampleBlazorClient — the "server explorer"

A runnable Iris **Blazor WebAssembly** app — the Phase 8 sample's "server explorer" (Deliverable B).
It is a thin, browser-side UI over the full `Iris.Client` ActivityPub pipeline: log on to an instance
by **WebFinger address**, then **enumerate and explore** the seeded graph (instance, actors, an actor's
outbox, an object + its replies, the community feed, global search) and **write** to it (post a note,
reply, follow/unfollow across instances, like, and moderate).

The app is a **static** site: the browser downloads `index.html` + `_framework` and runs it
client-side. It makes all ActivityPub I/O directly from the browser to the instance it is pointed at
(signed requests via the `Iris.Client` pipeline) — the container that serves it (the sample's `iris-ui`)
makes no outbound calls of its own.

## What it is / is not

- **A real client UI, not a mock.** Every screen calls the production `Iris.Client` surface
  (`IActivityPubClient`) against a live `Iris.Server` — the same pipeline the library's own integration
  tests exercise, but driven from a browser.
- **Logon by WebFinger address.** The user enters a WebFinger address (`alice@iris-a`), a password, and
  (for a known local instance, pre-filled) a base URL. The app resolves the address by WebFinger, logs on
  (Basic auth → the owner-only actor document → the actor's signing key), and keeps a signed,
  cache-enabled, proxy-fallback-enabled client.
- **Instance switching.** A recent-instances switcher logs out of the current instance and logs on to a
  previously-visited one (log on = WebFinger resolve + Basic-auth key acquisition + a fresh client).
- **The external-instance mechanism** (below) is how it is pointed at an instance that is *not* one of
  the two local Docker instances.

## Quick start

The explorer is served by the `iris-ui` service of the sample Docker stack (Deliverable A):

```sh
docker compose -f samples/docker-compose.yml up --build -d     # iris-a → host:8081, iris-b → host:8082, iris-ui → host:8090
# in a browser, open http://localhost:8090
```

**Log on to a local instance:** enter the WebFinger address + the shared sample password
(`iris-sample`). The endpoint override is **empty by default** — the explorer dials the actor's home
server (the address's host over `https`). For the local compose stack, the advertised FQDN
(`iris-dev1.luit.ink` / `iris-dev2.luit.ink`) is not resolvable from the browser, so expand the
**"Advanced: talk to a different endpoint"** section and enter the host-published port:

| Instance | WebFinger address | Endpoint override |
|---|---|---|
| iris-a | `alice@iris-dev1.luit.ink` | `http://localhost:8081` |
| iris-b | `alice@iris-dev2.luit.ink` | `http://localhost:8082` |

(Both instances seed the same local actor, `alice`; the password for every seeded actor is
`iris-sample` — see [`samples/SampleServer/README.md`](../SampleServer/README.md).)

> **Production instances:** when the FQDN is directly reachable from the browser (the normal
> production case), no override is needed — the explorer dials `https://{host}` automatically.

**Local (no Docker):** the explorer is a static site, so it can be run against a local
[`SampleServer`](../SampleServer/README.md):

```sh
dotnet run --project samples/SampleServer                       # → http://localhost:5000
dotnet run --project samples/SampleBlazorClient --no-build      # → http://localhost:8080 (Blazor dev server)
# in a browser, open http://localhost:8080 and log on with:
#   address alice@localhost · password iris-sample
#   (expand "Advanced" and set endpoint override to http://localhost:5000, since the
#    advertised IRI host `localhost` resolves to https://localhost by default)
```

## Screens

| Route | Screen | What it calls (`IActivityPubClient`) |
|---|---|---|
| `/` | **Home** — log on + recent-instances switcher | `LogOnAsync` (WebFinger resolve + Basic-auth key acquisition) |
| `/instance` | **Instance overview** — instance name, software, protocols, open registrations | `GetNodeInfoAsync` (served at `{base}/nodeinfo/2.0`) |
| `/actors` | **Actors** — global directory / search | `SearchAsync` (empty query = the whole directory) |
| `/actor` | **Actor detail** — one actor: outbox feed, moderation counts, follow/unfollow, mute/unmute, block/unblock, flag/unflag | `GetObjectAsync`, `GetCollectionItemsAsync(OutboxOf())`, `FollowAsync`/`UndoFollowAsync`, `MuteAsync`/`UnmuteAsync`, `BlockAsync`/`UnblockAsync`, `FlagAsync`/`UnflagAsync`, `GetMutesAsync`/`GetBlocksAsync`/`GetFlagsAsync` |
| `/object` | **Object** — load any object by IRI + its reply thread + like | `GetObjectAsync`, `GetRepliesAsync`, `LikeAsync` |
| `/community` | **Community** — feed, members, in-community search | `GetCommunityFeedAsync`, `GetCollectionItemsAsync({community}/members)`, `SearchAsync` (scoped) |
| `/compose` | **Compose** — post a note or a reply (parent IRI + comma-separated mentions) | `PostNoteAsync`, `PostReplyAsync` |

The shared object renderer is `Components/ObjectView.razor`. The write actions follow the sample's
**delivery model** (the actor's **outbox** is the write surface — an authored activity is POSTed to the
acting actor's *own* outbox, which the instance records and federates to the recipient's inbox; see
[change 077](../../docs/changes/077-delivery-model-outbox-write-surface.md)). Cross-instance writes (e.g.
`iris-a`'s alice following `iris-b`'s alice) are signed, server-delivered, and validated on the remote
instance.

> The raw-JSON **inspector** and **proxy-fallback** paths (the two S7 follow-up write-surface screens) are
> pinned in-process by [`tests/SampleBlazorClient.Tests`](../../tests/SampleBlazorClient.Tests)
> ([change 079](../../docs/changes/079-explorer-raw-inspector-and-proxy-fallback.md)); they are not yet
> separate Blazor pages in this sample. The proxy-fallback *behavior* (a direct 401 to a remote instance
> falls back through the home proxy, which re-signs) is enabled on the explorer's client
> (`UseProxyFallback = true`) and is what makes the external-instance read + follow paths work.

## Logon & the base-URL / IRI-host rule

Log on (`Pages/Home.razor`) takes two fields plus an optional override:

- **WebFinger address** — `handle@host` (e.g. `alice@iris-a`). Parsed by `Explorer/WebFingerAddress.cs`.
- **Password** — the Basic-auth password for that actor.
- **Endpoint override** (collapsed "Advanced" section) — **empty by default.** When empty, the explorer
  dials the actor's home server (the address's host over `https`). When filled, it is used as the dial
  base as-is (the user's explicit input always wins).

The **advertised IRI host** and the **dial base** are **separate**
([DEPLOYMENT — Routable addresses](../../docs/reference/DEPLOYMENT.md#routable-addresses-the-docker-only-routable-rule),
[change 074](../../docs/changes/074-base-url-vs-iri-host-config.md)):

- The **advertised IRI host** is the address's host — for a local instance that is its Docker service name
  (`iris-a`) or its public FQDN (`iris-dev1.luit.ink`), which may not be resolvable from the browser.
- The **dial base** is what the browser actually dials — by default the IRI host (production assumption);
  overridden by the advanced field for unusual deployments (a host-published port, a reverse proxy, etc.).

**The dial base is resolved at log-on time** (`Pages/Home.razor` → `ResolveDialBase`):

- When the user **enters** an endpoint override, it is used **as-is**.
- When the override is **empty**, the dial base is the actor's home server (the address's host over
  `https`, e.g. `alice@example.com` → `https://example.com`). This is the **production assumption**:
  the IRI host is browser-reachable.

The user therefore only needs the address + password when talking to a real production server; the
endpoint override is a collapsed, opt-in field for deployments where the instance is reachable at a
different address than its advertised host.

## The external-instance mechanism (no real dev FQDN committed)

The explorer is pointed at an instance that is **not** one of the two local Docker instances by supplying,
at logon:

1. a **WebFinger address** on that instance (`user@example.com`), and
2. the actor's **password**.

That's all. The dial base **defaults to the actor's home server** (the address's host over `https`) —
the **production assumption**: the IRI host is browser-reachable. Nothing about the external instance
is hard-coded.

When the instance **is not** reachable at its advertised host (a local Docker instance behind a
host-published port, a reverse proxy on a different port, a LAN IP, etc.), the user expands the
**"Advanced: talk to a different endpoint"** section on the log-on form and enters the browser-reachable
address (e.g. `http://my-host:port`). That value is used as the dial base as-is.

The WebFinger-resolved actor IRI (whose host is the *external* host) becomes the client's
`actorIriOverride` (what it authenticates as and signs as), while the transport dials the resolved dial
base — the base-URL / IRI-host separation that makes an external instance work the same way a local one
does. The read + follow + **proxy-fallback** paths all run against the external instance through this one
mechanism (a direct request the browser cannot make — CORS, or a 401 the instance cannot validate — falls
back through the home proxy, which re-signs with the acting actor's key).

> **No real dev FQDN is committed.** The sample is self-contained on `localhost` (host-published ports) +
> service names (in-network). Any external base URL / FQDN is **operator-supplied at logon** (runtime, in the
> browser) and is never written into the repo — the mechanism is documented here with placeholders only.

## How it is tested

- `tests/SampleBlazorClient.Tests` — in-process coverage of the explorer's screens and the full
  `Iris.Client` pipeline against a live `Iris.Server` (logon-by-address + instance switching, the read
  screens, the write screens incl. a genuine two-instance federated follow/unfollow, moderation, and the
  raw-JSON inspector + proxy-fallback). See [change 072](../../docs/changes/072-sample-blazor-wasm-explorer.md)
  through [change 079](../../docs/changes/079-explorer-raw-inspector-and-proxy-fallback.md).
- `scripts/docker-smoke-test.sh` — the Docker smoke path boots the three-service stack and asserts, over
  genuine sockets, that the UI serves its index page and that a **signed cross-container Follow** lands on
  the remote instance (plus the proxy fallback). The browser behaviors above are the manual-exploration
  checklist the smoke test cannot click ([INTEROP_TEST_HARNESS §4a](../../docs/reference/INTEROP_TEST_HARNESS.md#4a-standing-manual-interop-checklist-the-browser-path-no-test-can-drive)). See
  [DEPLOYMENT](../../docs/reference/DEPLOYMENT.md).

## Manual exploration checklist (the browser path the smoke test cannot drive)

With the stack up (`iris-a` → `:8081`, `iris-b` → `:8082`, `iris-ui` → `:8090`) and a browser at
`http://localhost:8090`:

1. **Log on** to `alice@iris-a` (base `http://localhost:8081`, password `iris-sample`); the app resolves
   the address by WebFinger and logs on.
2. **Explore:** open **Instance** (nodeinfo), **Actors** (directory), an **Actor** (outbox + moderation
   counts), an **Object** (a note + its reply thread), and the **Community** (feed + members).
3. **Switch instance** to `iris-b` (`alice@iris-b`, base `http://localhost:8082`) from the recent-instances
   switcher; confirm the actor/community data is iris-b's.
4. **Write cross-instance:** on `iris-a`'s alice, **follow** `iris-b`'s alice (the actor's IRI carries the
   `iris-b` host); it is signed, delivered to iris-b, validated, and recorded — visible in iris-b's public
   followers. **Like** and **post/reply** likewise.
5. **Moderate:** **mute** (local), then **block** / **flag** (signed, federated) on an actor; confirm the
   counts update and the edges are recorded.
6. **External instance (optional):** log on to a non-local instance by its WebFinger address + password + a
   browser-reachable base URL; confirm the read + follow + proxy-fallback paths work against it.
