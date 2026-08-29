using System.Text;
using Iris.Core;
using Microsoft.AspNetCore.Http;

namespace Iris.Server.Tests.Security;

/// <summary>
/// Unit tests for the F-21 key-rotation invalidation in <see cref="HttpSignatureValidator"/>. A
/// remote actor that rotates its key keeps the same key IRI, so the <see cref="RemoteKeyCache"/>
/// keeps serving the old public key until its TTL. The validator treats a verification <em>failure</em>
/// (distinct from a missing key) as the signal that the cached key is stale: it invalidates the entry
/// and re-resolves once (a fresh actor-document fetch) before re-verifying. These tests drive
/// <see cref="HttpSignatureValidator.ValidateAsync"/> directly with a scripted
/// <see cref="IInboundKeyResolver"/> (no live HTTP) to assert the invalidate + re-resolve contract.
/// </summary>
public sealed class KeyRotationInvalidationTests
{
    private static readonly Iri KeyId = new("https://a.domain.local/u/alice#key-1");

    [Fact]
    public async Task VerifyFails_ThenCacheInvalidated_AndReResolved_Once()
    {
        // The first resolution returns a key that does NOT verify the (validly-signed) request —
        // the stale, pre-rotation key. After invalidation the re-resolution returns the rotated key,
        // which does verify.
        var staleKey = KeyPairGenerator.GenerateRsa(KeyId);
        var rotatedKey = KeyPairGenerator.GenerateRsa(KeyId);

        var resolver = new ScriptedKeyResolver(KeyId, new ISigningKey?[] { staleKey, rotatedKey });
        var cache = new CountingKeyCache();
        var validator = new HttpSignatureValidator(resolver, new RealVerifier(), cache);

        // Sign with the ROTATED key (the actor's current real key) so the first resolution (the stale
        // key) fails to verify, and the post-invalidation re-resolution (the rotated key) verifies.
        var (context, _) = BuildSignedContext(rotatedKey, KeyId);
        var result = await validator.ValidateAsync(context);

        Assert.NotNull(result);
        Assert.True(result!.IsValid, "After invalidation + re-resolution the rotated key must verify");
        // The cache was invalidated (the stale entry removed) and re-resolved once.
        Assert.True(cache.InvalidateCallCount == 1, "A verification failure must invalidate the cached key");
        Assert.True(resolver.ResolveCallCount == 2, "The key must be re-resolved exactly once after a failure");
    }

    [Fact]
    public async Task VerifyFails_AndReResolveReturnsNull_IsInvalid_NoSecondVerify()
    {
        // First resolution returns a key that fails to verify; after invalidation the re-resolution
        // returns null (the actor now has no resolvable key). The request is invalid and the verifier
        // is not called a second time.
        var staleKey = KeyPairGenerator.GenerateRsa(KeyId);

        var resolver = new ScriptedKeyResolver(KeyId, new ISigningKey?[] { staleKey, null });
        var cache = new CountingKeyCache();
        var validator = new HttpSignatureValidator(resolver, new RealVerifier(), cache);

        // Sign with a key that is NEITHER of the resolver's two returns: the first resolution
        // (staleKey) fails to verify, the re-resolution (null) yields no key, so the request is
        // invalid and no second verify occurs.
        var (context, _) = BuildSignedContext(KeyPairGenerator.GenerateRsa(KeyId), KeyId);
        var result = await validator.ValidateAsync(context);

        Assert.NotNull(result);
        Assert.False(result!.IsValid);
        Assert.Equal(1, cache.InvalidateCallCount);
        Assert.Equal(2, resolver.ResolveCallCount);
    }

    [Fact]
    public async Task KeyMissing_DoesNotInvalidate_Cache()
    {
        // A missing key (the resolver returns null up front) is NOT a rotation signal: no
        // invalidation is attempted and the key is not re-resolved.
        var resolver = new ScriptedKeyResolver(KeyId, new ISigningKey?[] { null });
        var cache = new CountingKeyCache();
        var validator = new HttpSignatureValidator(resolver, new RealVerifier(), cache);

        var (context, _) = BuildSignedContext(KeyPairGenerator.GenerateRsa(KeyId), KeyId);
        var result = await validator.ValidateAsync(context);

        Assert.NotNull(result);
        Assert.False(result!.IsValid);
        Assert.True(cache.InvalidateCallCount == 0, "A missing key must not trigger invalidation");
        Assert.True(resolver.ResolveCallCount == 1, "A missing key must not be re-resolved");
    }

    [Fact]
    public async Task VerifySucceeds_DoesNotInvalidate_Cache()
    {
        // A verification success (the first key verifies) must not invalidate the cache — the key is
        // not stale.
        var key = KeyPairGenerator.GenerateRsa(KeyId);

        var resolver = new ScriptedKeyResolver(KeyId, new ISigningKey?[] { key });
        var cache = new CountingKeyCache();
        var validator = new HttpSignatureValidator(resolver, new RealVerifier(), cache);

        var (context, _) = BuildSignedContext(key, KeyId);
        var result = await validator.ValidateAsync(context);

        Assert.NotNull(result);
        Assert.True(result!.IsValid);
        Assert.Equal(0, cache.InvalidateCallCount);
        Assert.Equal(1, resolver.ResolveCallCount);
    }

