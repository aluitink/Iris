namespace Iris.Server.Data.Entities;

/// <summary>
/// A local browser-session account row (username, password hash, role, linked actor IRI). Unlike the
/// ActivityStreams entities (which carry a full document in a <c>jsonb</c> column), this is a plain
/// relational row: the account is entirely described by its columns. The linked actor's full document
/// lives in the <c>Actors</c> table (referenced by <see cref="ActorIri"/>), not here.
/// </summary>
public sealed class UserAccountEntity
{
    /// <summary>
    /// The account's stable identifier (primary key).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The account's username (unique; compared case-insensitively).
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The password hash (<c>PasswordHasher&lt;UserAccount&gt;</c>-produced). Never the plaintext.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// The account's role (<c>User</c> or <c>Admin</c>) as its string name.
    /// </summary>
    public string Role { get; set; } = "User";

    /// <summary>
    /// The IRI of the linked local ActivityPub actor (the account's federated identity).
    /// </summary>
    public string ActorIri { get; set; } = string.Empty;

    /// <summary>
    /// The "mark notifications as read" cursor. Null until first read.
    /// </summary>
    public DateTimeOffset? NotificationsReadAt { get; set; }

    /// <summary>
    /// When the account was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
