namespace Iris.Server.Tests;

/// <summary>
/// Raises the process-wide minimum thread-pool thread count for the Iris.Server.Tests host. The suite
/// builds many in-process <c>TestServer</c> hosts, each of which runs a background <c>DeliveryWorker</c>
/// pump (an async <see cref="System.Threading.Channels.Channel"/> consumer + in-flight signed HTTP
/// round-trips). Under full-suite load the process-wide thread pool can starve: the pool is slow to grow
/// its worker threads fast enough to service every host's queued async continuations, so a delivery's
/// continuation is never scheduled and the round trip hangs indefinitely (the dead-letter store and queue
/// are both empty when the round-trip wait times out — the job is stuck in-flight, not failed). xunit is
/// already configured with <c>parallelizeTestCollections: false</c> / <c>maxParallelThreads: 1</c>, so this
/// is not test-parallelism contention; it is the pooled threads themselves being exhausted by the many
/// concurrently-live background pumps. Setting a generous minimum thread count keeps the pool from
/// starving those continuations, which removes the load-dependent flake (e.g.
/// <c>MutualFollow_BothFollowersCollections_ListTheOther</c>,
/// <c>Follow_Unfollow_Refollow_Cycle_EdgesConvergeOnBothInstances_StableCollections</c>) without weakening
/// any test.
/// </summary>
internal static class TestThreadPoolInitializer
{
    /// <summary>
    /// Runs once when the test assembly's types are first JITed (before any test runs), raising the
    /// minimum worker + IO thread count of the process-wide thread pool.
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize()
    {
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        ThreadPool.SetMinThreads(Math.Max(minWorker, 200), Math.Max(minIo, 200));
    }
}
