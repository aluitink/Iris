using CoreIris = Iris.Core.Iris;

namespace Iris.Core.Tests;

/// <summary>
/// Trivial smoke test proving the Iris.Core.Tests project compiles and runs.
/// Real IRI / key / signature / cache unit tests arrive in Phase 1.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void Iris_Core_Assembly_ExposesVersion()
    {
        Assert.Equal("1.0.0", CoreIris.Version);
    }
}
