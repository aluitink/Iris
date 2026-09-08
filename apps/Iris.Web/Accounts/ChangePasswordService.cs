using Iris.Server.Data.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Accounts;

/// <summary>
/// The result of a change-password attempt. <see cref="Error"/> is null on success.
/// </summary>
public sealed class ChangePasswordResult
{
    /// <summary>The error message, or null on success.</summary>
    public string? Error { get; }

    private ChangePasswordResult(string? error) => Error = error;

    /// <summary>Whether the operation succeeded.</summary>
    public bool Succeeded => Error is null;

    /// <summary>Creates a success result.</summary>
    public static ChangePasswordResult Ok() => new(null);

    /// <summary>Creates a failure result.</summary>
    /// <param name="error">The error message.</param>
    public static ChangePasswordResult Fail(string error) => new(error);
}

/// <summary>
/// Changes a user's password: verifies the current password, validates the new password's length,
/// hashes it, and persists the new hash. The caller (the endpoint) resolves the signed-in account
/// via the <c>sub</c> claim.
/// </summary>
public sealed class ChangePasswordService
{
    private readonly IUserAccountStore _accounts;
    private readonly PasswordHasher _hasher;
    private readonly ILogger<ChangePasswordService> _logger;

    /// <summary>
    /// Initializes the service.
    /// </summary>
    /// <param name="accounts">The account store.</param>
    /// <param name="hasher">The password hasher.</param>
    /// <param name="logger">A logger (records outcomes, never the password).</param>
    public ChangePasswordService(
        IUserAccountStore accounts,
        PasswordHasher hasher,
        ILogger<ChangePasswordService> logger)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Changes the password for the given account.
    /// </summary>
    /// <param name="accountId">The account's id.</param>
    /// <param name="currentPassword">The current password (verified before the change).</param>
    /// <param name="newPassword">The new password (must be at least <see cref="RegistrationService.MinPasswordLength"/> chars).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A <see cref="ChangePasswordResult"/>.</returns>
    public async Task<ChangePasswordResult> ChangeAsync(
        Guid accountId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(newPassword))
        {
            return ChangePasswordResult.Fail("Current and new passwords are required.");
        }

        if (newPassword.Length < RegistrationService.MinPasswordLength)
        {
            return ChangePasswordResult.Fail($"Password must be at least {RegistrationService.MinPasswordLength} characters.");
        }

        var account = await _accounts.FindByIdAsync(accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            return ChangePasswordResult.Fail("Account not found.");
        }

        var verifyResult = _hasher.Verify(account.PasswordHash, currentPassword);
        if (verifyResult is PasswordVerificationResult.Failed)
        {
            _logger.LogInformation("Change-password failure: wrong current password for account {AccountId}.", accountId);
            return ChangePasswordResult.Fail("Current password is incorrect.");
        }

        var newHash = _hasher.Hash(newPassword);
        await _accounts.UpdatePasswordHashAsync(account.Id, newHash, ct).ConfigureAwait(false);

        _logger.LogInformation("Password changed for account {AccountId}.", accountId);
        return ChangePasswordResult.Ok();
    }
}
