namespace Iris.Testing;

/// <summary>
/// Centralizes the xunit trait <em>names</em> used to partition the test suite, so the literals are
/// defined once instead of being scattered across test files.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Category"/> trait partitions tests by a coarse category. The <see cref="Slow"/>
/// value marks tests that wait out a real delivery backoff budget (a non-zero
/// <see cref="Iris.Server.Delivery.DeliveryRetryOptions.BaseDelay"/>) or other wall-clock delay, so
/// they are materially slower than the rest of the suite.
/// </para>
/// <para>
/// Usage: apply <c>[Trait(TestCategories.Category, TestCategories.Slow)]</c> to a slow test or class.
/// The everyday "fast" run excludes them with
/// <c>dotnet test --trait "Category!=Slow"</c>; the full suite is the plain <c>dotnet test</c>. See
/// <c>docs/reference/TESTING.md</c> for the exact commands.
/// </para>
/// </remarks>
public static class TestCategories
{
    /// <summary>
    /// The xunit trait key for the coarse test category.
    /// </summary>
    public const string Category = "Category";

    /// <summary>
    /// The category value for tests that wait out a real backoff / wall-clock delay.
    /// </summary>
    public const string Slow = "Slow";
}
