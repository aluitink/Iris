namespace Iris.Testing;

/// <summary>
/// The platform under test in a live-interop scenario.
/// </summary>
public enum InteropPlatform
{
    /// <summary>A Mastodon instance.</summary>
    Mastodon,

    /// <summary>A Lemmy instance.</summary>
    Lemmy,

    /// <summary>A Pleroma/Akkoma instance.</summary>
    Pleroma,

    /// <summary>A Threads instance.</summary>
    Threads,
}
