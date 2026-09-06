using Iris.Core;

namespace Iris.Server.Data.Accounts;

/// <summary>
/// The two roles the MVP recognizes. <c>User</c> is the default; <c>Admin</c> gates the
/// instance-admin surfaces (the moderation queue across all users, instance settings).
/// </summary>
public enum UserRole
{
    User,
    Admin,
}

/// <summary>
/// A local browser-session account (username, password hash, role) linked 1:1 to a local
/// ActivityPub actor. The account <em>is</em> a federated identity — the linked actor
/// (<see cref="ActorId"/>) is the durable identity concept, so whatever authenticates the
/// browser (a cookie today, an OAuth2 bearer token tomorrow, an external-IdP claim later)
/// just needs to resolve to the same actor.
/// </summary>
/// <remarks>
/// No email is collected (deliberate MVP cut — no self-service password recovery; the only
/// recovery path is an admin-assisted reset, see the auth plan §7). <see cref="NotificationsReadAt"/>
/// is the MVP "mark notifications as read" cursor.
/// </remarks>
public sealed class UserAccount
{
    /// <summary>
    /// The account's stable identifier (the browser session's <c>sub</c> claim).
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The account's username (unique, compared case-insensitively). Also the linked actor's
    /// handle, so it must satisfy the actor-handle rules (validated at registration in
    /// <c>Iris.Web</c>).
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The password hash, produced by <c>PasswordHasher&lt;UserAccount&gt;</c> (PBKDF2-HMAC-SHA256,
    /// versioned, per-password salt). Never the plaintext.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// The account's role (<see cref="UserRole.User"/> or <see cref="UserRole.Admin"/>).
    /// </summary>
    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>
    /// The IRI of the linked local ActivityPub actor (the account's federated identity). Must be set
    /// (the linked actor must already exist in the actor store before the account row is created).
    /// </summary>
    public required Iri ActorId { get; set; }

    /// <summary>
    /// The "mark notifications as read" cursor (the last time this account read its notification
    /// list). Null until first read.
    /// </summary>
    public DateTimeOffset? NotificationsReadAt { get; set; }

    /// <summary>
    /// When the account was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
