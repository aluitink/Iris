using Iris.Core;
using Iris.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Iris.Server.Data.Accounts;

/// <summary>
/// The EF Core (PostgreSQL) <see cref="IUserAccountStore"/>. Accounts are plain relational rows (no
/// <c>jsonb</c> payload — the account is fully described by its columns).
/// </summary>
public sealed class EfUserAccountStore : IUserAccountStore
{
    private readonly IDbContextFactory<IrisDbContext> _factory;

    /// <summary>
    /// Initializes the store over a context factory.
    /// </summary>
    /// <param name="factory">The <see cref="IrisDbContext"/> factory. Must not be null.</param>
    public EfUserAccountStore(IDbContextFactory<IrisDbContext> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc/>
    public async Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Set<UserAccountEntity>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Username.ToLower() == username.ToLower(), ct).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    /// <inheritdoc/>
    public async Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Set<UserAccountEntity>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    /// <inheritdoc/>
    public async Task CreateAsync(UserAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Username);
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var exists = await db.Set<UserAccountEntity>().AnyAsync(
            e => e.Username.ToLower() == account.Username.ToLower(), ct).ConfigureAwait(false);
        if (exists)
        {
            throw new InvalidOperationException(
                $"An account with the username '{account.Username}' already exists.");
        }

        db.Set<UserAccountEntity>().Add(ToEntity(account));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpdatePasswordHashAsync(Guid id, string newHash, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newHash);
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Set<UserAccountEntity>().FirstOrDefaultAsync(e => e.Id == id, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            throw new InvalidOperationException($"No account with id {id}.");
        }

        entity.PasswordHash = newHash;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpdateNotificationsReadAtAsync(Guid id, DateTimeOffset readAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Set<UserAccountEntity>().FirstOrDefaultAsync(e => e.Id == id, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            throw new InvalidOperationException($"No account with id {id}.");
        }

        entity.NotificationsReadAt = readAt;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> AnyAdminExistsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Set<UserAccountEntity>().AnyAsync(
            e => e.Role == UserRole.Admin.ToString(), ct).ConfigureAwait(false);
    }

    private static UserAccountEntity ToEntity(UserAccount account) => new()
    {
        Id = account.Id,
        Username = account.Username,
        PasswordHash = account.PasswordHash,
        Role = account.Role.ToString(),
        ActorIri = account.ActorId.Value,
        NotificationsReadAt = account.NotificationsReadAt,
        CreatedAt = account.CreatedAt,
    };

    private static UserAccount ToModel(UserAccountEntity entity) => new()
    {
        Id = entity.Id,
        Username = entity.Username,
        PasswordHash = entity.PasswordHash,
        Role = Enum.TryParse<UserRole>(entity.Role, out var role) ? role : UserRole.User,
        ActorId = new Iri(entity.ActorIri),
        NotificationsReadAt = entity.NotificationsReadAt,
        CreatedAt = entity.CreatedAt,
    };
}
