using System.Security.Cryptography;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Iris.Server.Data.Stores;

/// <summary>
/// An EF Core (PostgreSQL) <see cref="IKeyStore"/>: the local instance's signing keys, persisted so a
/// signature made before a restart verifies identically after one (the private-key PEM is the lossless
/// form for all three supported algorithms).
/// </summary>
/// <remarks>
/// <see cref="IKeyStore"/> is synchronous (the signing pipeline calls it on the request path), so this
/// store opens a short-lived context per operation. It stores the private-key PEM (the canonical
/// round-trip form) and reconstructs the live key on read via <see cref="KeyPair.FromPem"/> /
/// <see cref="Ed25519Key.FromPem"/>.
/// </remarks>
public sealed class EfKeyStore : IKeyStore
{
    private readonly IDbContextFactory<IrisDbContext> _factory;

    /// <summary>
    /// Initializes the store over a context factory.
    /// </summary>
    /// <param name="factory">The <see cref="IrisDbContext"/> factory. Must not be null.</param>
    public EfKeyStore(IDbContextFactory<IrisDbContext> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc/>
    public bool TryGetKey(Iri keyId, out ISigningKey? key)
    {
        key = null;
        using var db = _factory.CreateDbContext();
        var entity = db.Set<KeyEntity>().AsNoTracking().FirstOrDefault(e => e.KeyId == keyId.Value);
        if (entity is null)
        {
            return false;
        }

        key = Reconstruct(entity);
        return key is not null;
    }

    /// <inheritdoc/>
    public void PutKey(ISigningKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var db = _factory.CreateDbContext();
        var existing = db.Set<KeyEntity>().FirstOrDefault(e => e.KeyId == key.KeyId.Value);
        if (existing is null)
        {
            db.Set<KeyEntity>().Add(new KeyEntity
            {
                KeyId = key.KeyId.Value,
                Algorithm = key.Algorithm.ToString(),
                PrivateKeyPem = SafePrivatePem(key),
                PublicKeyPem = key.ExportPublicKeyPem(),
            });
        }
        else
        {
            existing.Algorithm = key.Algorithm.ToString();
            existing.PrivateKeyPem = SafePrivatePem(key);
            existing.PublicKeyPem = key.ExportPublicKeyPem();
        }

        db.SaveChanges();
    }

    /// <inheritdoc/>
    public bool RemoveKey(Iri keyId)
    {
        using var db = _factory.CreateDbContext();
        var existing = db.Set<KeyEntity>().FirstOrDefault(e => e.KeyId == keyId.Value);
        if (existing is null)
        {
            return false;
        }

        db.Set<KeyEntity>().Remove(existing);
        db.SaveChanges();
        return true;
    }

    /// <summary>
    /// Reads the key's private-key PEM, tolerating a public-only key (stored as null).
    /// </summary>
    private static string? SafePrivatePem(ISigningKey key)
    {
        try
        {
            return key.ExportPrivateKeyPem();
        }
        catch (InvalidOperationException)
        {
            // A public-only key has no private PEM; store null (it can still verify, just not sign).
            return null;
        }
    }

    /// <summary>
    /// Reconstructs a live <see cref="ISigningKey"/> from its stored PEM. Returns null when the stored
    /// key cannot be reconstructed (a corrupt PEM) rather than throwing, so a single bad entry does not
    /// take down signature verification for the whole instance.
    /// </summary>
    private static ISigningKey? Reconstruct(KeyEntity entity)
    {
        try
        {
            if (!Enum.TryParse<KeyAlgorithm>(entity.Algorithm, out var algorithm))
            {
                return null;
            }

            var keyId = new Iri(entity.KeyId);
            if (algorithm == KeyAlgorithm.Ed25519)
            {
                return string.IsNullOrEmpty(entity.PrivateKeyPem)
                    ? null
                    : Ed25519Key.FromPem(entity.PrivateKeyPem, keyId);
            }

            // RSA / EC P-256: the private PEM round-trips the exact key material.
            return string.IsNullOrEmpty(entity.PrivateKeyPem) ? null : KeyPair.FromPem(entity.PrivateKeyPem, algorithm, keyId);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
