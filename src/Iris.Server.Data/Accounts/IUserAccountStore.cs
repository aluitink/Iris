namespace Iris.Server.Data.Accounts;

/// <summary>
/// Persists local browser-session accounts (<see cref="UserAccount"/>) and their 1:1 link to a local
/// ActivityPub actor. Unlike the ActivityStreams store interfaces (which live in <c>Iris.Server</c>
/// and are implemented by a swappable persistence project), this one is declared <em>and</em>
/// implemented in <c>Iris.Server.Data</c>: it has no reason to be swappable independently of that
/// project, and declaring it in <c>Iris.Server</c> would pull a "local user account" concept into a
/// project that has none (ActivityPub has no browser-session login), while declaring it in
/// <c>Iris.Web</c> would create a circular project reference.
/// </summary>
/// <remarks>
/// <c>Iris.Web</c> consumes this only through DI (it never reimplements it). The in-memory and EF
/// Core (PostgreSQL) backends implement the same interface so the app's auth surface is
/// persistence-agnostic.
/// </remarks>
public interface IUserAccountStore
{
    /// <summary>
    /// Finds the account with the given username (case-insensitive). Returns <c>null</c> when there is
    /// no such account.
    /// </summary>
    Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Finds the account with the given id. Returns <c>null</c> when there is no such account.
    /// </summary>
    Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Creates a new account. The caller is responsible for the linked actor already existing in the
    /// actor store (this method persists only the account row). Throws on a duplicate username.
    /// </summary>
    Task CreateAsync(UserAccount account, CancellationToken ct = default);

    /// <summary>
    /// Replaces an account's password hash. Used by both the user's own "change password" flow and the
    /// admin-assisted reset (the MVP's only account-recovery path).
    /// </summary>
    Task UpdatePasswordHashAsync(Guid id, string newHash, CancellationToken ct = default);

    /// <summary>
    /// Advances an account's "notifications read" cursor.
    /// </summary>
    Task UpdateNotificationsReadAtAsync(Guid id, DateTimeOffset readAt, CancellationToken ct = default);

    /// <summary>
    /// Returns whether any account has the <see cref="UserRole.Admin"/> role. Used by the admin
    /// bootstrapper (which is idempotent — it never creates a second admin once one exists).
    /// </summary>
    Task<bool> AnyAdminExistsAsync(CancellationToken ct = default);
}
