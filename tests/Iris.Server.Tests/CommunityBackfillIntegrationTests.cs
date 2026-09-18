using System.Net;
using System.Text;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server.Services;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Xunit;
using CollectionPage = Iris.Core.Collections.CollectionPage;

namespace Iris.Server.Tests;

/// <summary>
/// 138.20 (full-thread backfill on first peer) — when Iris first peers with a Lemmy community that
/// already has history, the community feed's remote outbox walk persists the embedded objects to the
/// local object store and records the <c>Create</c> activities in the community's local members'
/// outboxes, so the historical content is available locally (not just proxied).
/// </summary>
public sealed class CommunityBackfillIntegrationTests
{
    private const string Host = "backfill.domain.local";
    private const string Community = "inter";
    private const string RemoteHost = "lemmy.luit.ink";
    private static readonly Iri CommunityIri = new($"https://{Host}/ap/v1/c/{Community}");
    private static readonly Iri MemberIri = new($"https://{Host}/ap/v1/u/bob");
    private static readonly Iri RemoteCommunityIri = new($"https://{RemoteHost}/c/interop");

    [Fact]
    public async Task Backfill_RemoteOutboxItems_PersistedToLocalStore()
    {
        var persistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedCommunityWithKey(persistence, Host, Community, memberIri: MemberIri);
        await persistence.Communities.AddFollowAsync(CommunityIri, RemoteCommunityIri);

        // Canned outbox JSON: a single-page OrderedCollectionPage with one Announce(Create(Page)).
        var outboxJson = BuildOutboxJson();
        var client = BuildClientWithStubHandler(outboxJson);

        var service = new CommunityFeedService(
            persistence, null, new StubLocalActorResolver(),
            new StubActorDocumentFetcher(RemoteCommunityIri), client,
            new FeedOptions { PagesPerActor = 5, MaxItems = 50 });

        var feed = await service.GetFeedAsync(CommunityIri);

        // (a) The feed contains the relayed content.
        Assert.NotEmpty(feed);

        // (b) The Page is persisted in the local object store.
        var pageIri = new Iri($"https://{RemoteHost}/post/1");
        Assert.True(
            await persistence.Objects.TryGetObjectAsync(pageIri, out var stored),
            "The backfilled Page should be in the local object store");
        var page = Assert.IsType<Page>(stored);
        Assert.Contains("Historical post", page.Name!);

        // (c) The Create is recorded in the local member's outbox.
        var memberOutbox = (await persistence.Activities.GetOutboxAsync(MemberIri)).ToList();
        Assert.Contains(memberOutbox, a =>
            a is Create c && c.Object?.OfType<Page>().Any(p => p.Id == pageIri.Value) == true);
    }

    [Fact]
    public async Task Backfill_IsIdempotent_SecondReadDoesNotDuplicate()
    {
        var persistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedCommunityWithKey(persistence, Host, Community, memberIri: MemberIri);
        await persistence.Communities.AddFollowAsync(CommunityIri, RemoteCommunityIri);

        var outboxJson = BuildOutboxJson(idSuffix: "2");
        var client = BuildClientWithStubHandler(outboxJson);
        var service = new CommunityFeedService(
            persistence, null, new StubLocalActorResolver(),
            new StubActorDocumentFetcher(RemoteCommunityIri), client,
            new FeedOptions { PagesPerActor = 5, MaxItems = 50 });

        await service.GetFeedAsync(CommunityIri);
        var countAfterFirst = (await persistence.Activities.GetOutboxAsync(MemberIri)).Count;

        await service.GetFeedAsync(CommunityIri);
        var countAfterSecond = (await persistence.Activities.GetOutboxAsync(MemberIri)).Count;

        Assert.Equal(countAfterFirst, countAfterSecond);
    }

