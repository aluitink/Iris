namespace Iris.Server;

/// <summary>
/// Provides instance-level statistics for federation metadata (e.g. NodeInfo's
/// <c>usage.users.total</c>). Implemented by the persistence layer (e.g.
/// <c>Iris.Server.Data</c>) to return real counts; a default in-memory implementation
/// returns zero for hosts that don't wire a real backend.
/// </summary>
/// <remarks>
/// This interface lives in <c>Iris.Server</c> (not <c>Iris.Server.Data</c>) so the
/// NodeInfo handler can consume it without creating a dependency from the server
/// core to a specific persistence project. The persistence project implements it
/// and registers the implementation in DI.
/// </remarks>
public interface IInstanceStatsProvider
{
    /// <summary>
    /// Returns the total number of local user accounts on this instance.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The account count, or 0 when unavailable.</returns>
    Task<int> GetLocalUserCountAsync(CancellationToken ct = default);
}
