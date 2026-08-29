using System;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Server.Tests.Delivery;

/// <summary>
/// Unit tests for the outbound delivery infrastructure: the in-memory
/// <see cref="InMemoryDeliveryQueue"/> (a bounded <c>Channel</c>) and the <see cref="DeliveryService"/>
/// (enqueues a <see cref="DeliveryJob"/>; resolves a recipient actor's inbox or advertised shared inbox).
/// These exercise the pure enqueue/dispatch logic — no HTTP; the over-the-wire delivery is covered by the
/// integration tests.
/// </summary>
public sealed class DeliveryQueueAndServiceTests
{
    private readonly Iri InboxIri = new("https://a.domain.local/ap/v1/u/alice/inbox");
    private readonly Iri ActorIri = new("https://a.domain.local/ap/v1/u/alice");

    // --- Queue: enqueue then dequeue returns the same job --------------------------

    [Fact]
    public async Task Queue_EnqueueThenDequeue_ReturnsSameJob()
    {
        var queue = new InMemoryDeliveryQueue();
        var job = new DeliveryJob(InboxIri, BuildCreate("n-1"));

        await queue.EnqueueAsync(job);
        var dequeued = await queue.TryDequeueAsync();

        Assert.Equal(job, dequeued);
    }

    // --- Queue: FIFO ordering is preserved -----------------------------------------

    [Fact]
    public async Task Queue_MultipleEnqueues_DequeueInFifoOrder()
    {
        var queue = new InMemoryDeliveryQueue();
        DeliveryJob[] jobs =
        [
            new(InboxIri, BuildCreate("a")),
            new(InboxIri, BuildCreate("b")),
            new(InboxIri, BuildCreate("c")),
        ];

        foreach (var job in jobs)
        {
            await queue.EnqueueAsync(job);
        }

        Assert.Equal(3, queue.Count);
        Assert.Equal("a", (await queue.TryDequeueAsync())!.Activity.Id);
        Assert.Equal("b", (await queue.TryDequeueAsync())!.Activity.Id);
        Assert.Equal("c", (await queue.TryDequeueAsync())!.Activity.Id);
        Assert.Equal(0, queue.Count);
    }

    // --- Queue: CompleteAsync then drain returns null -------------------------------

    [Fact]
    public async Task Queue_CompletedAndDrained_TryDequeueReturnsNull()
    {
        var queue = new InMemoryDeliveryQueue();
        await queue.EnqueueAsync(new DeliveryJob(InboxIri, BuildCreate("x")));

        // One item is still deliverable before the completion is observed.
        Assert.NotNull(await queue.TryDequeueAsync());

        await queue.CompleteAsync();

        // Queue is complete and empty → null (a worker can shut down).
        Assert.Null(await queue.TryDequeueAsync());
    }

    // --- Queue: CompleteAsync with a pending item still yields it --------------------

    [Fact]
    public async Task Queue_CompletedWithPendingItem_StillYieldsItThenNull()
    {
        var queue = new InMemoryDeliveryQueue();
        await queue.EnqueueAsync(new DeliveryJob(InboxIri, BuildCreate("pending")));
        await queue.CompleteAsync();

        // The pending item is delivered (completion does not drop in-flight work).
        Assert.Equal("pending", (await queue.TryDequeueAsync())!.Activity.Id);
        // Now complete and drained → null.
        Assert.Null(await queue.TryDequeueAsync());
    }

    // --- Queue: null job throws ------------------------------------------------------

    [Fact]
    public async Task Queue_EnqueueNull_Throws()
    {
        var queue = new InMemoryDeliveryQueue();
        await Assert.ThrowsAsync<ArgumentNullException>(() => queue.EnqueueAsync(null!));
    }

    // --- Queue: invalid capacity throws ---------------------------------------------

