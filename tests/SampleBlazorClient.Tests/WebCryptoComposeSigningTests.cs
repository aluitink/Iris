using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Samples.SampleBlazorClient;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// S1: the broken Compose (signed Create) write path. In the Blazor WASM explorer the client signs its
/// outgoing ActivityPub requests with the browser's WebCrypto (<c>crypto.subtle</c>, RSASSA-PKCS1-v1_5 +
/// SHA-256) via the <c>Iris.WebCrypto</c> library's <c>WebCryptoSigningKey</c>, loaded through the
/// <c>keyFactory</c> seam (PEM + algorithm + key id → <see cref="ISigningKey"/>); the server verifies
/// with its BCL <see cref="KeyPair"/>. The BCL path is proven working (the OAuth2 browser-flow and
/// reply integration tests sign a <c>PostNoteAsync</c> against an in-process TestServer). This suite
/// drives the <em>same</em> production path —
/// <see cref="SampleBlazorClient.CreateClientService"/> + <c>PostNoteAsync</c> over a real
/// <see cref="HttpSignatureValidator"/> (TestServer) — but with a key whose <c>SignAsync</c> performs the
/// WebCrypto primitive (a BCL RSA in RSASSA-PKCS1-v1_5/SHA-256, byte-identical to
/// <c>crypto.subtle.sign</c>), so no browser is required.
/// </summary>
public sealed class WebCryptoComposeSigningTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string Handle = "alice";

    private readonly TestServer _server;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _actorIri;
    private readonly Iri _keyIri;
    private readonly KeyPair _bclKey;

    public WebCryptoComposeSigningTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        var seeded = TestSeeder.SeedPersonWithKey(_persistence, Host, Handle);
        _bclKey = seeded.Key;
        _actorIri = seeded.ActorIri;
        _keyIri = seeded.KeyId;

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Handle,
            Persistence = _persistence,
            // Serve the actor document in-process (no network fetch) so the inbound key resolver reads
            // the seeded public key, and gate the owner-only document with the sample's Basic-auth creds.
            Fetcher = new PersistenceActorFetcher(_persistence),
            CredentialValidator = new BasicAuthCredentialValidator((_, user, password) =>
                new ValueTask<bool>(user == Handle && password == SampleBlazorClient.SamplePassword)),
        });
    }

    public void Dispose() => _server.Dispose();

    /// <summary>
    /// The WebCrypto key factory the Blazor WASM host uses (the
    /// <see cref="Iris.WebCrypto.WebCryptoSigningKeyFactory"/> role). Instead of importing the PKCS#8 PEM
    /// into the browser's <c>crypto.subtle</c>, it loads the SAME private key into a BCL
    /// <see cref="WebCryptoSigningKeyStandIn"/> (an <see cref="ISigningKey"/> whose <c>SignAsync</c>
    /// performs the WebCrypto primitive — RSASSA-PKCS1-v1_5 + SHA-256 — and whose synchronous
    /// <c>Sign</c>/export methods are unsupported, exactly like the real WebCrypto key). This is the seam
    /// that lets a non-browser host exercise the browser's signing path.
    /// </summary>
    private Func<string, KeyAlgorithm, Iri, CancellationToken, Task<ISigningKey>> WebCryptoKeyFactory()
        => (pem, algorithm, keyId, ct) => Task.FromResult<ISigningKey>(
            new WebCryptoSigningKeyStandIn((KeyPair)KeyPem.Load(pem, algorithm, keyId), keyId));

    /// <summary>
    /// The browser's WebCrypto signing, mirrored in-process: an <see cref="ISigningKey"/> whose
    /// <c>SignAsync</c> performs RSASSA-PKCS1-v1_5 + SHA-256 (exactly what
    /// <c>crypto.subtle.sign({name:"RSASSA-PKCS1-v1_5"}, key, data)</c> does). The synchronous
    /// <c>Sign</c>/export methods are unsupported, just like the real <c>WebCryptoSigningKey</c>, so any
    /// path that requires them fails loudly.
    /// </summary>
    private sealed class WebCryptoSigningKeyStandIn(KeyPair inner, Iri keyIri) : ISigningKey
    {
        private readonly RSA _rsa = (RSA)inner.Key;

        public KeyAlgorithm Algorithm => KeyAlgorithm.Rsa;
        public Iri KeyId => keyIri;

        public byte[] Sign(byte[] data) => throw new PlatformNotSupportedException();

        public Task<byte[]> SignAsync(byte[] data, CancellationToken ct = default)
            => Task.FromResult(_rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        public bool Verify(byte[] data, byte[] signature)
            => _rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        public string GetPublicJwk() => inner.GetPublicJwk();
        public string ExportPublicKeyPem() => inner.ExportPublicKeyPem();
        public string ExportPrivateKeyPem() => throw new PlatformNotSupportedException();
        public string GetThumbprint() => inner.GetThumbprint();
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that serves the actor document from in-process persistence
    /// (no network), so the inbound key resolver resolves the sender's public key for signature
    /// verification.
    /// </summary>
    private sealed class PersistenceActorFetcher(IPersistenceProvider persistence) : IActorDocumentFetcher
    {
        public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => await persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct) ? actor : null;
    }

    /// <summary>
    /// The exact production path the Blazor WASM explorer takes: log on with the WebCrypto key factory,
    /// then <c>PostNoteAsync</c> (the Compose button). The signed outbox Create must validate on the
    /// server (not 401) and the note must land in the actor's outbox.
    /// </summary>
    [Fact]
    public async Task WebCryptoSigned_PostNote_ValidateOnServer_AndNoteLandsInOutbox()
    {
        var service = SampleBlazorClient.CreateClientService(
            new Uri($"https://{Host}"),
            Handle,
            SampleBlazorClient.SamplePassword,
            transportFactory: () => _server.CreateHandler(),
            actorIriOverride: _actorIri,
            keyFactory: WebCryptoKeyFactory());

        try
        {
            var logged = await service.LoginAsync();
            Assert.True(logged, "the WebCrypto logon must load the actor's private key");

            var client = service.GetClient();
            var result = await client.PostNoteAsync(_actorIri, "S1: webcrypto compose post");

            Assert.True(
                result.IsSuccess,
                $"the WebCrypto-signed Compose (PostNoteAsync) must validate on the server (got HTTP {result.StatusCode}: {result.Body})");

            // The point of the fix: the post is not silently lost — the note is in the actor's outbox.
            // (GetCollectionItemsAsync yields the outbox as batches of items.)
            var allItems = new List<IObjectOrLink>();
            await foreach (var batch in client.GetCollectionItemsAsync(_actorIri.OutboxOf()))
            {
                allItems.AddRange(batch);
            }

            var posted = allItems.Any(i =>
                i is Create c && c.Object?.OfType<Note>().Any(n => n.Content?.FirstOrDefault() == "S1: webcrypto compose post") == true);
            Assert.True(posted, "the posted note must be in the actor's outbox");
        }
        finally
        {
            service.Dispose();
        }
    }

    /// <summary>
    /// The contrast that pins the diagnosis: the SAME production path, but with the default BCL key
    /// loader (<c>keyFactory: null</c>). This must also validate — proving the pipeline, base-string, and
    /// server verifier are all correct, and isolating any failure to the WebCrypto (async/JS-interop)
    /// signing path specifically.
    /// </summary>
    [Fact]
    public async Task BclSigned_PostNote_ValidateOnServer()
    {
        var service = SampleBlazorClient.CreateClientService(
            new Uri($"https://{Host}"),
            Handle,
            SampleBlazorClient.SamplePassword,
            transportFactory: () => _server.CreateHandler(),
            actorIriOverride: _actorIri,
            keyFactory: null);

        try
        {
            var logged = await service.LoginAsync();
            Assert.True(logged, "the BCL logon must load the actor's private key");

            var client = service.GetClient();
            var result = await client.PostNoteAsync(_actorIri, "S1: bcl compose post (control)");

            Assert.True(
                result.IsSuccess,
                $"the BCL-signed Compose (PostNoteAsync) must validate on the server (got HTTP {result.StatusCode}: {result.Body})");
        }
        finally
        {
            service.Dispose();
        }
    }
}