    /// <summary>
    /// 139.3 scenario 5 regression: a REMOTE community the instance has interacted with is persisted to
    /// the community store by the remote community persister (135.1). When the local community follows
    /// that remote community, its outbox must be fetched OVER THE WIRE — not read from the (empty) local
    /// activity store. Before the fix, <c>ReadOutboxAsync</c> treated any community in the community
    /// store as local, so a followed remote (Lemmy) community was misrouted to its empty local outbox
    /// and the first-peer backfill captured nothing.
    /// </summary>
    [Fact]
    public async Task RemoteCommunity_PersistedToStore_IsFetchedOverWire_NotReadLocally()
    {
        var persistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedCommunityWithKey(persistence, Host, Community, memberIri: MemberIri);
        await persistence.Communities.AddFollowAsync(CommunityIri, RemoteCommunityIri);

        // Simulate the remote community persister (135.1): the remote Lemmy community is now in the
        // local community store (as it is in production after the instance interacts with it). Without
        // the 139.3 s5 host-locality gate, this makes ReadOutboxAsync treat the remote community as
        // local and read its (empty) local outbox.
        await persistence.Communities.PutCommunityAsync(new Group
        {
            Id = RemoteCommunityIri.Value,
            PreferredUsername = "interop",
            Outbox = new Link { Href = new Uri(RemoteCommunityIri.Value + "/outbox") },
        });

        var outboxJson = BuildOutboxJson(idSuffix: "3");
        var client = BuildClientWithStubHandler(outboxJson);

        // The instance base is the LOCAL host, so the remote community (on RemoteHost) is NOT hosted
        // on the instance and must be fetched over the wire.
        var service = new CommunityFeedService(
            persistence, null, new StubLocalActorResolver(),
            new StubActorDocumentFetcher(RemoteCommunityIri), client,
            new FeedOptions { PagesPerActor = 5, MaxItems = 50 },
            instanceBase: new Iri($"https://{Host}"));

        var feed = await service.GetFeedAsync(CommunityIri);

        // The remote community's outbox WAS fetched over the wire (the relayed content is present),
        // proving it was not misrouted to the empty local outbox.
        Assert.NotEmpty(feed);

        // The backfilled Page is persisted locally (the wire fetch path ran, not the local read).
        var pageIri = new Iri($"https://{RemoteHost}/post/3");
        Assert.True(
            await persistence.Objects.TryGetObjectAsync(pageIri, out _),
            "the remote community's content should be backfilled locally via the wire fetch");
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private static string BuildOutboxJson(string idSuffix = "1")
    {
        var lemmyMemberIri = $"https://{RemoteHost}/u/lemmyadmin";
        return $$"""
        {
          "type": "OrderedCollectionPage",
          "id": "https://{{RemoteHost}}/c/interop/outbox?page=1",
          "partOf": "https://{{RemoteHost}}/c/interop/outbox",
          "orderedItems": [
            {
              "type": "Announce",
              "id": "https://{{RemoteHost}}/c/interop/announce/{{idSuffix}}",
              "actor": "{{lemmyMemberIri}}",
              "object": {
                "type": "Create",
                "id": "https://{{RemoteHost}}/c/interop/create/{{idSuffix}}",
                "actor": "{{lemmyMemberIri}}",
                "object": {
                  "type": "Page",
                  "id": "https://{{RemoteHost}}/post/{{idSuffix}}",
                  "name": "Historical post",
                  "content": "<p>Old content</p>",
                  "attributedTo": "{{lemmyMemberIri}}"
                }
              }
            }
          ]
        }
        """;
    }

    private static IActivityPubClient BuildClientWithStubHandler(string outboxJson)
    {
        var key = KeyPairGenerator.GenerateRsa(new Iri($"https://{Host}/ap/v1/c/{Community}/#key-1"));
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(CommunityIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var handler = new StubHttpMessageHandler(outboxJson);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = CommunityIri, EnableRetry = false },
            handler);
    }

    // --- Stubs ---------------------------------------------------------------------------------------

    private sealed class StubLocalActorResolver : ILocalActorResolver
    {
        public Task<bool> IsLocalActorAsync(Iri actorIri, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private sealed class StubActorDocumentFetcher : IActorDocumentFetcher
    {
        private readonly Iri _remoteIri;
        public StubActorDocumentFetcher(Iri remoteIri) => _remoteIri = remoteIri;

        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        {
            if (actorIri.Equals(_remoteIri))
            {
                return Task.FromResult<Actor?>(new Group
                {
                    Id = actorIri.Value,
                    Outbox = new Link { Href = new Uri(actorIri.Value + "/outbox") },
                });
            }
            return Task.FromResult<Actor?>(null);
        }
    }

    /// <summary>
    /// A stub HTTP handler that returns canned JSON for the remote outbox and 404 for everything
    /// else. Used to exercise <see cref="CommunityFeedService"/> without a second HTTP server.
    /// </summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _outboxJson;
        public StubHttpMessageHandler(string outboxJson) => _outboxJson = outboxJson;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            // Return the canned outbox for any request to the remote host's outbox.
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_outboxJson, Encoding.UTF8, "application/activity+json"),
            };
            return Task.FromResult(response);
        }
    }
}
