namespace Iris.Server.Observability;

/// <summary>
/// Collects <see cref="FederationTraceEntry"/> observations of inbound and outbound federation
/// requests (Phase 136.1).
/// </summary>
/// <remarks>
/// The default implementation (<see cref="InMemoryFederationTraceCollector"/>) is a bounded, in-memory
/// ring buffer registered as a DI singleton. A host (or test) reads <see cref="Snapshot"/> to obtain
/// the captured entries for the current scenario; an operator can then export that snapshot as a
/// single shared trace artifact (the Phase 136.1 exit criterion — "a baseline run can produce a
/// single shared trace artifact per scenario"). A host wanting durable capture can rebind this
/// interface to a file- or log-backed collector without changing the call sites.
/// </remarks>
public interface IFederationTraceCollector
{
    /// <summary>
    /// Records a federation request/response observation. This is a no-op for a collector whose buffer
    /// is disabled (capacity 0); callers never need to null-check the collector's state.
    /// </summary>
    /// <param name="entry">The observed entry. Must not be null.</param>
    void Record(FederationTraceEntry entry);

    /// <summary>
    /// Returns a point-in-time, ordered (oldest-first) snapshot of the captured entries. The snapshot is
    /// a copy: subsequent <see cref="Record"/> calls do not affect it. An empty array when nothing has
    /// been captured (or the buffer is disabled).
    /// </summary>
    IReadOnlyList<FederationTraceEntry> Snapshot();

    /// <summary>
    /// Clears the captured entries (e.g. at the start of a scenario so its trace contains only its own
    /// traffic).
    /// </summary>
    void Clear();
}
