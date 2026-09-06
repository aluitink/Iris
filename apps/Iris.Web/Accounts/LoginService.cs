using Iris.Server.Data.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Accounts;

/// <summary>
/// The result of a login attempt.
/// </summary>
/// <param name="Account">The verified account (null when <see cref="Succeeded"/> is false).</param>
/// <param name="Error">A generic user-facing error (null on success). Never reveals which field was
/// wrong — the same message for a bad username and a bad password.</param>
/// <param name="RetryAfter">When the key is rate-limited, when it unblocks (for a <c>Retry-After</c>
/// hint); otherwise null.</param>
public sealed record LoginResult(UserAccount? Account, string? Error, DateTimeOffset? RetryAfter)
{
    public bool Succeeded => Error is null;

    public static LoginResult Ok(UserAccount account) => new(account, null, null);
    public static LoginResult Fail(string error, DateTimeOffset? retryAfter = null) => new(null, error, retryAfter);
}

/// <summary>
/// Verifies a login: rate-limits repeated failures per <c>username + remote IP</c>, looks up the
/// account, verifies the password hash (re-hashing + saving when the stored hash is from an older
/// algorithm), and returns the verified account. The caller (the <c>/login</c> page) signs the user
/// in via cookie authentication using the account's claims. On any failure it returns the same
/// generic "invalid username or password" message (it never reveals which field was wrong).
/// </summary>
public sealed class LoginService
{
    private readonly IUserAccountStore _accounts;
    private readonly PasswordHasher _hasher;
    private readonly ILoginRateLimiter _rateLimiter;
    private readonly ILogger<LoginService> _logger;

    /// <summary>
    /// The generic failure message (used for both a wrong username and a wrong password, so the
    /// response does not reveal which).
    /// </summary>
    public const string InvalidCredentials = "Invalid username or password.";

    /// <summary>
    /// The generic rate-limit message.
    /// </summary>
    public const string RateLimited = "Too many failed attempts. Please try again later.";

    /// <summary>
    /// Initializes the service.
    /// </summary>
    /// <param name="accounts">The account store.</param>
    /// <param name="hasher">The password hasher.</param>
    /// <param name="rateLimiter">The login rate limiter.</param>
    /// <param name="logger">A logger (records outcomes, never the password).</param>
    public LoginService(
        IUserAccountStore accounts,
        PasswordHasher hasher,
        ILoginRateLimiter rateLimiter,
        ILogger<LoginService> logger)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Verifies a login.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <param name="password">The plaintext password (never persisted or logged).</param>
    /// <param name="remoteIp">The remote IP (part of the rate-limit key).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A <see cref="LoginResult"/> (success + account, or the generic failure).</returns>
    public async Task<LoginResult> LoginAsync(
        string? username, string? password, string remoteIp, CancellationToken ct = default)
    {
        var key = $"{username?.Trim().ToLowerInvariant()}|{remoteIp}";

        // Already over budget? Reject before even looking up the account.
        if (_rateLimiter.IsBlocked(key))
        {
            return LoginResult.Fail(RateLimited, _rateLimiter.RetryAfter(key));
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return LoginResult.Fail(InvalidCredentials);
        }

        var account = await _accounts.FindByUsernameAsync(username, ct).ConfigureAwait(false);
        if (account is null)
        {
            // The username does not exist. Record the failure (the key includes the username) and
            // return the same generic message as a wrong password.
            _rateLimiter.RecordFailure(key);
            _logger.LogInformation("Login failure: unknown username '{Username}' from {Ip}.", username, remoteIp);
            return LoginResult.Fail(InvalidCredentials);
        }

        var result = _hasher.Verify(account.PasswordHash, password);
        if (result is PasswordVerificationResult.Failed)
        {
            _rateLimiter.RecordFailure(key);
            _logger.LogInformation("Login failure: bad password for '{Username}' from {Ip}.", username, remoteIp);
            return LoginResult.Fail(InvalidCredentials);
        }

        // Success. If the hash is from an older algorithm, transparently re-hash + save.
        if (result is PasswordVerificationResult.SuccessRehashNeeded)
        {
            var newHash = _hasher.Hash(password);
            await _accounts.UpdatePasswordHashAsync(account.Id, newHash, ct).ConfigureAwait(false);
            // Refresh the returned account's hash (it is the value the caller will see).
            account = new UserAccount
            {
                Id = account.Id,
                Username = account.Username,
                PasswordHash = newHash,
                Role = account.Role,
                ActorId = account.ActorId,
                NotificationsReadAt = account.NotificationsReadAt,
                CreatedAt = account.CreatedAt,
            };
        }

        _rateLimiter.Clear(key);
        _logger.LogInformation("Login success for '{Username}' from {Ip}.", username, remoteIp);
        return LoginResult.Ok(account);
    }
}
