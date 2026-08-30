using Xunit;

namespace Iris.LiveInterop.Tests;

/// <summary>
/// The live-interop scenario tests (the Phase 13 payload). Each test calls
/// <see cref="LiveGuard.TryRequires"/> at the top; when the suite is disabled (the default), the test
/// returns early (a no-op, reported as passed — not failed). When the operator has provisioned the
/// FQDN and set <c>IRIS_LIVE_INTEROP=1</c>, the test runs the scenario against the real instance.
///
/// These tests are the "fill in targets" seam: the scenario logic is stubbed here (the <c>Assert.Fail</c>
/// placeholder) and the Phase 13 work is to replace the stubs with the real scenario drivers (using
/// the <c>LiveInteropOptions</c> targets + per-platform admin-API adapters).
/// </summary>
public sealed class LiveScenarioTests
{
    // --- F1: Our actor follows a platform actor -------------------------------------------------

    [Fact]
    public async Task F1_OurActorFollowsPlatformActor_EdgeRecordedAndAcceptDelivered()
    {
        if (!LiveGuard.TryRequires(out var options))
        {
            return; // Suite disabled or no FQDN — skip (no-op).
        }

        // Phase 13 payload: drive our actor to follow a platform actor (via the platform's admin API
        // or by resolving the actor's IRI), wait, and assert our AcceptActivityHandler recorded the
        // edge and an Accept was delivered to the platform inbox.
        Assert.Fail("Phase 13 payload not yet implemented — F1 scenario stub.");
    }

    // --- C1: Remote follower's inbox receives a signed Create ------------------------------------

    [Fact]
    public async Task C1_RemoteFollowerInboxReceivesSignedCreate()
    {
        if (!LiveGuard.TryRequires(out var options))
        {
            return;
        }

        // Phase 13 payload: post a Create as our actor and assert the remote follower's inbox
        // received a signed Create.
        Assert.Fail("Phase 13 payload not yet implemented — C1 scenario stub.");
    }

    // --- SIG1: Server-to-server signature compatibility ------------------------------------------

    [Fact]
    public async Task SIG1_ServerToServerSignatureCompatibility()
    {
        if (!LiveGuard.TryRequires(out var options))
        {
            return;
        }

        // Phase 13 payload: verify that a signed request from our instance to the platform (and vice
        // versa) is accepted (202) — the signature profile, digest, and date headers are compatible.
        Assert.Fail("Phase 13 payload not yet implemented — SIG1 scenario stub.");
    }

    // --- P1: Pagination compatibility ------------------------------------------------------------

    [Fact]
    public async Task P1_PaginationCompatibility()
    {
        if (!LiveGuard.TryRequires(out var options))
        {
            return;
        }

        // Phase 13 payload: enumerate a collection (e.g. the platform actor's outbox) and verify the
        // pagination (OrderedCollectionPage, next/prev links) is compatible.
        Assert.Fail("Phase 13 payload not yet implemented — P1 scenario stub.");
    }
}
