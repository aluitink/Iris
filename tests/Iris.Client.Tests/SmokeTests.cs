using Iris.Testing;

namespace Iris.Client.Tests;

/// <summary>
/// Trivial smoke test proving the Iris.Client.Tests project compiles and runs against a
/// live in-process <see cref="Iris.Testing.TestServerInstance"/>.
/// Real client integration tests (auth flow, discovery, paging, caching) arrive in Phase 2.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void Client_Project_RunsAgainstLiveTestServer()
    {
        using var instance = TestServerFactory.CreateInstance("a.domain.local");

        Assert.Equal("a.domain.local", instance.Hostname);
    }
}
