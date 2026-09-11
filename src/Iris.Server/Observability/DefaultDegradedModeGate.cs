namespace Iris.Server.Observability;

/// <summary>
/// The default <see cref="IDegradedModeGate"/>: a thread-safe, stateful flag flipped by the
/// <see cref="PersistenceDegradedModeProbe"/>.
/// </summary>
/// <remarks>
/// The flag is a single <see cref="int"/> read/written with <see cref="Volatile"/> semantics:
/// <c>0</c> = read-write, <c>1</c> = degraded (read-only). The write paths (the inbound/outbox
/// handlers) read <see cref="IsDegraded"/> once per request and, when degraded, refuse the write with
/// <c>503</c> before touching the store — so a torn read of the flag is benign (the request either sees
/// the pre-flip or post-flip value, both of which are a consistent decision for that one request).
/// </remarks>
public sealed class DefaultDegradedModeGate : IDegradedModeGate
{
    private const int ReadWrite = 0;
    private const int Degraded = 1;

    private int _state;

    /// <inheritdoc/>
    public bool IsDegraded => Volatile.Read(ref _state) == Degraded;

    /// <inheritdoc/>
    public void MarkDegraded() => Volatile.Write(ref _state, Degraded);

    /// <inheritdoc/>
    public void ClearDegraded() => Volatile.Write(ref _state, ReadWrite);
}
