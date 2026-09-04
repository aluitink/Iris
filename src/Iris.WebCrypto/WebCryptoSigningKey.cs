using System.Text;
using Iris.Core;
using Iris.Core.Identity;
using Microsoft.JSInterop;

namespace Iris.WebCrypto;

/// <summary>
/// An <see cref="ISigningKey"/> whose RSA signing is performed by the browser's WebCrypto
/// (<c>crypto.subtle</c>) via JS interop, because the .NET-on-WASM BCL has no usable RSA
/// implementation (<c>RSA.Create()</c> + <c>ImportFromPem</c> throws
/// <c>ArgumentException "Arg_PlatformNotSupported"</c> in a Blazor WebAssembly host).
/// </summary>
/// <remarks>
/// The key material is imported once (asynchronously) into a page-lifetime WebCrypto registry
/// (the <c>WebCrypto.js</c> bridge, auto-injected into the page — see
/// <see cref="WebCryptoBridgeBootstrap"/>) and addressed by an opaque id. <see cref="SignAsync"/>
/// awaits the browser's RSASSA-PKCS1-v1_5 + SHA-256 signing — the exact primitive the BCL signer
/// uses (<c>rsa.SignData(data, SHA256, Pkcs1)</c>) — so signatures produced here verify on the
/// server exactly like the BCL-produced ones. The synchronous <see cref="ISigningKey.Sign"/> method
/// is not supported (WebCrypto is inherently asynchronous); the signing pipeline calls
/// <see cref="SignAsync"/>. Public-key export methods are likewise unavailable in the browser (the
/// actor document already carries the server-issued <c>publicKeyPem</c> / JWK, so the client never
/// re-exports them).
/// </remarks>
public sealed class WebCryptoSigningKey : ISigningKey, IDisposable
{
    private readonly IJSRuntime _js;
    private readonly int _keyId;
    private bool _disposed;

    internal WebCryptoSigningKey(IJSRuntime js, int keyId, KeyAlgorithm algorithm, Iri keyIri)
    {
        _js = js ?? throw new ArgumentNullException(nameof(js));
        _keyId = keyId;
        Algorithm = algorithm;
        KeyId = keyIri;
    }

    /// <inheritdoc/>
    public KeyAlgorithm Algorithm { get; }

    /// <inheritdoc/>
    public Iri KeyId { get; }

    /// <summary>
    /// Imports the PKCS#8 RSA private key (the actor document's owner-only <c>privateKey</c>) into
    /// the browser's WebCrypto and returns a key that can sign. The import is asynchronous
    /// (<c>crypto.subtle.importKey</c>).
    /// </summary>
    /// <param name="js">The JS runtime (Blazor WebAssembly). Not disposed by the returned key.</param>
    /// <param name="pem">The PKCS#8 PEM private key.</param>
    /// <param name="keyIri">The IRI that identifies the key (the actor's <c>publicKey.id</c>).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A WebCrypto-backed signing key.</returns>
    public static async Task<WebCryptoSigningKey> CreateAsync(
        IJSRuntime js, string pem, Iri keyIri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(pem);
        await WebCryptoBridgeBootstrap.EnsureInjectedAsync(js, ct).ConfigureAwait(false);
        var keyId = await js.InvokeAsync<int>("webcryptoSign.importPrivateKey", pem).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new WebCryptoSigningKey(js, keyId, KeyAlgorithm.Rsa, keyIri);
    }

    /// <inheritdoc/>
    /// <remarks>Not supported in the browser (WebCrypto is asynchronous); use <see cref="SignAsync"/>.</remarks>
    public byte[] Sign(byte[] data)
        => throw new PlatformNotSupportedException(
            "WebCrypto signing is asynchronous; the pipeline must call SignAsync.");

    /// <inheritdoc/>
    public async Task<byte[]> SignAsync(byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var b64 = await _js.InvokeAsync<string>("webcryptoSign.sign", _keyId, Convert.ToBase64String(data))
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return Convert.FromBase64String(b64);
    }

    /// <inheritdoc/>
    /// <remarks>Verification is available (the browser can verify RSASSA-PKCS1-v1_5), but the
    /// client's runtime path never verifies with its own acting key; this is provided for
    /// completeness and uses the same registered key.</remarks>
    public async Task<bool> VerifyAsync(byte[] data, byte[] signature, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _js.InvokeAsync<bool>(
            "webcryptoSign.verify", _keyId, Convert.ToBase64String(data), Convert.ToBase64String(signature))
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public bool Verify(byte[] data, byte[] signature)
        => throw new PlatformNotSupportedException(
            "WebCrypto verification is asynchronous; call VerifyAsync.");

    /// <inheritdoc/>
    public string GetPublicJwk()
        => ThrowExportNotSupported();

    /// <inheritdoc/>
    public string ExportPublicKeyPem()
        => ThrowExportNotSupported();

    /// <inheritdoc/>
    public string ExportPrivateKeyPem()
        => ThrowExportNotSupported();

    /// <inheritdoc/>
    public string GetThumbprint()
        => ThrowExportNotSupported();

    private string ThrowExportNotSupported()
        => throw new PlatformNotSupportedException(
            "Public/private key export is not available in the browser; the actor document already " +
            "carries the server-issued publicKeyPem / JWK.");

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Free the WebCrypto registry entry. IJSVoid is fire-and-forget on dispose; a failure here
        // (e.g. the page is tearing down) must not throw from Dispose.
        try
        {
            _ = _js.InvokeVoidAsync("webcryptoSign.free", _keyId);
        }
        catch
        {
            // Best-effort cleanup on dispose; ignore JS-interop failures (page unloading, etc.).
        }
    }
}