    [Fact]
    public void Queue_ZeroCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryDeliveryQueue(0));
    }

    // --- Service: DeliverAsync enqueues a job for the given inbox --------------------

    [Fact]
    public async Task Service_DeliverAsync_EnqueuesJobForGivenInbox()
    {
        var queue = new InMemoryDeliveryQueue();
        var service = new DeliveryService(queue, NullLogger<DeliveryService>.Instance);
        var note = BuildCreate("deliver-1");

        await service.DeliverAsync(InboxIri, note);

        var job = await queue.TryDequeueAsync();
        Assert.NotNull(job);
        Assert.Equal(InboxIri, job!.InboxIri);
        Assert.Equal(note, job.Activity);
    }

    // --- Service: DeliverToActorAsync derives the inbox from the actor IRI -----------

    [Fact]
    public async Task Service_DeliverToActorAsync_DerivesInboxFromActorIri()
    {
        var queue = new InMemoryDeliveryQueue();
        var service = new DeliveryService(queue, NullLogger<DeliveryService>.Instance);

        await service.DeliverToActorAsync(ActorIri, BuildCreate("actor-1"));

        var job = await queue.TryDequeueAsync();
        Assert.NotNull(job);
        // The inbox is the actor IRI + "/inbox" (the ActivityPub convention).
        Assert.Equal(ActorIri.InboxOf(), job!.InboxIri);
    }

    // --- Service: DeliverAsync with an actor threads it onto the job -----------------

    [Fact]
    public async Task Service_DeliverAsync_WithActor_ThreadsActorOntoJob()
    {
        var queue = new InMemoryDeliveryQueue();
        var service = new DeliveryService(queue, NullLogger<DeliveryService>.Instance);
        var note = BuildCreate("deliver-actor");

        await service.DeliverAsync(InboxIri, note, ActorIri);

        var job = await queue.TryDequeueAsync();
        Assert.NotNull(job);
        Assert.Equal(InboxIri, job!.InboxIri);
        Assert.Equal(note, job.Activity);
        // The acting actor is recorded so the worker signs the delivery as that actor.
        Assert.Equal(ActorIri, job.ActorIri);
    }

    // --- Service: DeliverToActorAsync with an actor threads it onto the job ----------

    [Fact]
    public async Task Service_DeliverToActorAsync_WithActor_ThreadsActorOntoJob()
    {
        var queue = new InMemoryDeliveryQueue();
        var service = new DeliveryService(queue, NullLogger<DeliveryService>.Instance);

        await service.DeliverToActorAsync(ActorIri, BuildCreate("actor-2"), ActorIri);

        var job = await queue.TryDequeueAsync();
        Assert.NotNull(job);
        Assert.Equal(ActorIri.InboxOf(), job!.InboxIri);
        Assert.Equal(ActorIri, job.ActorIri);
    }

    // --- Service: the instance-actor overload leaves the job's actor null -------------

    [Fact]
    public async Task Service_DeliverAsync_InstanceActor_LeavesJobActorNull()
    {
        var queue = new InMemoryDeliveryQueue();
        var service = new DeliveryService(queue, NullLogger<DeliveryService>.Instance);

        await service.DeliverAsync(InboxIri, BuildCreate("deliver-instance"));

        var job = await queue.TryDequeueAsync();
        Assert.NotNull(job);
        // A null actor means "sign as the instance actor" (the system key for automated events).
        Assert.Null(job!.ActorIri);
    }

    // --- Service: relative (non-absolute) inbox IRI throws ---------------------------

    [Fact]
    public async Task Service_DeliverAsync_RelativeInboxIri_Throws()
    {
        var queue = new InMemoryDeliveryQueue();
        var service = new DeliveryService(queue, NullLogger<DeliveryService>.Instance);
        var relative = new Iri("/u/alice/inbox");

        await Assert.ThrowsAsync<ArgumentException>(() => service.DeliverAsync(relative, BuildCreate("rel")));
    }

    // --- Service: null activity throws ------------------------------------------------

    [Fact]
    public async Task Service_DeliverAsync_NullActivity_Throws()
    {
        var queue = new InMemoryDeliveryQueue();
        var service = new DeliveryService(queue, NullLogger<DeliveryService>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.DeliverAsync(InboxIri, null!));
    }

    // --- Guards: null dependencies throw ---------------------------------------------

    [Fact]
    public void Service_NullQueue_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DeliveryService(null!, NullLogger<DeliveryService>.Instance));
    }

    [Fact]
    public void Service_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DeliveryService(new InMemoryDeliveryQueue(), null!));
    }

    // --- Service: DeliverToActorAsync honors the recipient's advertised shared inbox (F-01) --

    [Fact]
    public async Task Service_DeliverToActorAsync_SharedInboxAdvertised_DeliversToSharedInbox()
    {
        var queue = new InMemoryDeliveryQueue();
        var fetcher = new FixedActorDocumentFetcher(new Person
        {
            Id = ActorIri.Value,
            Endpoints = new Endpoints { SharedInbox = new Uri("https://a.domain.local/ap/v1/shared-inbox") },
        });
        var service = new DeliveryService(queue, fetcher, NullLogger<DeliveryService>.Instance);

        await service.DeliverToActorAsync(ActorIri, BuildCreate("shared-1"));

        var job = await queue.TryDequeueAsync();
        Assert.NotNull(job);
        // The recipient advertises a shared inbox → deliver to it (not the per-actor inbox).
        Assert.Equal(new Iri("https://a.domain.local/ap/v1/shared-inbox"), job!.InboxIri);
    }

    [Fact]
    public async Task Service_DeliverToActorAsync_NoSharedInbox_FallsBackToActorInbox()
    {
        var queue = new InMemoryDeliveryQueue();
        // The remote actor's document is present but advertises no endpoints/sharedInbox.
        var fetcher = new FixedActorDocumentFetcher(new Person { Id = ActorIri.Value });
        var service = new DeliveryService(queue, fetcher, NullLogger<DeliveryService>.Instance);

        await service.DeliverToActorAsync(ActorIri, BuildCreate("fallback-1"));

        var job = await queue.TryDequeueAsync();
        Assert.NotNull(job);
        Assert.Equal(ActorIri.InboxOf(), job!.InboxIri);
    }

    [Fact]
    public async Task Service_DeliverToActorAsync_FetchFails_FallsBackToActorInbox()
    {
        var queue = new InMemoryDeliveryQueue();
        // The fetcher returns null (fetch failed / not an actor) → fall back to the per-actor inbox.
        var fetcher = new FixedActorDocumentFetcher(null);
        var service = new DeliveryService(queue, fetcher, NullLogger<DeliveryService>.Instance);

        await service.DeliverToActorAsync(ActorIri, BuildCreate("fetchfail-1"));

        var job = await queue.TryDequeueAsync();
        Assert.NotNull(job);
        Assert.Equal(ActorIri.InboxOf(), job!.InboxIri);
    }

    [Fact]
    public async Task Service_DeliverToActorAsync_SharedInbox_WithActor_ThreadsActorAndSharedInbox()
    {
        var queue = new InMemoryDeliveryQueue();
        var fetcher = new FixedActorDocumentFetcher(new Person
        {
            Id = ActorIri.Value,
            Endpoints = new Endpoints { SharedInbox = new Uri("https://a.domain.local/ap/v1/shared-inbox") },
        });
        var service = new DeliveryService(queue, fetcher, NullLogger<DeliveryService>.Instance);

        await service.DeliverToActorAsync(ActorIri, BuildCreate("shared-actor"), ActorIri);

        var job = await queue.TryDequeueAsync();
        Assert.NotNull(job);
        Assert.Equal(new Iri("https://a.domain.local/ap/v1/shared-inbox"), job!.InboxIri);
        Assert.Equal(ActorIri, job.ActorIri);
    }

    // --- Service: block suppression (F-07: apply the block edge) ----------------------

    [Fact]
    public async Task Service_DeliverToActorAsync_RecipientBlockedSigner_SuppressesDelivery()
    {
        // The recipient (bob) blocked the signing actor (alice): the block edge bob → alice means bob
        // does not want content from alice, so the delivery is suppressed (no job enqueued).
        var queue = new InMemoryDeliveryQueue();
        var moderation = new InMemoryModerationStore();
        var bob = new Iri("https://b.domain.local/ap/v1/u/bob");
        await moderation.RecordBlockAsync(bob, ActorIri);
        var service = new DeliveryService(queue, null, moderation, NullLogger<DeliveryService>.Instance);

        await service.DeliverToActorAsync(bob, BuildCreate("blocked-1"), ActorIri);

        // The queue is empty (no job was enqueued). The bounded queue's TryDequeueAsync blocks when
        // empty, so emptiness is asserted via Count.
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Service_DeliverToActorAsync_NoBlock_DeliversNormally()
    {
        // No block edge: the delivery is enqueued (the moderation store is present but empty).
        var queue = new InMemoryDeliveryQueue();
        var moderation = new InMemoryModerationStore();
        var bob = new Iri("https://b.domain.local/ap/v1/u/bob");
        var service = new DeliveryService(queue, null, moderation, NullLogger<DeliveryService>.Instance);

        await service.DeliverToActorAsync(bob, BuildCreate("ok-1"), ActorIri);

        var job = await queue.TryDequeueAsync();
        Assert.NotNull(job);
        Assert.Equal(bob.InboxOf(), job!.InboxIri);
        Assert.Equal(ActorIri, job.ActorIri);
    }

    [Fact]
    public async Task Service_DeliverToActorAsync_InstanceActor_SkipsBlockCheck()
    {
        // A delivery signed as the instance actor (actorIri is null) is never suppressed, even when the
        // recipient has blocked the instance actor (the instance actor is not the blocked party).
        var queue = new InMemoryDeliveryQueue();
        var moderation = new InMemoryModerationStore();
        var bob = new Iri("https://b.domain.local/ap/v1/u/bob");
        await moderation.RecordBlockAsync(bob, ActorIri); // bob blocked alice (the instance actor here).
        var service = new DeliveryService(queue, null, moderation, NullLogger<DeliveryService>.Instance);

        await service.DeliverToActorAsync(bob, BuildCreate("instance-1")); // no acting actor → instance actor

        var job = await queue.TryDequeueAsync();
        Assert.NotNull(job);
        Assert.Null(job!.ActorIri);
    }

    [Fact]
    public async Task Service_DeliverToActorAsync_NoModerationStore_NeverSuppresses()
    {
        // Without a moderation store (moderation disabled), a delivery is never suppressed (pre-F-07).
        var queue = new InMemoryDeliveryQueue();
        var bob = new Iri("https://b.domain.local/ap/v1/u/bob");
        var service = new DeliveryService(queue, null, NullLogger<DeliveryService>.Instance);

        await service.DeliverToActorAsync(bob, BuildCreate("nomod-1"), ActorIri);

        Assert.NotNull(await queue.TryDequeueAsync());
    }

    // --- Helpers ----------------------------------------------------------------------

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that returns a fixed actor (or null) for every request —
    /// the seam <see cref="DeliveryService"/> uses to resolve a recipient's advertised shared inbox
    /// (F-01) without HTTP.
    /// </summary>
    private sealed class FixedActorDocumentFetcher(Actor? actor) : IActorDocumentFetcher
    {
        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => Task.FromResult(actor);
    }

    /// <summary>
    /// Builds a <see cref="Create"/> activity (a real <see cref="Activity"/>) wrapping a
    /// <see cref="Note"/>, with a deterministic IRI — used as the payload for delivery jobs.
    /// </summary>
    private static Activity BuildCreate(string id) => new Create
    {
        Id = id,
        Object = [new Note { Id = $"object-{id}", Content = [$"note {id}"] }],
    };
}
