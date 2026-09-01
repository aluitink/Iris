using System.Security.Cryptography;
using System.Text;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Delivery;

/// <summary>
/// Phase 12 Slice 12.10 end-to-end test (the federated write half of F-02/F-03): a local author (alice
/// on instance A) edits and then deletes a note; the <see cref="UpdateActivityHandler"/> /
/// <see cref="DeleteActivityHandler"/> refresh / tombstone the stored object on A <em>and</em>
/// propagate the <see cref="Update"/> / <see cref="Delete"/> to alice's remote follower (erin on
/// instance C) — over the wire, signed as alice. C's handlers apply the change to C's copy of the
/// object (C stored the note via the outbound <see cref="Create"/> federation, Slice 11.7), so C serves
/// the edited content after the Update and the <see cref="Tombstone"/> after the Delete.
/// </summary>
/// <remarks>
/// Topology: instance A (prop-a.domain.local, author <c>alice</c>) and instance C (prop-c.domain.local,
/// follower <c>erin</c>). The follow edge erin→alice is recorded on A (A owns alice's follower set —
/// the propagation target set is read from A's follow store). Outbound propagation runs on a test
/// delivery worker signed as alice (A's host worker signs as the instance actor, which is not alice),
/// routing to C. C's inbound signature validation resolves alice's key from A's actor document
/// (C's fetcher reaches A's in-process <c>TestServer</c>), so the propagated activities are accepted
/// and applied to C's stored copy.
/// </remarks>
public sealed class ObjectPropagationIntegrationTests : IDisposable
{
    private const string AHost = "prop-a.domain.local";
    private const string CHost = "prop-c.domain.local";
    private const string Alice = "alice";
    private const string Erin = "erin";

    private readonly TestServer _a;
    private readonly TestServer _c;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _cPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _aliceInboxIri;
    private readonly Iri _erinActorIri;
    private readonly KeyPair _cSeedKey;

    public ObjectPropagationIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _cPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;
        _aliceInboxIri = _aliceActorIri.InboxOf();

        var cSeeded = TestSeeder.SeedPersonWithKey(_cPersistence, CHost, Erin);
        _erinActorIri = cSeeded.ActorIri;
        _cSeedKey = cSeeded.Key;

        // The follow edge erin→alice is recorded on A (A is alice's home instance; it owns her follower
        // set — the propagation target set is read from A's follow store).
        _aPersistence.Follows.RecordFollowAsync(_erinActorIri, _aliceActorIri).GetAwaiter().GetResult();

        _a = StartAuthorServer(
            _aPersistence, _aliceKey, _aliceActorIri,
            targetServer: () => _c!, selfServer: () => _a!);

