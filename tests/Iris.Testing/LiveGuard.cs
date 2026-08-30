namespace Iris.Testing;

/// <summary>
/// The runtime gate for the live-interop suite. Call <see cref="TryRequires"/> at the top of each
/// live test: when the suite is disabled (the <c>IRIS_LIVE_INTEROP</c> env var is not <c>"1"</c>) or
/// the FQDN is not configured, the method returns <c>false</c> and the test should **return early**
/// (a no-op, reported as passed — not failed). This is the C# analogue of
/// <c>docker-smoke-test.sh</c>'s <c>exit 0</c> when Docker is unavailable: the default <c>dotnet
/// test</c> run stays green (no failure, no contact with a live instance) and the live scenarios run
/// only when the operator has provisioned the FQDN and enabled the suite.
/// </summary>
public static class LiveGuard
{
    /// <summary>
    /// The skip message when the suite is disabled (master switch off).
    /// </summary>
    public const string DisabledMessage = "Live interop suite is disabled (IRIS_LIVE_INTEROP is not set to '1').";

    /// <summary>
    /// The skip message when the suite is enabled but the FQDN is not configured.
    /// </summary>
    public const string NoFqdnMessage = "Live interop suite is enabled but no base URI is configured (IRIS_LIVE_INTEROP_BASE_URI).";

    /// <summary>
    /// Tries to gate a live test on the <see cref="LiveInteropOptions"/>. Returns <c>true</c> (with
    /// the loaded options) when the suite can run; returns <c>false</c> when the test should return
    /// early (disabled or no FQDN).
    /// </summary>
    /// <param name="options">The loaded options (valid when the return value is <c>true</c>).</param>
    /// <returns><c>true</c> when the suite can run; <c>false</c> when the test should return early.</returns>
    public static bool TryRequires(out LiveInteropOptions options)
    {
        if (!LiveInteropOptions.TryLoadFromEnvironment(out options))
        {
            return false;
        }

        return options.CanRun;
    }
}
