using Iris.Server.Data.Accounts;
using Iris.Server.Stores;
using Iris.Web;

namespace Iris.Web.Accounts;

/// <summary>
/// A scoped service that provides notification read-state operations for Blazor components.
/// Operates in-process (directly on the account store and activity store) rather than via HTTP,
/// so it avoids the cookie-propagation problem of server-circuit → same-origin HTTP calls.
/// </summary>
public sealed class NotificationService
{
    private readonly IUserAccountStore _accounts;
    private readonly IPersistenceProvider _persistence;

    /// <summary>
    /// Initializes the service.
    /// </summary>
    /// <param name="accounts">The user account store.</param>
    /// <param name="persistence">The persistence provider (for inbox access).</param>
    public NotificationService(IUserAccountStore accounts, IPersistenceProvider persistence)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    /// <summary>
    /// The number of unread notifications for the given account (inbox items newer than the
    /// account's <c>NotificationsReadAt</c> cursor). Returns 0 when the account is null.
    /// </summary>
    public async Task<int> GetUnreadCountAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = await _accounts.FindByIdAsync(accountId, ct);
        if (account is null)
        {
            return 0;
        }

        var inbox = await _persistence.Activities.GetInboxAsync(account.ActorId, ct);
        return WebAppFactory.CountUnread(inbox, account.NotificationsReadAt);
    }

    /// <summary>
    /// Marks all notifications as read for the given account. Returns the (now-zero) unread count.
    /// </summary>
    public async Task<int> MarkAllReadAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = await _accounts.FindByIdAsync(accountId, ct);
        if (account is null)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        await _accounts.UpdateNotificationsReadAtAsync(account.Id, now, ct);
        return 0;
    }
}