    [Fact]
    public async Task NoCacheProvided_FailureDoesNotThrow_AndNoReResolve()
    {
        // When no cache is supplied (e.g. a host that disabled key caching), a verification failure
        // is reported as invalid without invalidation or re-resolution (there is nothing to
        // invalidate), and no exception is thrown.
        // The resolver returns `key`, but the request is signed with a DIFFERENT key, so the
        // (single) verification fails. With no cache, the validator reports invalid without
        // invalidation or re-resolution (there is nothing to invalidate), and no exception is thrown.
        var key = KeyPairGenerator.GenerateRsa(KeyId);
        var signingKey = KeyPairGenerator.GenerateRsa(KeyId);

        var resolver = new ScriptedKeyResolver(KeyId, new ISigningKey?[] { key });
        var validator = new HttpSignatureValidator(resolver, new RealVerifier(), remoteKeyCache: null);

        var (context, _) = BuildSignedContext(signingKey, KeyId);
        var result = await validator.ValidateAsync(context);

        Assert.NotNull(result);
        Assert.False(result!.IsValid);
        Assert.True(resolver.ResolveCallCount == 1, "With no cache, a failure must not re-resolve");
    }

    // --- Helpers ---------------------------------------------------------------

    /// <summary>
    /// Builds an <see cref="HttpContext"/> carrying a validly-signed request (signed with
    /// <paramref name="signingKey"/>) to a POST inbox, and returns the raw <c>Signature</c> header
    /// value. The signature is always valid for the metadata it is built from, so a verification
    /// failure in the tests below is produced purely by the resolver returning a key that does not
    /// match (the stale key) — the F-21 rotation scenario.
    /// </summary>
    private static (HttpContext, string) BuildSignedContext(KeyPair signingKey, Iri keyId)
    {
        var store = new InMemoryKeyStore();
        store.PutKey(signingKey);
        var identity = new SystemIdentity(new Iri("https://a.domain.local/u/alice"), keyId);
        var signer = new HttpSignatureSigner(store);

        var body = Encoding.UTF8.GetBytes("{\"id\":\"https://a.domain.local/a/1\"}");
        var digest = Signatures.ComputeDigest(body);
        var metadata = new HttpRequestMetadata(
            method: "POST",
            pathAndQuery: "/ap/v1/u/alice/inbox",
            host: "a.domain.local",
            date: "Tue, 26 Aug 2026 12:00:00 GMT",
            contentType: "application/activity+json",
            body: body,
            headers: new Dictionary<string, string> { ["digest"] = digest });
        var signatureHeader = signer.Sign(metadata, identity, SigningProfile.ServerToServer);

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/ap/v1/u/alice/inbox";
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString("a.domain.local");
        context.Request.ContentType = "application/activity+json";
        context.Request.Headers["Date"] = "Tue, 26 Aug 2026 12:00:00 GMT";
        context.Request.Headers[Signatures.SignatureHeaderName] = signatureHeader;
        context.Request.Body = new MemoryStream(body);

        // The digest is a content header; the validator reads it via Request.Headers (the combined
        // view of message + content headers).
        context.Request.Headers["Digest"] = digest;

        return (context, signatureHeader);
    }

    /// <summary>
    /// An <see cref="IInboundKeyResolver"/> that returns a scripted sequence of keys (one per call,
    /// reusing the last once exhausted) and records how many times it was resolved and invalidated.
    /// </summary>
    private sealed class ScriptedKeyResolver(Iri keyId, IReadOnlyList<ISigningKey?> keys) : IInboundKeyResolver
    {
        private readonly Iri _keyId = keyId;
        private readonly IReadOnlyList<ISigningKey?> _keys = keys;

        /// <summary>
        /// The number of <see cref="ResolveAsync"/> calls.
        /// </summary>
        public int ResolveCallCount { get; private set; }

        public Task<ISigningKey?> ResolveAsync(Iri keyId, CancellationToken ct = default)
        {
            ResolveCallCount++;
            // A distinct key per call: the first call returns the first key, the second the second,
            // and any further call reuses the last (so a third resolve is still observable as a
            // regression).
            var index = Math.Min(ResolveCallCount - 1, _keys.Count - 1);
            return Task.FromResult(_keys[index]);
        }
    }

    /// <summary>
    /// A <see cref="RemoteKeyCache"/> that counts <see cref="Invalidate"/> calls (the real cache is
    /// unused by the validator in these tests — only its <c>Invalidate</c> is consulted), so the
    /// tests can assert the invalidate contract without a live cache.
    /// </summary>
    private sealed class CountingKeyCache : RemoteKeyCache
    {
        public CountingKeyCache() : base(policy: CachePolicy.Key, capacity: 16)
        {
        }

        public int InvalidateCallCount { get; private set; }

        public override bool Invalidate(Iri key)
        {
            InvalidateCallCount++;
            return base.Invalidate(key);
        }
    }

    /// <summary>
    /// An <see cref="ISignatureVerifier"/> backed by a real <see cref="HttpSignatureVerifier"/> over an
    /// in-memory key store, so the cryptographic check is genuine (a stale key does not verify, the
    /// matching key does).
    /// </summary>
    private sealed class RealVerifier : ISignatureVerifier
    {
        private readonly InMemoryKeyStore _store = new();

        public RealVerifier()
        {
            // The verifier needs a store for the key-less Verify overload; the key-supplied overload
            // (the one the validator uses) ignores the store.
        }

        public bool Verify(HttpRequestMetadata metadata, string signatureHeader)
            => new HttpSignatureVerifier(_store).Verify(metadata, signatureHeader);

        public bool Verify(HttpRequestMetadata metadata, ISigningKey key, string signatureHeader)
            => new HttpSignatureVerifier(_store).Verify(metadata, key, signatureHeader);
    }
}
