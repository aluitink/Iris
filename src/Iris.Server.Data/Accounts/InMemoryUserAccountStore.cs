namespace Iris.Server.Data.Accounts;

/// <summary>
/// The in-memory <see cref="IUserAccountStore"/> (the default backend for the bare host and the
/// integration tests). Accounts are held in a thread-safe dictionary keyed by account id; username
/// lookups scan by case-insensitive username.
/// </summary>
public sealed class InMemoryUserAccountStore : IUserAccountStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, UserAccount> _accounts = new();

    public Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        lock (_gate)
        {
            foreach (var account in _accounts.Values)
            {
                if (string.Equals(account.Username, username, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult<UserAccount?>(clone(account));
                }
            }
        }
        UserAccount? none = null;
        return Task.FromResult(none);
    }

    public Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        UserAccount? result = null;
        lock (_gate)
        {
            if (_accounts.TryGetValue(id, out var account))
            {
                result = clone(account);
            }
        }
        return Task.FromResult(result);
    }

    public Task CreateAsync(UserAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_gate)
        {
            if (_accounts.Values.Any(a =>
                    string.Equals(a.Username, account.Username, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"An account with the username '{account.Username}' already exists.");
            }
            _accounts[account.Id] = clone(account);
        }
        return Task.CompletedTask;
    }

    public Task UpdatePasswordHashAsync(Guid id, string newHash, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newHash);
        lock (_gate)
        {
            if (!_accounts.TryGetValue(id, out var account))
            {
                return Task.FromException(new InvalidOperationException($"No account with id {id}."));
            }
            account.PasswordHash = newHash;
        }
        return Task.CompletedTask;
    }

    public Task UpdateNotificationsReadAtAsync(Guid id, DateTimeOffset readAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(id, out var account))
            {
                return Task.FromException(new InvalidOperationException($"No account with id {id}."));
            }
            account.NotificationsReadAt = readAt;
        }
        return Task.CompletedTask;
    }

    public Task<bool> AnyAdminExistsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_accounts.Values.Any(a => a.Role == UserRole.Admin));
        }
    }

    // A defensive clone so callers cannot mutate the stored account by holding onto the returned reference.
    private static UserAccount clone(UserAccount account) => new()
    {
        Id = account.Id,
        Username = account.Username,
        PasswordHash = account.PasswordHash,
        Role = account.Role,
        ActorId = account.ActorId,
        NotificationsReadAt = account.NotificationsReadAt,
        CreatedAt = account.CreatedAt,
    };
}
