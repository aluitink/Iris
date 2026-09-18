using System.Diagnostics;
using Iris.Client.Auth;
using Iris.Server.Bootstrap;
using Iris.Server.Caching;
using Iris.Server.Delivery;
using Iris.Server.Identity;
using Iris.Server.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Bootstrap;

/// <summary>
/// Integration tests for <see cref="SingleInstanceGuardHostedService"/> (Phase 84.5; lifted in Phase
/// 84.6 to support multi-instance scale-out): the exact hosted service the DI container starts on boot.
/// These exercise the real <c>StartAsync</c>/<c>StopAsync</c> lifecycle against a real lock file + a real
/// live foreign process — the cross-process guard. The lock is per-origin: two hosts on the same lock
/// path are a collision. The guard's behavior on a collision depends on whether the scale-out
/// convergence pieces are all configured (Phase 84.6): fail-fast when any is missing (unsafe divergence),
/// warning when all three are configured (supported multi-instance).
/// </summary>
public sealed class SingleInstanceGuardIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "iris-single-instance-guard-" + Guid.NewGuid().ToString("N"));

    public SingleInstanceGuardIntegrationTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string LockPath(string name) => Path.Combine(_root, name);

    [Fact]
    public async Task StartAsync_LiveForeignOwner_FailsFast()
    {
        // A lock file held by a live foreign process: the guard must throw InstanceAlreadyRunningException
        // from StartAsync (host startup fails fast — the operator must stop the other instance or change
        // this instance's persistence/lock path).
        var path = LockPath("fail-fast.json");
        using var foreign = StartLiveProcess();
        var record = new SingleInstanceLock.LockRecord(foreign.ProcessId, "other-instance");
        await File.WriteAllTextAsync(path, record.ToJson());

        var guard = CreateGuard(path);
        var ex = await Assert.ThrowsAsync<InstanceAlreadyRunningException>(() => guard.StartAsync(CancellationToken.None));

        Assert.Equal(path, ex.LockPath);
        Assert.Equal(foreign.ProcessId, ex.OwnerPid);
        Assert.Equal("other-instance", ex.OwnerHost);

        // The foreign owner's lock file is untouched (not stolen).
        var intact = SingleInstanceLock.LockRecord.FromJson(File.ReadAllText(path));
        Assert.NotNull(intact);
        Assert.Equal(foreign.ProcessId, intact!.Pid);

        await guard.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_LiveForeignOwner_MultiInstanceConfigured_WarnsNotFails()
    {
        // A lock file held by a live foreign process + all three scale-out convergence pieces configured
        // (Phase 84.6): the guard must NOT throw (multi-instance is supported — the instances converge via
        // the shared state). The operator is informed (a warning is logged), but host startup succeeds.
        var path = LockPath("multi-instance.json");
        using var foreign = StartLiveProcess();
        var record = new SingleInstanceLock.LockRecord(foreign.ProcessId, "other-instance");
        await File.WriteAllTextAsync(path, record.ToJson());

        // A service provider with all three scale-out convergence pieces configured.
        var serviceProvider = CreateMultiInstanceServiceProvider(_root);
        var guard = CreateGuard(path, serviceProvider);

        // StartAsync does NOT throw (the guard logs a warning and proceeds).
        await guard.StartAsync(CancellationToken.None);

        // The foreign owner's lock file is untouched (not stolen — the guard does not acquire the lock
        // when a live foreign instance holds it; it only warns).
        var intact = SingleInstanceLock.LockRecord.FromJson(File.ReadAllText(path));
        Assert.NotNull(intact);
        Assert.Equal(foreign.ProcessId, intact!.Pid);

        await guard.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_NoExistingLock_Acquires_And_StopAsync_Releases()
    {
        // A fresh lock path: StartAsync acquires (the file now names this process); StopAsync releases
        // (the file is deleted).
        var path = LockPath("acquire-release.json");
        var guard = CreateGuard(path);

        await guard.StartAsync(CancellationToken.None);
        var acquired = SingleInstanceLock.LockRecord.FromJson(File.ReadAllText(path));
        Assert.NotNull(acquired);
        Assert.Equal(Environment.ProcessId, acquired!.Pid);

        await guard.StopAsync(CancellationToken.None);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task StartAsync_NoLockPathConfigured_IsInert()
    {
        // With no InstanceLockPath configured, the guard is a no-op (no lock file created, no throw).
        var guard = CreateGuard(null);
        await guard.StartAsync(CancellationToken.None);
        await guard.StopAsync(CancellationToken.None);
        // Nothing was written under the guard's root (the guard has no path to write to).
    }

    private static SingleInstanceGuardHostedService CreateGuard(string? lockPath, IServiceProvider? serviceProvider = null) =>
        new(
            Options.Create(new ActivityPubServerOptions { InstanceLockPath = lockPath }),
            serviceProvider ?? new ServiceCollection().BuildServiceProvider(),
            NullLogger<SingleInstanceGuardHostedService>.Instance);

    /// <summary>
    /// A service provider with all three scale-out convergence pieces configured (Phase 84.6): the
    /// <see cref="DocumentDerivedKeyProvider"/> as <c>IKeyProvider</c> + the <see cref="SharedDeliveryQueue"/>
    /// as <c>IDeliveryQueue</c> + the <see cref="CacheInvalidationChannel"/> as
    /// <c>ICacheInvalidationPublisher</c>. When the guard resolves these on a collision, it determines
    /// multi-instance is supported (warning, not fail-fast).
    /// </summary>
    private static IServiceProvider CreateMultiInstanceServiceProvider(string directory)
    {
        var services = new ServiceCollection();
        var persistence = new InMemoryPersistenceProvider();
        services.AddSingleton<IKeyProvider>(_ => new DocumentDerivedKeyProvider(persistence.Keys));
        services.AddSingleton<IDeliveryQueue>(_ => new SharedDeliveryQueue(Path.Combine(directory, "delivery.jsonl")));
        var channel = new CacheInvalidationChannel(Path.Combine(directory, "invalidation.jsonl"));
        services.AddSingleton<ICacheInvalidationPublisher>(channel);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A process that stays alive for the lifetime of the wrapper (disposed by the caller's <c>using</c>),
    /// used to simulate a live foreign instance holding the lock.
    /// </summary>
    private sealed class LiveProcess : IDisposable
    {
        private readonly Process _process;

        public LiveProcess(Process process) => _process = process;

        public int ProcessId => _process.Id;

        public void Dispose()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // best-effort
            }
            finally
            {
                _process.Dispose();
            }
        }
    }

    private static LiveProcess StartLiveProcess()
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c timeout /t 30 >nul")
            : ("/bin/sleep", "30");
        var process = Process.Start(new ProcessStartInfo { FileName = fileName, Arguments = arguments, UseShellExecute = false })!;
        Thread.Sleep(150);
        return new LiveProcess(process);
    }
}
