namespace Iris.Testing;

/// <summary>
/// A third-party platform under test in the live-interop suite. Describes one target instance and
/// the credentials/secrets needed to drive it (via its admin API) and to assert against its state.
/// </summary>
/// <param name="Platform">The platform type.</param>
/// <param name="Name">A short, human-readable name for the target (used in test output).</param>
/// <param name="BaseUri">The base URI of the target instance (e.g. <c>https://mastodon.example.org</c>).</param>
/// <param name="SeedAccounts">The handles to resolve on the target (from ENUMERATION_DESIGN.md).</param>
/// <param name="AdminApiBase">The base URI of the target's admin API (for creating test accounts/posts/follows).</param>
/// <param name="AdminToken">The admin API token (secret; never logged).</param>
public sealed record InteropTarget(
    InteropPlatform Platform,
    string Name,
    Iri BaseUri,
    IReadOnlyList<string> SeedAccounts,
    Iri AdminApiBase,
    string AdminToken)
{
    /// <summary>
    /// The display name for this target, including the platform.
    /// </summary>
    public string DisplayName => $"{Platform}:{Name}";
}
