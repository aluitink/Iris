using Microsoft.Extensions.DependencyInjection;

namespace Iris.Testing;

/// <summary>
/// Smoke tests that prove the multi-instance <see cref="FederationTopology"/> harness is
/// wired correctly: distinct hostnames, real in-process HTTP, and a resolvable store.
/// These run as part of the Iris.Testing project so the harness is exercised on every build.
/// </summary>
public class HarnessSmokeTests
{
    [Fact]
    public async Task CreateInstance_SingleInstance_ResolvesStoreAndBaseUri()
    {
        using var instance = TestServerFactory.CreateInstance("a.domain.local");

        Assert.Equal("a.domain.local", instance.Hostname);
        Assert.Equal(new Uri("https://a.domain.local/"), instance.BaseUri);
        Assert.Equal(new Uri("https://a.domain.local/u/alice"), instance.ActorIri);

        var store = instance.Services.GetRequiredService<IHarnessStore>();
        Assert.Equal("a.domain.local", store.Hostname);
    }

    [Fact]
    public void CreateTopology_TwoInstances_HaveDistinctHostnames()
    {
        using var topology = FederationTopology.Create(2);

        Assert.Equal(2, topology.Instances.Count);
        Assert.Equal("a.domain.local", topology.InstanceA.Hostname);
        Assert.Equal("b.domain.local", topology.InstanceB.Hostname);
        Assert.NotEqual(topology.InstanceA.BaseUri, topology.InstanceB.BaseUri);
    }

    [Fact]
    public void CreateTopology_ThreeInstances_ScalesToN()
    {
        using var topology = FederationTopology.Create(3);

        Assert.Equal(3, topology.Instances.Count);
        Assert.Equal("a.domain.local", topology.Instances[0].Hostname);
        Assert.Equal("b.domain.local", topology.Instances[1].Hostname);
        Assert.Equal("c.domain.local", topology.Instances[2].Hostname);
    }

    [Fact]
    public async Task HttpClient_IsWiredToInProcessServer_ReturnsResponse()
    {
        using var instance = TestServerFactory.CreateInstance("a.domain.local");

        // No endpoints are mapped yet (Phase 3), so any path yields a 404 — which proves the
        // request traversed the real in-process HTTP stack and got a well-formed response.
        var response = await instance.HttpClient.GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(response.Content.Headers);
    }
}