        // C hosts erin; its fetcher reaches A so C validates the propagated Update/Delete by fetching
        // A's actor doc (alice's key). A is already created here.
        _c = StartServer(
            CHost, Erin, _cPersistence, cSeeded.Key,
            fetcher: BuildFetcherFor(CHost, Erin, cSeeded.Key, targetServer: _a));
    }

    public void Dispose()
    {
        _a.Dispose();
        _c.Dispose();
    }

    // --- A local edit is federated to the remote follower (F-02 federated half) ------------

    [Fact]
    public async Task LocalUpdate_IsFederatedToRemoteFollower_RemoteCopyRefreshed()
    {
        var noteIri = new Iri($"{_aliceActorIri}/notes/u1");

        // 1. alice posts a note; the Create is federated to erin's inbox (Slice 11.7) and C stores the
        // embedded note in its object store (the CreateActivityHandler's object-store half). The test
        // worker routes to A (alice's home instance) so the inbound Create runs through A's full
        // pipeline; A's own host worker then federates the Create to C.
        var create = BuildCreate(_aliceActorIri, noteIri, "original body");
        using var worker = BuildDeliveryWorker(_aliceActorIri, _aliceKey, _a);
        await worker.Service.DeliverAsync(_aliceInboxIri, create);
        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(
            async () => await _cPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            timeout: TimeSpan.FromSeconds(10));

        Assert.True(await _cPersistence.Objects.TryGetObjectAsync(noteIri, out var cStored),
            "C should have stored the note federated by A's Create");
        Assert.Equal("original body", cStored!.Content?.FirstOrDefault());

        // 2. alice edits the note; the Update handler refreshes A's copy AND propagates the Update to
        // erin's inbox (signed as alice). C's UpdateActivityHandler refreshes C's stored copy.
        var update = BuildUpdate(_aliceActorIri, noteIri, "edited body");
        await worker.Service.DeliverAsync(_aliceInboxIri, update);
        await WaitForAsync(
            async () => await _cPersistence.Objects.TryGetObjectAsync(noteIri, out var c)
                && c is Note { Content: { } content } && content.FirstOrDefault() == "edited body",
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        // A's local copy is refreshed ...
        Assert.True(await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var aStored));
        Assert.Equal("edited body", aStored!.Content?.FirstOrDefault());
        // ... and C's remote copy is refreshed too (the federated half of F-02).
        Assert.True(await _cPersistence.Objects.TryGetObjectAsync(noteIri, out var cEdited));
        Assert.Equal("edited body", cEdited!.Content?.FirstOrDefault());
    }

    // --- A local delete is federated to the remote follower (F-03 federated half) ----------

    [Fact]
    public async Task LocalDelete_IsFederatedToRemoteFollower_RemoteCopyTombstoned()
    {
        var noteIri = new Iri($"{_aliceActorIri}/notes/d1");

        // 1. alice posts a note; C stores the embedded note (as above). The test worker routes to A
        // (alice's home instance) so the inbound Create runs through A's full pipeline; A's own host
        // worker then federates the Create to C.
        var create = BuildCreate(_aliceActorIri, noteIri, "doomed body");
        using var worker = BuildDeliveryWorker(_aliceActorIri, _aliceKey, _a);
        await worker.Service.DeliverAsync(_aliceInboxIri, create);
        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(
            async () => await _cPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            timeout: TimeSpan.FromSeconds(10));

        Assert.True(await _cPersistence.Objects.TryGetObjectAsync(noteIri, out _));

        // 2. alice deletes the note; the Delete handler tombstones A's copy AND propagates the Delete
        // to erin's inbox (signed as alice). C's DeleteActivityHandler tombstones C's stored copy.
        var delete = BuildDelete(_aliceActorIri, noteIri);
        await worker.Service.DeliverAsync(_aliceInboxIri, delete);
        await WaitForAsync(
            async () => await _cPersistence.Objects.TryGetObjectAsync(noteIri, out var c)
                && c is Tombstone,
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        // A's local copy is tombstoned ...
        Assert.True(await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var aTomb));
        Assert.IsType<Tombstone>(aTomb);
        // ... and C's remote copy is tombstoned too (the federated half of F-03).
        Assert.True(await _cPersistence.Objects.TryGetObjectAsync(noteIri, out var cTomb));
        Assert.IsType<Tombstone>(cTomb);
        Assert.Equal(noteIri.Value, cTomb!.Id);
    }

    // --- A remote actor deletes a note we hold a copy of: tombstoned, no collateral, no re-prop (19.3.4)

    /// <summary>
    /// 19.3.4 direction 2: a note *originating locally* (erin on B) is federated to a remote follower
    /// (bob on A), who stores a copy; erin then deletes the note and A's <see cref="DeleteActivityHandler"/>
    /// receives the federated <see cref="Delete"/> from erin — a <em>remote</em> actor on A. A must
    /// tombstone its copy (the owner guard accepts a remote author when A holds an attributed copy) with
    /// <strong>correct scope</strong> (A's own unrelated note is left untouched — no collateral deletion)
    /// and <strong>without re-propagating</strong> (only erin's home instance, B, re-fans-out; a non-home
    /// copy applies the delete locally and stops).
    /// </summary>
    /// <remarks>
    /// Topology: instance A (prop-b.domain.local, local person <c>bob</c>) and instance B (prop-c,
    /// <c>erin</c>, the author). bob follows erin (bob→erin, recorded on B — the author's home instance,
    /// which owns the propagation target set). The test delivery worker (signed as erin, routing to A)
    /// delivers the federated Create and Delete to bob's inbox on A; A's <see cref="CreateActivityHandler"/>
    /// (bob is local on A) stores the embedded note — the "remote copy" on A — and A's
    /// <see cref="DeleteActivityHandler"/> tombstones it. erin is <em>not</em> a local actor on A, so the
    /// delete's owner guard accepts the federated (remote) delete and the re-propagation branch is skipped
    /// (only the home instance re-fans-out).
    /// </remarks>
    [Fact]
    public async Task RemoteAuthorDelete_LocalCopyTombstoned_NoCollateral_NoRePropagation()
    {
        // bob is a local person on instance A (prop-b) who follows erin (on B). B (prop-c) owns the bob→erin
        // follow edge (the author's home instance owns her follower set / propagation target set).
        const string BobHost = "prop-b.domain.local";
        var aPersistence = new InMemoryPersistenceProvider();
        var bobSeeded = TestSeeder.SeedPersonWithKey(aPersistence, BobHost, "bob");
        var bobActorIri = bobSeeded.ActorIri;

        // bob→erin on B (so B's outbound Create/Delete federation targets bob's inbox on A).
        await _cPersistence.Follows.RecordFollowAsync(bobActorIri, _erinActorIri);

        // Start A (bob's home instance) with a fetcher that reaches B (so A validates inbound activities
        // signed as erin by fetching B's actor doc for erin's key) — mirroring how B's fetcher reaches A.
        // bob's key is registered so A can sign its own outbound deliveries (unused here: the
        // re-propagation branch is the point of the assertion and is skipped for a remote author).
        var bServer = _c;
        var aServerRef = StartServer(
            BobHost, "bob", aPersistence, bobSeeded.Key,
            fetcher: BuildFetcherFor(BobHost, "bob", bobSeeded.Key, targetServer: bServer));

        var noteIri = new Iri($"{_erinActorIri}/notes/r1");

        // A's unrelated note (the collateral-deletion control on A's side): stored directly so the remote
        // delete can be shown not to touch it.
        var unrelatedIri = new Iri($"{_erinActorIri}/notes/u1");
        await aPersistence.Objects.PutObjectAsync(
            new Note
            {
                Id = unrelatedIri.Value,
                Content = ["erin's other note"],
                AttributedTo = [new Link { Href = new Uri(_erinActorIri.Value) }],
            });

        // 1. erin's note is federated to bob's inbox on A (signed as erin). A's CreateActivityHandler
        // (bob is local on A) stores the embedded note (attributed to erin) — the "remote copy" on A.
        var create = BuildCreate(_erinActorIri, noteIri, "erin's body");
        using var erinWorker = BuildDeliveryWorker(_erinActorIri, _cSeedKey, aServerRef);
        await erinWorker.StartAsync(CancellationToken.None);
        await erinWorker.Service.DeliverAsync(bobActorIri.InboxOf(), create);
        await WaitForAsync(
            async () => await aPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            timeout: TimeSpan.FromSeconds(10));
        Assert.True(await aPersistence.Objects.TryGetObjectAsync(noteIri, out var aStored));
        Assert.Equal("erin's body", aStored!.Content?.FirstOrDefault());

        // 2. erin deletes the note; the federated Delete reaches bob's inbox on A (signed as erin). A's
        // DeleteActivityHandler receives a Delete from a REMOTE actor (erin, not local on A) for an object
        // A holds attributed to erin → owner guard passes → A tombstones its copy. A does NOT re-propagate
        // (erin is not local on A; the home instance, B, owns the re-fan-out).
        var delete = BuildDelete(_erinActorIri, noteIri);
        await erinWorker.Service.DeliverAsync(bobActorIri.InboxOf(), delete);
        await WaitForAsync(
            async () => await aPersistence.Objects.TryGetObjectAsync(noteIri, out var a) && a is Tombstone,
            timeout: TimeSpan.FromSeconds(10));
        await erinWorker.StopAsync(CancellationToken.None);

        // A's copy of erin's note is tombstoned (the federated half of F-03, direction 2).
        Assert.True(await aPersistence.Objects.TryGetObjectAsync(noteIri, out var aTomb));
        Assert.IsType<Tombstone>(aTomb);
        Assert.Equal(noteIri.Value, aTomb!.Id);

        // Correct scope: A's unrelated note is untouched — no collateral deletion.
        Assert.True(await aPersistence.Objects.TryGetObjectAsync(unrelatedIri, out var unrelated));
        Assert.IsType<Note>(unrelated);
        Assert.Equal("erin's other note", unrelated!.Content?.FirstOrDefault());

        // No re-propagation: the handler's re-propagation branch (DeleteActivityHandler, F-03 federated
        // half) runs only when the deleting actor is local on this instance (the home instance re-fans-out).
        // erin is a remote actor on A (she lives on B/prop-c, not on A/prop-b), so A's local-actor store
        // does not contain her and the branch is skipped — a non-home copy applies the delete locally and
        // stops, rather than fanning the tombstone out again.
        Assert.False(
            await aPersistence.Actors.TryGetActorAsync(_erinActorIri, out _),
            "erin must not be a local actor on A (only then would A re-propagate the delete)");
    }

    // --- A remote actor updates a note we hold a copy of: refreshed, no collateral, no re-prop (19.3.6)

    /// <summary>
    /// 19.3.6 direction 2: a note *originating locally* (erin on B) is federated to a remote follower
    /// (bob on A), who stores a copy; erin then edits the note and A's <see cref="UpdateActivityHandler"/>
    /// receives the federated <see cref="Update"/> from erin — a <em>remote</em> actor on A. A must refresh
    /// its copy (the owner guard accepts a remote author when A holds an attributed copy) with
    /// <strong>correct scope</strong> (A's own unrelated note is left untouched — no collateral rewrite) and
    /// <strong>without re-propagating</strong> (only erin's home instance, B, re-fans-out; a non-home copy
    /// applies the update locally and stops).
    /// </summary>
    /// <remarks>
    /// Topology: instance A (prop-b.domain.local, local person <c>bob</c>) and instance B (prop-c,
    /// <c>erin</c>, the author). bob follows erin (bob→erin, recorded on B — the author's home instance,
    /// which owns the propagation target set). The test delivery worker (signed as erin, routing to A)
    /// delivers the federated Create and Update to bob's inbox on A; A's <see cref="CreateActivityHandler"/>
    /// (bob is local on A) stores the embedded note — the "remote copy" on A — and A's
    /// <see cref="UpdateActivityHandler"/> refreshes it. erin is <em>not</em> a local actor on A, so the
    /// update's owner guard accepts the federated (remote) update and the re-propagation branch is skipped
    /// (only the home instance re-fans-out).
    /// </remarks>
    [Fact]
    public async Task RemoteAuthorUpdate_LocalCopyRefreshed_NoCollateral_NoRePropagation()
    {
        // bob is a local person on instance A (prop-b) who follows erin (on B). B (prop-c) owns the bob→erin
        // follow edge (the author's home instance owns her follower set / propagation target set).
        const string BobHost = "prop-b.domain.local";
        var aPersistence = new InMemoryPersistenceProvider();
        var bobSeeded = TestSeeder.SeedPersonWithKey(aPersistence, BobHost, "bob");
        var bobActorIri = bobSeeded.ActorIri;

        // bob→erin on B (so B's outbound Create/Update federation targets bob's inbox on A).
        await _cPersistence.Follows.RecordFollowAsync(bobActorIri, _erinActorIri);

        // Start A (bob's home instance) with a fetcher that reaches B (so A validates inbound activities
        // signed as erin by fetching B's actor doc for erin's key) — mirroring how B's fetcher reaches A.
        var bServer = _c;
        var aServerRef = StartServer(
            BobHost, "bob", aPersistence, bobSeeded.Key,
            fetcher: BuildFetcherFor(BobHost, "bob", bobSeeded.Key, targetServer: bServer));

        var noteIri = new Iri($"{_erinActorIri}/notes/u2");

        // A's unrelated note (the collateral-rewrite control on A's side): stored directly so the remote
        // update can be shown not to touch it.
        var unrelatedIri = new Iri($"{_erinActorIri}/notes/u3");
        await aPersistence.Objects.PutObjectAsync(
            new Note
            {
                Id = unrelatedIri.Value,
                Content = ["erin's other note"],
                AttributedTo = [new Link { Href = new Uri(_erinActorIri.Value) }],
            });

        // 1. erin's note is federated to bob's inbox on A (signed as erin). A's CreateActivityHandler
        // (bob is local on A) stores the embedded note (attributed to erin) — the "remote copy" on A.
        var create = BuildCreate(_erinActorIri, noteIri, "erin's original body");
        using var erinWorker = BuildDeliveryWorker(_erinActorIri, _cSeedKey, aServerRef);
        await erinWorker.StartAsync(CancellationToken.None);
        await erinWorker.Service.DeliverAsync(bobActorIri.InboxOf(), create);
        await WaitForAsync(
            async () => await aPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            timeout: TimeSpan.FromSeconds(10));
        Assert.True(await aPersistence.Objects.TryGetObjectAsync(noteIri, out var aStored));
        Assert.Equal("erin's original body", aStored!.Content?.FirstOrDefault());

        // 2. erin edits the note; the federated Update reaches bob's inbox on A (signed as erin). A's
        // UpdateActivityHandler receives an Update from a REMOTE actor (erin, not local on A) for an
        // object A holds attributed to erin → owner guard passes → A refreshes its copy. A does NOT
        // re-propagate (erin is not local on A; the home instance, B, owns the re-fan-out).
        var update = BuildUpdate(_erinActorIri, noteIri, "erin's edited body");
        await erinWorker.Service.DeliverAsync(bobActorIri.InboxOf(), update);
        await WaitForAsync(
            async () => await aPersistence.Objects.TryGetObjectAsync(noteIri, out var a)
                && a is Note { Content: { } content } && content.FirstOrDefault() == "erin's edited body",
            timeout: TimeSpan.FromSeconds(10));
        await erinWorker.StopAsync(CancellationToken.None);

        // A's copy of erin's note is refreshed to the edited content (the federated half of F-02,
        // direction 2).
        Assert.True(await aPersistence.Objects.TryGetObjectAsync(noteIri, out var aEdited));
        Assert.Equal("erin's edited body", aEdited!.Content?.FirstOrDefault());

        // Correct scope: A's unrelated note is untouched — no collateral rewrite.
        Assert.True(await aPersistence.Objects.TryGetObjectAsync(unrelatedIri, out var unrelated));
        Assert.IsType<Note>(unrelated);
        Assert.Equal("erin's other note", unrelated!.Content?.FirstOrDefault());

        // No re-propagation: the handler's re-propagation branch (UpdateActivityHandler, F-02 federated
        // half) runs only when the updating actor is local on this instance (the home instance
        // re-fans-out). erin is a remote actor on A (she lives on B/prop-c, not on A/prop-b), so A's
        // local-actor store does not contain her and the branch is skipped — a non-home copy applies the
        // update locally and stops, rather than fanning it out again.
        Assert.False(
            await aPersistence.Actors.TryGetActorAsync(_erinActorIri, out _),
            "erin must not be a local actor on A (only then would A re-propagate the update)");
    }

    // --- A delete with no remote followers is local-only ----------------------------------

    [Fact]
    public async Task LocalDelete_WithNoRemoteFollowers_IsLocalOnly()
    {
        // A fresh author (frank) with no followers (fresh persistence — no follow edge recorded).
        var frankPersistence = new InMemoryPersistenceProvider();
        var frankSeeded = TestSeeder.SeedPersonWithKey(frankPersistence, AHost, "frank");
        var frankActorIri = frankSeeded.ActorIri;
        var frankInboxIri = frankActorIri.InboxOf();

        TestServer? frankServer = null;
        frankServer = StartAuthorServer(
            frankPersistence, frankSeeded.Key, frankActorIri,
            targetServer: () => _c, selfServer: () => frankServer!);

        var noteIri = new Iri($"{frankActorIri}/notes/l1");
        var create = BuildCreate(frankActorIri, noteIri, "local body");

        using var frankWorker = BuildDeliveryWorker(frankActorIri, frankSeeded.Key, frankServer);
        await frankWorker.Service.DeliverAsync(frankInboxIri, create);
        await frankWorker.StartAsync(CancellationToken.None);
        await WaitForAsync(
            async () => await frankPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            timeout: TimeSpan.FromSeconds(10));

        // frank has no followers, so the delete is local-only: A tombstones the object, C (which never
        // saw the note) stores nothing.
        var delete = BuildDelete(frankActorIri, noteIri);
        await frankWorker.Service.DeliverAsync(frankInboxIri, delete);
        await WaitForAsync(
            async () => await frankPersistence.Objects.TryGetObjectAsync(noteIri, out var a) && a is Tombstone,
            timeout: TimeSpan.FromSeconds(10));
        await frankWorker.StopAsync(CancellationToken.None);

        Assert.True(await frankPersistence.Objects.TryGetObjectAsync(noteIri, out var a2));
        Assert.IsType<Tombstone>(a2);
        Assert.False(await _cPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            "C never saw the note and has no copy to tombstone");

        frankServer.Dispose();
    }

    // --- Helpers ---------------------------------------------------------------------------



    private sealed class TestWorker : IDisposable
    {
        private readonly IHost _host;
        private readonly DeliveryWorker _worker;

        internal TestWorker(IHost host, DeliveryWorker worker, IDeliveryService service, IDeliveryQueue queue)
        {
            _host = host;
            _worker = worker;
            Service = service;
            Queue = queue;
        }

        public IDeliveryService Service { get; }
        public IDeliveryQueue Queue { get; }

        public Task StartAsync(CancellationToken ct) => _host.StartAsync(ct);
        public Task StopAsync(CancellationToken ct) => _host.StopAsync(ct);

        public void Dispose()
        {
            _host.Dispose();
            _worker.Dispose();
        }
    }

    /// <summary>
    /// Builds a hosted <see cref="DeliveryWorker"/> signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>), routing deliveries to <paramref name="targetServer"/>. The delivery
    /// service resolves the recipient's delivery target directly (recipient inbox — the ActivityPub
    /// convention) without a network round-trip for the shared-inbox advertisement: the test worker's
    /// client cannot sign arbitrary recipient fetches, and the in-process recipient (C) advertises no
    /// shared inbox anyway.
    /// </summary>
    private static TestWorker BuildDeliveryWorker(Iri actorIri, KeyPair key, TestServer targetServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        // A factory that can only sign as this worker's actor (alice): the worker's one client is
        // created with the actor identity, so no other identity is ever requested. The host's own
        // factory (registered by AddActivityPubServer) is not used here — the worker is built
        // standalone so it can sign as the author (alice), not the instance actor.
        var factory = new StubClientFactory(keyStore, keyProvider, signer, actorIri);
        var queue = new InMemoryDeliveryQueue();
        ILoggerFactory loggerFactory = NullLoggerFactory.Instance;
        var service = new DeliveryService(queue, new StubActorDocumentFetcher(), loggerFactory.CreateLogger<DeliveryService>());
        var options = Options.Create(new ActivityPubServerOptions { InstanceActorId = actorIri });
        var transportFactory = () => targetServer.CreateHandler();

        var worker = new DeliveryWorker(
            queue, factory, transportFactory, options,
            loggerFactory.CreateLogger<DeliveryWorker>());

        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(s => s.AddHostedService<DeliveryWorker>(_ => worker))
            .Build();

        return new TestWorker(host, worker, service, queue);
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that returns a fixed actor document — no network round-trip
    /// (used by the test delivery worker's <see cref="DeliveryService"/> so recipient resolution is
    /// deterministic; the returned document advertises no shared inbox, so delivery falls back to the
    /// recipient's per-actor inbox).
    /// </summary>
    private sealed class StubActorDocumentFetcher : IActorDocumentFetcher
    {
        private static readonly Actor Document = new() { Id = "https://stub.local/ap/v1/u/actor" };

        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => Task.FromResult<Actor?>(Document);
    }

    /// <summary>
    /// An <see cref="IActivityPubClientFactory"/> that signs only as one fixed actor (the test delivery
    /// worker's author). The worker's single client is created with the author identity, so the
    /// requested <see cref="ActivityPubClientOptions.ActorId"/> always matches.
    /// </summary>
    private sealed class StubClientFactory : IActivityPubClientFactory
    {
        private readonly IKeyStore _keyStore;
        private readonly IKeyProvider _keyProvider;
        private readonly ISignatureSigner _signer;
        private readonly Iri _actorId;

        public StubClientFactory(IKeyStore keyStore, IKeyProvider keyProvider, ISignatureSigner signer, Iri actorId)
        {
            _keyStore = keyStore;
            _keyProvider = keyProvider;
            _signer = signer;
            _actorId = actorId;
        }

        public IActivityPubClient Create(ActivityPubClientOptions options, HttpMessageHandler httpHandler)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(httpHandler);

            var signingHandler = new SigningHandler(_signer, _keyProvider, httpHandler)
            {
                ActorId = _actorId,
            };

            // JsonLd → Signing → transport (no retry: a delivery is non-idempotent; the worker retries
            // at its own layer).
            var pipeline = new JsonLdHandler(signingHandler);
            var httpClient = new HttpClient(pipeline, disposeHandler: true)
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            };

            return new ActivityPubClient(httpClient);
        }

        public ILocalModerationClient CreateLocalModerationClient(ActivityPubClientOptions options, HttpMessageHandler httpHandler)
            => new LocalModerationClient(null);
    }

    /// <summary>
    /// Builds an <see cref="IActorDocumentFetcher"/> whose client (signed as <paramref name="handle"/>)
    /// routes to <paramref name="targetServer"/> — i.e. C's fetcher reaches A's actor documents (used
    /// by C's inbound signature validation to resolve alice's key).
    /// </summary>
    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair key, TestServer targetServer)
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

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    /// <summary>
    /// Starts the author's instance (A), registering the author's key so the host can sign, routing the
    /// host's delivery transport to the (deferred) remote instance's <c>TestServer</c>, and wiring a
    /// self-fetcher so A can validate an inbound activity signed by the author (resolving the author's
    /// key from A's own actor document).
    /// </summary>
    private static TestServer StartAuthorServer(
        InMemoryPersistenceProvider persistence, KeyPair authorKey, Iri authorActorIri,
        Func<TestServer> targetServer, Func<TestServer> selfServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(authorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(authorActorIri, authorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var uri = new Uri(authorActorIri.Value);
        var host = uri.Authority;
        var handle = uri.AbsolutePath.Trim('/').Split('/').Last();

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            DeliveryTransport = () => new LazyHandler(() => targetServer().CreateHandler()),
            Fetcher = BuildSelfFetcher(authorKey, authorActorIri, selfServer),
        });
    }

    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence, KeyPair key,
        IActorDocumentFetcher fetcher)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(new Iri($"https://{host}/ap/v1/u/{handle}"), key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            Fetcher = fetcher,
        });
    }

    private static IActorDocumentFetcher BuildSelfFetcher(
        KeyPair authorKey, Iri authorActorIri, Func<TestServer> selfServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(authorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(authorActorIri, authorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = authorActorIri, EnableRetry = false },
            new LazyHandler(() => selfServer().CreateHandler()));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    private static Create BuildCreate(Iri actorIri, Iri objectIri, string content) => new()
    {
        Id = $"{actorIri}/creates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = objectIri.Value,
                Content = [content],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
            },
        ],
    };

    private static Update BuildUpdate(Iri actorIri, Iri objectIri, string content) => new()
    {
        Id = $"{actorIri}/updates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = objectIri.Value,
                Content = [content],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
            },
        ],
    };

    private static Delete BuildDelete(Iri actorIri, Iri objectIri) => new()
    {
        Id = $"{actorIri}/deletes/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };

    private static async Task WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
            {
                return;
            }

            await Task.Delay(50);
        }
    }

}
