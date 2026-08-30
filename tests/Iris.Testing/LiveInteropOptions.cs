namespace Iris.Testing;

/// <summary>
/// The configuration for the live-interop suite. Read from environment variables (see
/// <see cref="TryLoadFromEnvironment"/>). When <see cref="IsEnabled"/> is <c>false</c> or
/// <see cref="OurBaseUri"/> is null, the suite skips (not fails).
/// </summary>
/// <param name="IsEnabled">Whether the live-interop suite is enabled (the <c>IRIS_LIVE_INTEROP=1</c> master switch).</param>
/// <param name="OurBaseUri">The base URI of our Iris instance (the operator-provided FQDN).</param>
/// <param name="OurActorIri">The IRI of our test actor on the Iris instance.</param>
/// <param name="OurUsername">The Basic-auth username for our test actor (for local operations).</param>
/// <param name="OurPassword">The Basic-auth password for our test actor (secret; never logged).</param>
/// <param name="Targets">The third-party targets to test against.</param>
/// <param name="RequestBudget">The maximum number of requests the suite may make to a single target in one run (a runaway guard).</param>
/// <param name="RateLimitPerSecond">The maximum requests per second to a single target (a runaway guard).</param>
public sealed record LiveInteropOptions(
    bool IsEnabled,
    Iri? OurBaseUri,
    Iri? OurActorIri,
    string? OurUsername,
    string? OurPassword,
    IReadOnlyList<InteropTarget> Targets,
    int RequestBudget,
    int RateLimitPerSecond)
{
    /// <summary>
    /// The environment variable name for the master switch.
    /// </summary>
    public const string EnabledEnvVar = "IRIS_LIVE_INTEROP";

    /// <summary>
    /// The environment variable name for our base URI.
    /// </summary>
    public const string OurBaseUriEnvVar = "IRIS_LIVE_INTEROP_BASE_URI";

    /// <summary>
    /// The environment variable name for our actor IRI.
    /// </summary>
    public const string OurActorIriEnvVar = "IRIS_LIVE_INTEROP_ACTOR";

    /// <summary>
    /// The environment variable name for our Basic-auth username.
    /// </summary>
    public const string OurUsernameEnvVar = "IRIS_LIVE_INTEROP_USERNAME";

    /// <summary>
    /// The environment variable name for our Basic-auth password.
    /// </summary>
    public const string OurPasswordEnvVar = "IRIS_LIVE_INTEROP_PASSWORD";

    /// <summary>
    /// The environment variable name for the request budget (default 100).
    /// </summary>
    public const string RequestBudgetEnvVar = "IRIS_LIVE_INTEROP_REQUEST_BUDGET";

    /// <summary>
    /// The environment variable name for the rate limit (default 10 req/s).
    /// </summary>
    public const string RateLimitEnvVar = "IRIS_LIVE_INTEROP_RATE_LIMIT";

    /// <summary>
    /// The default request budget when not configured.
    /// </summary>
    public const int DefaultRequestBudget = 100;

    /// <summary>
    /// The default rate limit (requests per second) when not configured.
    /// </summary>
    public const int DefaultRateLimitPerSecond = 10;

    /// <summary>
    /// Tries to load <see cref="LiveInteropOptions"/> from environment variables. Returns
    /// <c>false</c> when the master switch (<c>IRIS_LIVE_INTEROP</c>) is not set to <c>"1"</c> or
    /// <c>"true"</c> — the suite is disabled and should skip.
    /// </summary>
    /// <param name="options">The loaded options (valid when the return value is <c>true</c>).</param>
    /// <returns><c>true</c> when the suite is enabled; <c>false</c> when it should skip.</returns>
    public static bool TryLoadFromEnvironment(out LiveInteropOptions options)
    {
        var enabled = Environment.GetEnvironmentVariable(EnabledEnvVar) is "1" or "true";
        if (!enabled)
        {
            options = new LiveInteropOptions(
                IsEnabled: false,
                OurBaseUri: null,
                OurActorIri: null,
                OurUsername: null,
                OurPassword: null,
                Targets: [],
                RequestBudget: DefaultRequestBudget,
                RateLimitPerSecond: DefaultRateLimitPerSecond);
            return false;
        }

        var baseUri = Environment.GetEnvironmentVariable(OurBaseUriEnvVar);
        var actorIri = Environment.GetEnvironmentVariable(OurActorIriEnvVar);
        var username = Environment.GetEnvironmentVariable(OurUsernameEnvVar);
        var password = Environment.GetEnvironmentVariable(OurPasswordEnvVar);
        var budget = int.TryParse(Environment.GetEnvironmentVariable(RequestBudgetEnvVar), out var b) ? b : DefaultRequestBudget;
        var rateLimit = int.TryParse(Environment.GetEnvironmentVariable(RateLimitEnvVar), out var r) ? r : DefaultRateLimitPerSecond;

        options = new LiveInteropOptions(
            IsEnabled: true,
            OurBaseUri: baseUri is not null ? new Iri(baseUri) : null,
            OurActorIri: actorIri is not null ? new Iri(actorIri) : null,
            OurUsername: username,
            OurPassword: password,
            Targets: LoadTargetsFromEnvironment(),
            RequestBudget: budget,
            RateLimitPerSecond: rateLimit);
        return true;
    }

    /// <summary>
    /// Whether the suite is enabled and has a configured base URI (the minimum to run).
    /// </summary>
    public bool CanRun => IsEnabled && OurBaseUri is not null;

    private static IReadOnlyList<InteropTarget> LoadTargetsFromEnvironment()
    {
        // Targets are configured one-per-env-var-pair: IRIS_LIVE_INTEROP_TARGET_{N}_BASE_URI, etc.
        // For the initial implementation, targets are empty (the Phase 13 payload fills them in).
        // This is the "fill in targets" seam the design describes.
        return [];
    }
}
