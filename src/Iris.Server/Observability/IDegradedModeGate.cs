namespace Iris.Server.Observability;

/// <summary>
/// Reports whether the instance is running in a degraded (read-only) mode.
/// </summary>
/// <remarks>
/// A degraded instance is one whose durable store is unreachable: it still serves <em>reads</em>
/// (actor documents, collections, feeds) but refuses <em>writes</em> (inbound + outbound federation
/// activities) with <c>503 Service Unavailable</c> rather than throwing, so a partially-down instance
/// degrades gracefully instead of returning 500s on every mutation.
///
/// The gate is <em>stateful</em> (it holds the current degraded state), distinct from the
/// <see cref="IReadinessGate"/> (a per-request readiness decision). It is flipped by the
/// <see cref="PersistenceDegradedModeProbe"/> hosted service: on a failed persistence read at startup
/// (or a later re-probe) it is set to degraded, and on a successful re-probe it is cleared (the instance
/// recovers to read-write). A host that manages its own degraded-state detection may bind its own
/// <see cref="IDegradedModeGate"/> (via an override registration); the default is flipped only by the
/// built-in probe.
/// </remarks>
public interface IDegradedModeGate
{
    /// <summary>
    /// Gets a value indicating whether the instance is currently in degraded (read-only) mode.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the durable store is unreachable and writes are refused; otherwise
    /// <see langword="false"/> (the instance is read-write).
    /// </returns>
    bool IsDegraded { get; }

    /// <summary>
    /// Enters degraded (read-only) mode: subsequent <see cref="IsDegraded"/> reads report
    /// <see langword="true"/> until <see cref="ClearDegraded"/> is called.
    /// </summary>
    void MarkDegraded();

    /// <summary>
    /// Exits degraded mode (the instance recovers to read-write): subsequent <see cref="IsDegraded"/>
    /// reads report <see langword="false"/>.
    /// </summary>
    void ClearDegraded();
}
