# 118.2 — Communities: Post to a Remote (Lemmy) Community as a Lemmy-compatible Page

## Summary

The compose flow now detects whether a targeted community lives on a **different host** than the
signed-in actor (a remote community, e.g. a Lemmy one). For a remote community the post is built as
a **Lemmy-compatible `Page`** (rather than the local `Note`) and delivered **directly to the
community's inbox** via a signed `Create`. The `Page` carries `attributedTo` = [author, community],
`to` = [community, `as#Public`], `cc` = [community followers collection], `content` as
Markdown-rendered HTML, and a `source` field with the original Markdown — exactly the shape Lemmy's
`Create` handler expects. For a local community (same host) the behavior is **unchanged** (a `Note`
published to the author's outbox). A Dockerfile fix installs `libgssapi-krb5-2` in the runtime image
so the server's outbound HTTPS federation delivery no longer aborts on a missing GSSAPI library.

## Context

Before 118.2, posting to any community (local or remote) built a `Note` and published it to the
author's **own outbox** (`client.PostNoteAsync`). That works for a local community — the home server
sees the `Create` in its own outbox and routes the post into the community's members' feed. It does
**not** work for a remote community: the remote server never observes an activity delivered to the
author's own outbox, so a Lemmy community would never receive the post. (118.1 added the ability to
*follow* a remote community; 118.2 makes it possible to *post* to one.)

Research (Lemmy `crates/apub/objects/src/protocol/page.rs`) confirmed the Lemmy-compatible post
shape: the object `type` is `"Page"` (Lemmy rejects a `Note` that carries an `inReplyTo`), the object
is addressed `to` the community + `as#Public` with `cc` the community's followers, the activity is
`Create` posted to the community actor's own inbox, `content` is HTML, and `source` carries the
Markdown with `mediaType: "text/markdown"`. `attributedTo` carries both the author and the community.

## Changes

### Compose.razor

- **`IsRemoteCommunity`** (new property): `true` when the targeted community's IRI host differs from
  the signed-in actor's IRI host (case-insensitive). `false` when no community is targeted, the
  session has no actor, or either IRI is malformed.
- **`PostToCommunityAsync`** now branches: `IsRemoteCommunity` → `PostToRemoteCommunityAsync`, else
  `PostToLocalCommunityAsync` (the pre-118.2 `Note` path, moved verbatim).
- **`PostToRemoteCommunityAsync`** (new): builds the Lemmy-compatible `Page` (the fields above),
  renders `Content` to HTML with `Iris.Core.Rendering.Markdown.ToHtml` (the same dependency-free
  renderer the object views use), writes `cc` + `source` into `ExtensionData` (Rule 6 — the AS
  library does not model either), and wraps it in a `Create` with a minted `Id` (`…/creates/{guid}` —
  inbox-received activities are not id-minted server-side) delivered to
  `communityIri.InboxOf()`.
- **`PostToLocalCommunityAsync`** (new): the unchanged local `Note` path (outbox publish).
- **`BuildCommunityAttachments`** / **`BuildCommunityTags`** (new): the media-attachment and
  mention/hashtag-`tag` builders extracted so the local and remote paths share them.
- Added `@using Iris.Core.Rendering` (for `Markdown.ToHtml`).

### Dockerfile (apps/Iris.Web)

- The runtime stage now installs `libgssapi-krb5-2`. .NET's `SslStream` negotiates Kerberos (GSSAPI)
  by default on outbound HTTPS, which loads `libgssapi_krb5`; the `aspnet` base image does not ship
  it, so an outbound federation request whose TLS negotiation trips the GSSAPI path aborts with
  `The response ended prematurely` (`ResponseEnded`). Installing the lib makes that negotiation
  succeed.

## Verification

- `dotnet build` — 0 warnings, 0 errors.
- `dotnet test` — 1,126 server + 171 client + 95 web + the remaining suites pass, **0 real
  failures** (the `FollowEdgeConvergence` / `DuplicateInboundDeliveryIdempotency` federation tests
  are load-flaky under the full-suite run but pass when the server suite runs alone; this change
  touches no server code).
- **Wire format** (deterministic serialization check): the remote `Create` serializes to exactly the
  Lemmy shape — `"type":"Page"`, `attributedTo:[author,community]`, `to:[community,as#Public]`,
  `cc:[community/followers]`, `content` = rendered HTML, `source:{content,mediaType:"text/markdown"}`.
- Live verification (Playwright on the Docker app, signed in as `andrew`):
  - **Local community** (creator of `technology`): post → **HTTP 202**, minted IRI
    `…/creates/…`; the object is stored as a `Note` (`…/notes/…`, `to` = followers + `as#Public`) —
    the unchanged local path.
  - **Remote community** (`https://lemmy.ml/c/privacy`): the compose page renders
    "Posting to community" (fetched the remote community name via the proxy); the post is built as a
    `Page` and routed through the home proxy (`POST /ap/v1/proxy/…lemmy.ml/c/privacy/inbox`). The
    relay **still fails in this sandbox** — lemmy.ml's TLS stack rejects .NET's handshake even with
    the GSSAPI lib present (the host's `curl` reaches lemmy.ml with HTTP 200, proving egress is
    fine; the failure is a remote-side TLS quirk specific to .NET clients). This is a pre-existing
    limitation (118.1's webfinger lookup to lemmy.ml hit the same wall); the error surfaces as a 500
    proxy response rather than hanging. Full live acceptance of the Lemmy post requires a
    federation environment whose egress completes the TLS handshake with a real Lemmy server.

## Notes / open follow-ups

- The remote `Page` is **not** yet read back by Iris's own community-feed reader (the feed reader
  consumes `Note`/`Article`; a remote `Page` posted by a remote member would need a reader update to
  render). That is out of scope for 118.2, which is specifically about *posting in the proper form*.
- Likes/Dislikes enumeration on a Lemmy community (mentioned in the 118.2 plan item) is not
  addressed here.
