using System.Diagnostics;
using System.Text.Json;

namespace Iris.Server.Bootstrap;

/// <summary>
/// A cross-process single-instance lock that guards a shared persistence against a second, concurrent
/// Iris instance (Phase 84.5).
/// </summary>
/// <remarks>
/// <para>
/// Phase 84's survey of the per-process state surfaces found that two Iris instances sharing one
/// persistence would <em>silently</em> diverge on every in-process surface — most dangerously the
/// <c>actor→key</c> binding map (a rotation on instance A is invisible to B's signer) and the
/// in-memory delivery queue (a delivery queued on A is never sent by B). The in-memory and
/// file-backed <see cref="Iris.Server.Stores.IPersistenceProvider"/> implementations are not safe for
/// two processes at all (each holds its own in-memory copy of the whole graph); only the EF/Postgres
/// provider shares state, and even it leaves the binding map + delivery queue divergent. There was no
/// guard against this: a second instance on the same persistence simply started and diverged.
/// </para>
/// <para>
/// <see cref="SingleInstanceLock"/> closes that gap with a <strong>fail-fast</strong> startup guard. It
/// owns a <em>lock file</em> (JSON: the owning process id + a host identifier). On
/// <see cref="AcquireAsync"/>:
/// <list type="bullet">
/// <item>no lock file → create it and <em>acquire</em>;</item>
/// <item>a lock file whose owner process is <em>not alive</em> (the prior instance exited or crashed and
/// did not clean up) → <em>steal</em> it (overwrite) and acquire — so a crash never leaves a stale lock
/// that bricks the instance;</item>
/// <item>a lock file whose owner process <em>is alive</em> and is a different process → <strong>throw</strong>
/// (<see cref="InstanceAlreadyRunningException"/>) so the host fails to start with an actionable message.</item>
/// </list>
/// </para>
/// <para>
/// The lock is held for the owning process's lifetime: the OS releases the handle on normal exit
/// (<see cref="Dispose"/> deletes the file) and on a crash (the handle is closed, so a new instance can
/// steal it via the liveness check). No advisory-lock P/Invoke is required — the lock file + PID
/// liveness is portable across the platforms Iris targets. A recycled PID (the prior owner's id
/// reassigned to an unrelated process before a new instance starts) would be misread as "alive"; that
/// window is vanishingly small, and its consequence is only a (safe) failed startup that the operator
/// resolves by deleting the stale lock file.
/// </para>
/// </remarks>
public sealed class SingleInstanceLock : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _lockPath;
    private bool _disposed;

    private SingleInstanceLock(string lockPath, int ownerPid)
    {
        _lockPath = lockPath;
        OwnerPid = ownerPid;
    }

    /// <summary>
    /// The process id of the instance that owns (acquired) the lock.
    /// </summary>
    public int OwnerPid { get; }

    /// <summary>
    /// The path of the lock file this instance owns.
    /// </summary>
    public string LockPath => _lockPath;

    /// <summary>
    /// Acquires the single-instance lock at <paramref name="lockPath"/>, failing fast when a <em>live</em>
    /// different instance already holds it.
    /// </summary>
    /// <param name="lockPath">
    /// The path of the lock file (its parent directory is created if missing). Two instances that share
    /// the same persistence must be configured with the same <paramref name="lockPath"/> for the guard
    /// to detect the collision.
    /// </param>
    /// <param name="hostIdentifier">
    /// A human-readable identifier for this instance (e.g. the host name or container name); recorded in
    /// the lock file so an operator can tell which instance owns it.
    /// </param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The acquired lock. Dispose it on shutdown to release (delete) the lock file.</returns>
    /// <exception cref="ArgumentException">When <paramref name="lockPath"/> or <paramref name="hostIdentifier"/> is null or empty.</exception>
    /// <exception cref="InstanceAlreadyRunningException">
    /// When a lock file is present and its owner process is alive and is a different process — a second
    /// instance is already running against this persistence.
    /// </exception>
    public static async Task<SingleInstanceLock> AcquireAsync(
        string lockPath,
        string hostIdentifier,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostIdentifier);

        var currentPid = Environment.ProcessId;

        if (File.Exists(lockPath))
        {
            // A lock file is present. Read it and decide: steal (owner dead) or fail (owner live).
            string ownerJson;
            try
            {
                ownerJson = await File.ReadAllTextAsync(lockPath, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The file is being written by a concurrently-starting instance. Treat it as held (the
                // safe outcome — fail fast rather than race a second startup).
                throw new InstanceAlreadyRunningException(lockPath, unknownOwner: true);
            }

            var owner = LockRecord.FromJson(ownerJson);
            if (owner is { } existing && existing.Pid != currentPid && IsProcessAlive(existing.Pid))
            {
                throw new InstanceAlreadyRunningException(
                    lockPath,
                    unknownOwner: false,
                    ownerPid: existing.Pid,
                    ownerHost: existing.Host);
            }

            // Owner is dead (or unparseable, or our own stale file) — fall through and steal.
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(lockPath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var record = new LockRecord(currentPid, hostIdentifier);
        await File.WriteAllTextAsync(lockPath, record.ToJson(), ct).ConfigureAwait(false);
        return new SingleInstanceLock(lockPath, currentPid);
    }

    /// <summary>
    /// Whether the process with <paramref name="pid"/> is currently running.
    /// </summary>
    /// <remarks>
    /// A process that has exited is not found (<c>Process.GetProcessById</c> throws
    /// <see cref="ArgumentException"/>), so it is reported as not alive. The liveness check is the
    /// portable, P/Invoke-free mechanism: a crashed instance's handle is closed by the OS, so its pid is
    /// gone and a new instance can steal the lock file.
    /// </remarks>
    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Releases the lock: deletes the lock file (if it still names this instance).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Only delete the file if it still names us (a concurrent steal would have overwritten it with a
        // different owner — we must not delete that other instance's lock).
        try
        {
            if (File.Exists(_lockPath))
            {
                var current = LockRecord.FromJson(File.ReadAllText(_lockPath));
                if (current is { } existing && existing.Pid == OwnerPid)
                {
                    File.Delete(_lockPath);
                }
            }
        }
        catch (IOException)
        {
            // A concurrent writer (or the OS) interfered with the cleanup. The next startup's liveness
            // check will steal the stale file, so a leftover lock file is not fatal. No logger is
            // available to this static-owned type; move on.
        }
    }

    /// <summary>
    /// The on-disk lock record: the owning process id and a host identifier.
    /// </summary>
    /// <param name="Pid">The owning process id.</param>
    /// <param name="Host">A human-readable identifier for the owning instance.</param>
    public sealed record LockRecord(int Pid, string Host)
    {
        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        public static LockRecord? FromJson(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<LockRecord>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

/// <summary>
/// Thrown when a second Iris instance attempts to start against a persistence that a <em>live</em>
/// different instance already holds (Phase 84.5). The host should treat this as a fatal startup error.
/// </summary>
public sealed class InstanceAlreadyRunningException : Exception
{
    /// <summary>
    /// Initializes a new <see cref="InstanceAlreadyRunningException"/>.
    /// </summary>
    /// <param name="lockPath">The path of the lock file the collision was detected on.</param>
    /// <param name="unknownOwner">
    /// When <see langword="true"/>, the lock file was present but its owner could not be determined
    /// (e.g. it was mid-write); the message is generic. When <see langword="false"/>, the owner's pid +
    /// host are included in the message.
    /// </param>
    /// <param name="ownerPid">The live owner's process id (meaningful when <paramref name="unknownOwner"/> is <see langword="false"/>).</param>
    /// <param name="ownerHost">The live owner's host identifier (meaningful when <paramref name="unknownOwner"/> is <see langword="false"/>).</param>
    public InstanceAlreadyRunningException(string lockPath, bool unknownOwner, int? ownerPid = null, string? ownerHost = null)
        : base(
            unknownOwner
                ? $"A second Iris instance is already running against this persistence (lock file '{lockPath}' is held, but its owner could not be read). Only one instance per persistence is supported; stop the other instance or point this one at a different persistence (Iris:InstanceLockPath)."
                : $"A second Iris instance is already running against this persistence (lock file '{lockPath}' is held by pid {ownerPid}, host '{ownerHost}'). Only one instance per persistence is supported; stop the other instance or point this one at a different persistence (Iris:InstanceLockPath).")
    {
        LockPath = lockPath;
        OwnerPid = ownerPid;
        OwnerHost = ownerHost;
    }

    /// <summary>The path of the lock file the collision was detected on.</summary>
    public string LockPath { get; }

    /// <summary>The live owner's process id, when it could be determined.</summary>
    public int? OwnerPid { get; }

    /// <summary>The live owner's host identifier, when it could be determined.</summary>
    public string? OwnerHost { get; }
}
