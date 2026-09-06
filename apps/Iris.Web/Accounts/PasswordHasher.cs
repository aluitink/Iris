using Iris.Server.Data.Accounts;
using Microsoft.AspNetCore.Identity;

namespace Iris.Web.Accounts;

/// <summary>
/// The password hasher used by registration and login. Wraps ASP.NET Core's
/// <see cref="PasswordHasher{TUser}"/> (PBKDF2-HMAC-SHA256, a per-password salt, and a versioned
/// hash format) so the rest of the app never touches the raw Identity type. The hash is the only
/// thing persisted; the plaintext password is never stored or logged.
/// </summary>
/// <remarks>
/// The versioned format means a future algorithm upgrade re-hashes transparently on the next login:
/// <see cref="Verify"/> returns <c>SuccessRehashNeeded</c> when the stored hash was produced by an
/// older algorithm, and the caller re-hashes + saves.
/// </remarks>
public sealed class PasswordHasher
{
    private readonly PasswordHasher<UserAccount> _inner = new();

    /// <summary>
    /// Produces the versioned hash for a plaintext password.
    /// </summary>
    /// <param name="plaintext">The plaintext password (never persisted).</param>
    /// <returns>The versioned hash string (persisted in <see cref="UserAccount.PasswordHash"/>).</returns>
    public string Hash(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        return _inner.HashPassword(null!, plaintext);
    }

    /// <summary>
    /// Verifies a plaintext password against a stored hash.
    /// </summary>
    /// <param name="storedHash">The stored versioned hash.</param>
    /// <param name="plaintext">The plaintext candidate password (never persisted).</param>
    /// <returns>
    /// <see cref="PasswordVerificationResult.Success"/> when the password matches;
    /// <c>SuccessRehashNeeded</c> when it matches but the hash should be re-hashed with the current
    /// algorithm; <c>Failed</c> when it does not match (or the stored value is not a valid hash).
    /// </returns>
    public PasswordVerificationResult Verify(string storedHash, string plaintext)
    {
        if (string.IsNullOrWhiteSpace(storedHash))
        {
            return PasswordVerificationResult.Failed;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        return _inner.VerifyHashedPassword(null!, storedHash, plaintext);
    }
}
