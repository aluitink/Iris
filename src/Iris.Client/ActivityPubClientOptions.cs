using System;
using Iris.Core;

namespace Iris.Client;

/// <summary>
/// Options for an <see cref="ActivityPubClient"/>.
/// </summary>
public sealed class ActivityPubClientOptions
{
    /// <summary>
    /// Gets or sets the actor IRI the client signs as. Required for signed requests.
    /// </summary>
    public Iri? ActorId { get; set; }

    /// <summary>
    /// Gets or sets the HTTP client timeout. Defaults to <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </summary>
    public TimeSpan? HttpClientTimeout { get; set; }

    /// <summary>
    /// Gets or sets the optional client-side caches (actor / collection-page reads). When null the
    /// client goes straight to the network for reads (no caching).
    /// </summary>
    public ClientCaches? Caches { get; set; }

    /// <summary>
    /// Gets or sets whether the <see cref="RetryHandler"/> is included in the pipeline. Defaults to
    /// true.
    /// </summary>
    public bool EnableRetry { get; set; } = true;

    /// <summary>
    /// Gets or sets the total number of attempts (including the first) the
    /// <see cref="RetryHandler"/> will make for idempotent requests. Defaults to
    /// <see cref="RetryHandler.DefaultMaxAttempts"/> (3).
    /// </summary>
    public int MaxRetryAttempts { get; set; } = RetryHandler.DefaultMaxAttempts;
}
