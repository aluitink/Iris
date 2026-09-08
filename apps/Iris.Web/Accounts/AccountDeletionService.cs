using Iris.Core;
using Iris.Server.Data.Accounts;
using Iris.Server.Stores;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Accounts;

/// <summary>
/// The result of an account-deletion attempt.
/// </summary>
public sealed class AccountDeletionResult
{
    private AccountDeletionResult(bool succeeded, string? error)
    {
        Succeeded = succeeded;
        Error = error;
    }

    /// <summary>Whether the deletion completed.</summary>
    public bool Succeeded { get; }

    /// <summary>A user-facing error message (null on success).</summary>
    public string? Error { get; }

    /// <summary>Creates a success result.</summary>
    public static AccountDeletionResult Ok() => new(true, null);

    /// <summary>Creates a failure result.</summary>
    /// <param name="error">The error message.</param>
    public static AccountDeletionResult Fail(string error) => new(false, error);
}

/// <summary>
/// Orchestrates the teardown of a local account: tombstones the actor's content objects,
/// removes the actor from the actor store, and deletes the account row.
/// </summary>
public sealed class AccountDeletionService
{
    private readonly IUserAccountStore _accounts;
    private readonly IPersistenceProvider _persistence;
    private readonly ILogger<AccountDeletionService> _logger;

    /// <summary>
    /// Initializes the service.
    /// </summary>
    /// <param name="accounts">The account store.</param>
    /// <param name="persistence">The persistence provider (for actor + object stores).</param>
    /// <param name="logger">A logger.</param>
    public AccountDeletionService(
        IUserAccountStore accounts,
        IPersistenceProvider persistence,
        ILogger<AccountDeletionService> logger)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Deletes an account by id: tombstones the actor's objects, removes the actor, and deletes
    /// the account row.
    /// </summary>
    /// <param name="accountId">The account id.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A result (success or a user-facing error).</returns>
    public async Task<AccountDeletionResult> DeleteAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = await _accounts.FindByIdAsync(accountId, ct);
        if (account is null)
        {
            return AccountDeletionResult.Fail("Account not found.");
        }

        var actorIri = account.ActorId;

        // Tombstone the actor's content objects (so their IRIs still resolve as "deleted").
        var objects = await _persistence.Objects.ListByActorAsync(actorIri, ct);
        foreach (var obj in objects)
        {
            if (obj is Tombstone)
            {
                continue; // already tombstoned
            }

            var tombstone = new Tombstone
            {
                Id = obj.Id,
                Deleted = DateTime.UtcNow,
            };
            await _persistence.Objects.PutObjectAsync(tombstone, ct);
        }

        // Remove the actor.
        await _persistence.Actors.RemoveActorAsync(actorIri, ct);

        // Delete the account row.
        await _accounts.DeleteAsync(accountId, ct);

        _logger.LogInformation(
            "Account '{Username}' ({Id}) deleted: {ObjectCount} object(s) tombstoned, actor removed.",
            account.Username, accountId, objects.Count);

        return AccountDeletionResult.Ok();
    }
}
