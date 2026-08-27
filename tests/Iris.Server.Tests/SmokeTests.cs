using Iris.Testing;

namespace Iris.Server.Tests;

/// <summary>
/// Trivial smoke test proving the Iris.Server.Tests project compiles and runs against a
/// live two-instance <see cref="Iris.Testing.FederationTopology"/>.
/// Real federation integration tests (follow/accept, signature validation, WebFinger) arrive in Phases 3–4.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void Server_Project_FederatesTwoInstances()
    {
        using var topology = FederationTopology.Create(2);

        Assert.Equal("a.domain.local", topology.InstanceA.Hostname);
        Assert.Equal("b.domain.local", topology.InstanceB.Hostname);
    }
}
