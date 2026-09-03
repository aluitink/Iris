# 219 — Community feed supports remote members' outboxes

> 2026-09-03 · Phase 19.5.5 (community feed correctness) · the federated community feed

## What changed

The community feed endpoint (`GET /ap/v1/c/{name}/feed`) now fetches **remote members'** outboxes over
the wire, mirroring the `FeedService` (F-14) local/remote split. Previously, the community feed only
merged **local** members' outboxes (read from the local activity store). A remote member's content was
inaccessible unless it was delivered to the community inbox and propagated into member outboxes — a
narrower path than the full outbox merge.

### Server — `CommunityFeedService`

- **New constructor overloads** accept `ILocalActorResolver`, `IActorDocumentFetcher`,
  `IActivityPubClient`, and `FeedOptions` for remote outbox fetching. The original constructor
  (local-only) is preserved as a legacy overload that treats every member as local.
- **`ReadOutboxAsync(Iri memberIri, CancellationToken ct)`** — reads a member's outbox: local members
  from the local activity store, remote members over the wire (dispatched via
  `ILocalActorResolver.IsLocalAsync`).
- **`FetchRemoteOutboxAsync(Iri memberIri, CancellationToken ct)`** — walks a remote member's outbox
  (up to `FeedOptions.PagesPerActor` pages) over the wire. The member's `outbox` IRI is resolved from
  their public actor document (fetched via `IActorDocumentFetcher`); a broken remote contributes
  nothing (a single unreachable remote must not fail the whole feed).
- **`TruncateDedup(IReadOnlyList<IObjectOrLink> items)`** — truncates the merged feed to
  `FeedOptions.MaxItems`.
- **`ContainsInStrings(IEnumerable<string>? values, string query)`** — case-insensitive substring
  match (the `?q=` filter path, unchanged in behavior).

### Server — DI registration

The `ICommunityFeedService` registration in `ActivityPubServerExtensions.cs` now injects
`ILocalActorResolver`, `IActorDocumentFetcher`, `IActivityPubClient`, and `FeedOptions`. The outbound
client is created from the `IActivityPubClientFactory` using the instance actor's IRI (signed outbound
fetches). Without a configured instance actor, the client is null and remote members contribute nothing
(local members still work).

### Tests — 3 new integration tests

`CommunityFeedRemoteMemberIntegrationTests.cs`:

| Test | Scenario |
|------|----------|
| `Feed_MergesLocalAndRemoteMemberOutboxes` | Community on A with local member (alice, 1 post) + remote member (bob on B, 2 posts). Feed merges all 3 posts (1 local + 2 remote), newest-first. |
| `Feed_OnlyLocalMember_RemoteNotMember` | A second community with only alice as a member (bob NOT a member). Feed contains only alice's post. |
| `Feed_RemoteMemberOutboxUnavailable_ContributesNothing` | A third community with alice (local) + dave (remote, unreachable). Feed returns 200 with only alice's post (broken remote contributes nothing). |

### Test count

**1,319** (was 1,316, +3 new).
