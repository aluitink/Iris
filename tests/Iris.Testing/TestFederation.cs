using Iris.Client;
using Iris.Server;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Testing;

/// <summary>
/// Shared federation wiring helpers for integration tests: building a signed
/// <see cref="IActorDocumentFetcher"/> (an instance's inbound key resolver reaching another
/// instance's actor documents), starting a single-instance <see cref="TestServer"/>, and the
/// polling <c>WaitFor</c> / "post to inbox" helpers that were previously copy-pasted across the
/// federation suites. Complements <see cref="ActivityPubHostFactory"/> (the server bootstrap) and
/// <see cref="LazyHandler"/> (the deferred in-process transport).
/// </summary>
public static class TestFederation
{
    /// <summary>
    /// Builds an <see cref="IActorDocumentFetcher"/> whose client is signed as the instance's local
    /// actor (derived from <c>https://{host}/ap/v1/u/{handle}</c> using <paramref name="key"/>) and
    /// routes to <paramref name="targetServer"/> — i.e. this instance's inbound key resolver resolves
    /// remote keys by fetching the <em>other</em> instance's actor documents. The returned client is
    /// disposable (callers own it) so a test can dispose it with its <see cref="TestServer"/>.
    /// </summary>
    public static (IActorDocumentFetcher Fetcher, IDisposable Client) BuildFetcherFor(
        string host, string handle, ISigningKey key, TestServer targetServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            targetServer.CreateHandler());

        return (new IrisActorDocumentFetcher(client, new RemoteActorCache()), client);
    }

    /// <summary>
    /// Starts a single-instance <see cref="TestServer"/>, registering <paramref name="key"/> as the
    /// instance actor's signing key (so the host's outbound <c>DeliveryWorker</c> can sign) and
    /// optionally overriding the <see cref="IActorDocumentFetcher"/> (for federation wiring). The outbound
    /// delivery transport defaults to the real <c>HttpClientHandler</c>; pass <paramref name="deliveryTransport"/>
    /// to route the <c>DeliveryWorker</c> to another in-process <see cref="TestServer"/> (federation).
    /// </summary>
    public static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence, ISigningKey key,
        IActorDocumentFetcher? fetcher = null,
        Func<HttpMessageHandler>? deliveryTransport = null)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            Fetcher = fetcher,
            DeliveryTransport = deliveryTransport,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
        });
    }

    /// <summary>
    /// Polls <paramref name="probe"/> (every 50ms) until it returns <c>true</c> or <paramref name="timeout"/>
    /// elapses. Used to wait on the <em>effect</em> of an asynchronous delivery (e.g. a remote instance
    /// having stored the federated activity) rather than on the local write.
    /// </summary>
    public static async Task WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Posts <paramref name="activity"/> to <paramref name="actorIri"/>'s inbox via a client signed as
    /// the actor (the "local post" path), routed to the in-process <paramref name="server"/>. Returns the
    /// HTTP status (202 Accepted when the full inbound pipeline — signature validation + handler — ran).
    /// The returned client is disposable.
    /// </summary>
    public static async Task<(int Status, IDisposable Client)> PostToInboxAsync(
        TestServer server, Iri actorIri, Activity activity)
    {
        var keyStore = server.Services.GetRequiredService<IKeyStore>();
        var keyProvider = server.Services.GetRequiredService<IKeyProvider>();
        var signer = server.Services.GetRequiredService<ISignatureSigner>();
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            server.CreateHandler());
        var result = await client.DeliverAsync(actorIri.InboxOf(), activity);
        return (result.StatusCode, client);
    }
}