/// <summary>
/// Ensures the <c>WebCrypto.js</c> bridge is present in the page before the first signing call. The
/// host includes the bridge once (a single <c>&lt;script src=".../WebCrypto.js"&gt;</c> — see the
/// package README); that script defines both the signing surface (<c>window.webcryptoSign</c>) and the
/// named <c>window.webcryptoSignBootstrap.install</c> entry point. This bootstrap calls that named
/// entry point with the embedded bridge source so hosts that load the bridge lazily (or after a
/// client-side navigation that cleared the globals) still end up with <c>window.webcryptoSign</c>
/// defined.
/// </summary>
/// <remarks>
/// Blazor's <see cref="IJSRuntime"/> can only invoke <em>named</em> global functions — it rejects
/// inline JS string expressions (verified: <c>InvokeAsync("() =&gt; 1+1")</c> throws
/// <c>"is not a function"</c>). Consequently the library cannot inject the bridge with zero host-side
/// JS; it relies on the one named global the host's <c>&lt;script&gt;</c> tag defines. If that global
/// is absent (the host did not include the bridge), the first signing call throws a clear
/// <see cref="InvalidOperationException"/> explaining the one-line setup step. All interop is
/// asynchronous — no synchronous <c>IJSRuntime</c> calls (which would deadlock on the single-threaded
/// Blazor WASM UI sync context).
/// </remarks>
internal static class WebCryptoBridgeBootstrap
{
    private static readonly object Gate = new();
    private static Task? _injected;

    /// <summary>
    /// Ensures the WebCrypto bridge is present in the page. No-op if it has already been ensured on a
    /// previous call.
    /// </summary>
    /// <param name="js">The JS runtime used to call the bridge's named bootstrap entry point.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task EnsureInjectedAsync(IJSRuntime js, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(js);

        // Fast path: already ensured on a previous call.
        if (_injected is not null)
        {
            await _injected.ConfigureAwait(false);
            return;
        }

        lock (Gate)
        {
            _injected ??= InjectCoreAsync(js, ct);
        }

        await _injected.ConfigureAwait(false);
    }

    /// <summary>
    /// Resets the injection state so the next call to <see cref="EnsureInjectedAsync"/> re-injects the
    /// bridge. Exposed for the dedicated test project (via <c>InternalsVisibleTo</c>) so each test
    /// starts from a clean process state; not part of the public API.
    /// </summary>
    internal static void ResetForTesting()
    {
        lock (Gate)
        {
            _injected = null;
        }
    }

    private static async Task InjectCoreAsync(IJSRuntime js, CancellationToken ct)
    {
        try
        {
            // Call the host-provided named bootstrap entry point with the embedded bridge source. The
            // host's <script src=".../WebCrypto.js"> defines window.webcryptoSignBootstrap.install;
            // if the bridge is already present install() is a no-op returning true, so this is safe to
            // call unconditionally (and covers lazy-load / post-navigation re-injection).
            var source = LoadBridgeSource();
            // Pass `ct` as the dedicated cancellation-token parameter (the `InvokeAsync<T>(string,
            // CancellationToken, object?[])` overload) so it is never boxed into the JSON-serialized
            // `args`. Passing it as a trailing JSON argument (the old `(string, object?[])` binding)
            // made the JS-interop layer serialize the `CancellationToken`, which throws
            // `SerializeTypeInstanceNotSupported` at `CancellationToken.WaitHandle.Handle`
            // (`System.IntPtr`) — see change 226 finding A.
            await js.InvokeAsync<bool>("webcryptoSignBootstrap.install", ct, [source]).ConfigureAwait(false);
        }
        catch (JSException ex) when (ex.Message.Contains("webcryptoSignBootstrap", StringComparison.OrdinalIgnoreCase)
                                       || ex.Message.Contains("not a function", StringComparison.OrdinalIgnoreCase))
        {
            // The named bootstrap global is missing: the host did not include the bridge. Reset so a
            // later retry (after the host adds the script) can succeed, and surface the setup step.
            _injected = null;
            throw new InvalidOperationException(
                "The Iris.WebCrypto bridge (WebCrypto.js) is not loaded in the page. Add a single " +
                "<script src=\"js/WebCrypto.js\"></script> tag to index.html (the package README " +
                "shows the exact step) so window.webcryptoSignBootstrap is defined, then retry.",
                ex);
        }
        catch
        {
            _injected = null;
            throw;
        }
    }

    private static string LoadBridgeSource()
    {
        var asm = typeof(WebCryptoBridgeBootstrap).Assembly;
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("WebCrypto.js", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Embedded WebCrypto.js resource not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
