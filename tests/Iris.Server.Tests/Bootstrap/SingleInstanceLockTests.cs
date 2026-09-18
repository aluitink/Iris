using System.Diagnostics;
using Iris.Server.Bootstrap;

namespace Iris.Server.Tests.Bootstrap;

/// <summary>
/// Phase 84.5 — <strong>Single-instance guard</strong>: unit tests for <see cref="SingleInstanceLock"/>.
/// The lock file + PID liveness is the portable, P/Invoke-free mechanism that turns a silent
/// second-instance divergence (the worst failure mode: two instances on the same persistence signing
/// with different keys / dropping deliveries) into a loud, actionable startup error.
/// </summary>
public sealed class SingleInstanceLockTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "iris-single-instance-tests", Guid.NewGuid().ToString("n"));

    public SingleInstanceLockTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup; a leftover temp dir is harmless
        }
    }

    private string LockPath(string name) => Path.Combine(_directory, name);

    [Fact]
    public async Task Acquire_NoLockFile_CreatesAndAcquires()
    {
        var path = LockPath("acquire.json");
        Assert.False(File.Exists(path));

        var acquired = await SingleInstanceLock.AcquireAsync(path, "test-host");

        Assert.True(File.Exists(path));
        Assert.Equal(Environment.ProcessId, acquired.OwnerPid);
        Assert.Equal(path, acquired.LockPath);

        // The lock file records this process's pid + host.
        var record = SingleInstanceLock.LockRecord.FromJson(File.ReadAllText(path));
        Assert.NotNull(record);
        Assert.Equal(Environment.ProcessId, record!.Pid);
        Assert.Equal("test-host", record.Host);
    }

    [Fact]
    public async Task Acquire_OwnStaleLockFile_Steals()
    {
        // A lock file naming our own pid (e.g. left over from a prior run in the same process) is stolen.
        var path = LockPath("stale-own.json");
        var ownRecord = new SingleInstanceLock.LockRecord(Environment.ProcessId, "stale-host");
        await File.WriteAllTextAsync(path, ownRecord.ToJson());

        var acquired = await SingleInstanceLock.AcquireAsync(path, "new-host");

        // The lock file now names our current acquisition (host updated).
        var record = SingleInstanceLock.LockRecord.FromJson(File.ReadAllText(path));
        Assert.NotNull(record);
        Assert.Equal("new-host", record!.Host);
        Assert.Equal(Environment.ProcessId, acquired.OwnerPid);
    }

    [Fact]
    public async Task Acquire_DeadOwnerLockFile_Steals()
    {
        // A lock file naming a process that is not alive (crashed/exited) is stolen — no stale lock bricks
        // the instance.
        var path = LockPath("dead-owner.json");
        var deadPid = await GetDeadPidAsync();
        var deadRecord = new SingleInstanceLock.LockRecord(deadPid, "dead-host");
        await File.WriteAllTextAsync(path, deadRecord.ToJson());

        var acquired = await SingleInstanceLock.AcquireAsync(path, "new-host");

        Assert.Equal(Environment.ProcessId, acquired.OwnerPid);
        var record = SingleInstanceLock.LockRecord.FromJson(File.ReadAllText(path));
        Assert.NotNull(record);
        Assert.Equal("new-host", record!.Host);
    }

    [Fact]
    public async Task Acquire_LiveOwnerLockFile_ThrowsInstanceAlreadyRunning()
    {
        // A lock file naming a live, different process fails fast with an actionable error. The "owner"
        // must stay alive across the AcquireAsync call, so the process is launched here and killed after.
        var path = LockPath("live-owner.json");
        using var owner = StartLiveProcess();
        var ownerPid = owner.ProcessId;
        var liveRecord = new SingleInstanceLock.LockRecord(ownerPid, "live-host");
        await File.WriteAllTextAsync(path, liveRecord.ToJson());

        var ex = await Assert.ThrowsAsync<InstanceAlreadyRunningException>(
            () => SingleInstanceLock.AcquireAsync(path, "new-host"));

        Assert.Equal(path, ex.LockPath);
        Assert.Equal(ownerPid, ex.OwnerPid);
        Assert.Equal("live-host", ex.OwnerHost);
        Assert.Contains("second Iris instance", ex.Message);

        // The live owner's lock file is untouched (not stolen).
        var record = SingleInstanceLock.LockRecord.FromJson(File.ReadAllText(path));
        Assert.NotNull(record);
        Assert.Equal(ownerPid, record!.Pid);
    }

    [Fact]
    public async Task Acquire_UnparseableLockFile_Steals()
    {
        // A corrupt lock file (unparseable) is treated as "no owner" and stolen (the safe, non-bricking
        // outcome — a corrupt file is not evidence of a live owner).
        var path = LockPath("corrupt.json");
        await File.WriteAllTextAsync(path, "{ not valid json ");

        var acquired = await SingleInstanceLock.AcquireAsync(path, "new-host");

        Assert.Equal(Environment.ProcessId, acquired.OwnerPid);
    }

    [Fact]
    public async Task Dispose_DeletesOwnLockFile()
    {
        var path = LockPath("dispose.json");
        var acquired = await SingleInstanceLock.AcquireAsync(path, "test-host");

        acquired.Dispose();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Dispose_DoesNotDeleteAnotherInstancesLockFile()
    {
        // If another instance stole our lock (overwriting the file with its own pid), our Dispose must not
        // delete the new owner's lock file.
        var path = LockPath("dispose-other.json");
        var acquired = await SingleInstanceLock.AcquireAsync(path, "first-host");

        // Simulate a concurrent steal: overwrite the file with a different (dead, so it'd be stolen back)
        // pid — but the point is the file no longer names us.
        var otherPid = await GetDeadPidAsync();
        var otherRecord = new SingleInstanceLock.LockRecord(otherPid, "second-host");
        await File.WriteAllTextAsync(path, otherRecord.ToJson());

        acquired.Dispose();

        // Our Dispose saw the file names a different pid and left it alone.
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Acquire_CreatesMissingParentDirectory()
    {
        var path = Path.Combine(_directory, "nested", "deeper", "lock.json");

        var acquired = await SingleInstanceLock.AcquireAsync(path, "test-host");

        Assert.True(File.Exists(path));
        Assert.Equal(Environment.ProcessId, acquired.OwnerPid);
    }

    [Fact]
    public void IsProcessAlive_CurrentProcess_IsAlive()
    {
        Assert.True(SingleInstanceLock.IsProcessAlive(Environment.ProcessId));
    }

    [Fact]
    public async Task IsProcessAlive_DeadPid_IsNotAlive()
    {
        var deadPid = await GetDeadPidAsync();
        Assert.False(SingleInstanceLock.IsProcessAlive(deadPid));
    }

    [Fact]
    public async Task Acquire_EmptyPath_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => SingleInstanceLock.AcquireAsync("", "host"));
        await Assert.ThrowsAsync<ArgumentException>(() => SingleInstanceLock.AcquireAsync("   ", "host"));
        await Assert.ThrowsAsync<ArgumentException>(() => SingleInstanceLock.AcquireAsync(LockPath("x.json"), ""));
    }

    /// <summary>
    /// Finds a pid that is (very likely) not currently in use: a high pid value beyond the current
    /// process's, verified by the liveness check itself.
    /// </summary>
    private static async Task<int> GetDeadPidAsync()
    {
        // Spawn a process that exits immediately, capture its pid, wait for exit, and return that (now dead) pid.
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c exit 0")
            : ("/bin/true", null);
        var process = Process.Start(new ProcessStartInfo { FileName = fileName, Arguments = arguments, UseShellExecute = false })!;
        var pid = process.Id;
        await process.WaitForExitAsync();
        process.Dispose();
        // The pid is now dead. (If the OS recycles it before this check, the liveness check below would
        // rarely flip; in practice a just-exited pid stays free long enough.)
        return pid;
    }

    /// <summary>
    /// A process that stays alive for the lifetime of the wrapper (disposed by the caller's <c>using</c>).
    /// Used to simulate a live foreign instance holding the lock.
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

    /// <summary>
    /// Starts a process that sleeps long enough to outlive the test (a foreign live "owner").
    /// </summary>
    private static LiveProcess StartLiveProcess()
    {
        // A real sleep binary (no shell quoting) — stays alive for the wrapper's lifetime.
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c timeout /t 30 >nul")
            : ("/bin/sleep", "30");
        var process = Process.Start(new ProcessStartInfo { FileName = fileName, Arguments = arguments, UseShellExecute = false })!;
        // Give the OS a moment to fully register the process so its pid is resolvable.
        Thread.Sleep(150);
        return new LiveProcess(process);
    }
}
