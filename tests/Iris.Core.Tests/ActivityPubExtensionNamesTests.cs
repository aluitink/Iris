using Iris.Core;

namespace Iris.Core.Tests;

/// <summary>
/// Unit tests for the canonical wire names of the non-core-AP extension terms
/// (<see cref="ActivityPubExtensionNames"/>). These constants are the single source of truth shared by
/// the <c>Iris.Client</c> write/read path and the <c>Iris.Server</c> render/update path; the tests pin
/// the exact wire strings so a typo (which would silently break settings propagation on the wire) is
/// caught.
/// </summary>
public sealed class ActivityPubExtensionNamesTests
{
    [Fact]
    public void ManuallyApprovesFollowers_MatchesTheWireTerm()
    {
        // The un-prefixed, camelCase term the server echoes onto the public actor document and the
        // client builds into the settings Add/Remove object (J-10 / Resolved Decision #46).
        Assert.Equal("manuallyApprovesFollowers", ActivityPubExtensionNames.ManuallyApprovesFollowers);
    }

    [Fact]
    public void ManuallyApprovesMembers_MatchesTheWireTerm()
    {
        // The un-prefixed, camelCase term the server echoes onto the public group document and the
        // client builds into the community settings Add/Remove object (change 217).
        Assert.Equal("manuallyApprovesMembers", ActivityPubExtensionNames.ManuallyApprovesMembers);
    }
}
