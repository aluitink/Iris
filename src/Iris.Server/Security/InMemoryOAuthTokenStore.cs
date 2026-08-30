using System.Collections.Concurrent;
using Iris.Core;

namespace Iris.Server.Security;

/// <summary>
/// An in-memory <see cref="IOAuthTokenStore"/>. Suitable for single-instance development and testing;
/// a production deployment would use a database-backed or Redis-backed store.
/// </summary>
public sealed class InMemoryOAuthTokenStore : IOAuthTokenStore
{
    private readonly ConcurrentDictionary<string, Iri> _codes = new();
    private readonly ConcurrentDictionary<string, Iri> _tokens = new();
    private readonly ConcurrentDictionary<string, Iri> _refreshTokens = new();

    /// <inheritdoc/>
    public Task StoreAuthorizationCodeAsync(string code, Iri actorIri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        _codes[code] = actorIri;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<Iri?> RedeemAuthorizationCodeAsync(string code, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (_codes.TryRemove(code, out var actorIri))
        {
            return Task.FromResult<Iri?>(actorIri);
        }

        return Task.FromResult<Iri?>(null);
    }

    /// <inheritdoc/>
    public Task StoreTokenAsync(string token, Iri actorIri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        _tokens[token] = actorIri;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<Iri?> ResolveTokenAsync(string token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (_tokens.TryGetValue(token, out var actorIri))
        {
            return Task.FromResult<Iri?>(actorIri);
        }

        return Task.FromResult<Iri?>(null);
    }

    /// <inheritdoc/>
    public Task RevokeTokenAsync(string token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        _tokens.TryRemove(token, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StoreRefreshTokenAsync(string refreshToken, Iri actorIri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        _refreshTokens[refreshToken] = actorIri;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<Iri?> RedeemRefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        if (_refreshTokens.TryRemove(refreshToken, out var actorIri))
        {
            return Task.FromResult<Iri?>(actorIri);
        }

        return Task.FromResult<Iri?>(null);
    }
}
