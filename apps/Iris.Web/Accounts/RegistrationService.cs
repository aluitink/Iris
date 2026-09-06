using Iris.Core.Identity;
using Iris.Server.Data.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Accounts;

/// <summary>
/// The result of a registration attempt.
/// </summary>
/// <param name="Account">The created account (null when <see cref="Succeeded"/> is false).</param>
/// <param name="Error">A user-facing error message (null when <see cref="Succeeded"/> is true).</param>
public sealed record RegistrationResult(UserAccount? Account, string? Error)
{
    public bool Succeeded => Error is null;

    public static RegistrationResult Ok(UserAccount account) => new(account, null);
    public static RegistrationResult Fail(string error) => new(null, error);
}

/// <summary>
/// Registers a new local account: validates the username + password, provisions the linked local
/// ActivityPub actor (a <see cref="KristofferStrube.ActivityStreams.Person"/> with a real key pair),
/// and persists the <see cref="UserAccount"/> linked 1:1 to that actor. The returned account's
/// <see cref="UserAccount.ActorId"/> is the actor's IRI; the caller (the <c>/register</c> page) signs
/// the user in via cookie authentication using the account's claims.
/// </summary>
/// <remarks>
/// This is the "bootstrap mechanism" of the production app — the one deliberate exception to the
/// "no new APIs" rule, because ActivityPub has no browser-session login. Actor provisioning calls the
/// library's existing host-side actor-management surface in-process (the same path
/// <c>SampleServer</c> uses to seed <c>alice</c>/<c>bob</c>), not a wire endpoint.
/// </remarks>
public sealed class RegistrationService
{
    /// <summary>The minimum password length (a reasonable floor, not a gauntlet).</summary>
    public const int MinPasswordLength = 8;

    private readonly IUserAccountStore _accounts;
    private readonly ActorProvisioner _provisioner;
    private readonly PasswordHasher _hasher;
    private readonly ILogger<RegistrationService> _logger;

    /// <summary>
    /// Initializes the service.
    /// </summary>
    /// <param name="accounts">The account store.</param>
    /// <param name="provisioner">The actor provisioner (creates the linked actor).</param>
    /// <param name="hasher">The password hasher.</param>
    /// <param name="logger">A logger (records registration outcomes, never the password).</param>
    public RegistrationService(
        IUserAccountStore accounts,
        ActorProvisioner provisioner,
        PasswordHasher hasher,
        ILogger<RegistrationService> logger)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Registers a new account.
    /// </summary>
    /// <param name="username">The desired username (also the linked actor's handle).</param>
    /// <param name="password">The plaintext password (never persisted).</param>
    /// <param name="displayName">An optional display name (defaults to the username).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A <see cref="RegistrationResult"/> (success + account, or a user-facing error).</returns>
    public async Task<RegistrationResult> RegisterAsync(
        string? username, string? password, string? displayName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return RegistrationResult.Fail("A username is required.");
        }

        // Validate the handle (username == actor handle).
        var handleError = HandleRules.Validate(username);
        if (handleError is not null)
        {
            return RegistrationResult.Fail(handleError);
        }

        // Validate the password (a length floor — the plan calls for "length, not a gauntlet").
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            return RegistrationResult.Fail($"The password must be at least {MinPasswordLength} characters.");
        }

        // Uniqueness (case-insensitive).
        var existing = await _accounts.FindByUsernameAsync(username, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return RegistrationResult.Fail("That username is already taken.");
        }

        // Provision the linked local actor (a Person with a real key pair).
        Iri actorIri;
        try
        {
            actorIri = await _provisioner.ProvisionAsync(username, displayName, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Actor provisioning failed for '{Username}'.", username);
            return RegistrationResult.Fail("Could not create your account. Please try again.");
        }

        // Persist the account, linked 1:1 to the provisioned actor.
        var account = new UserAccount
        {
            Username = username,
            PasswordHash = _hasher.Hash(password),
            Role = UserRole.User,
            ActorId = actorIri,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        try
        {
            await _accounts.CreateAsync(account, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // A concurrent registration won the race (username taken between check + create).
            _logger.LogWarning("Account create failed for '{Username}': {Message}", username, ex.Message);
            return RegistrationResult.Fail("That username is already taken.");
        }

        // Never log the password or the hash; log only the outcome.
        _logger.LogInformation("Registered account '{Username}' (actor {ActorIri}).", username, actorIri.Value);
        return RegistrationResult.Ok(account);
    }
}
