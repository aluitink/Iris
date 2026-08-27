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
}
