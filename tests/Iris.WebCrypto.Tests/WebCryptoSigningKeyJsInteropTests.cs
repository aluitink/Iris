using Iris.Core;
using Iris.Core.Identity;
using Iris.WebCrypto;

namespace Iris.WebCrypto.Tests;

/// <summary>
/// Regression tests for the browser WebCrypto key-import path (change 226, finding A): the
/// <c>IJSRuntime</c> interop calls must never pass a <see cref="CancellationToken"/> as a JSON-
/// serialized argument. Blazor's JS-interop layer marshals the <c>object?[]</c> argument array through
/// <see cref="System.Text.Json"/>; a <see cref="CancellationToken"/> in there throws
/// <c>SerializeTypeInstanceNotSupported</c> at <c>CancellationToken.WaitHandle.Handle</c> (an
/// <see cref="IntPtr"/>), which is exactly what broke the documented Blazor WebAssembly log-on flow.
/// </summary>
public class WebCryptoSigningKeyJsInteropTests
{
    private const string Pem =
        "-----BEGIN PRIVATE KEY-----\nMIIBVAIBADANBg\n-----END PRIVATE KEY-----\n";

    [Fact]
    public async Task CreateAsync_InstallBootstrap_DoesNotPassCancellationTokenAsJsonArgument()
    {
        // Arrange — the fake JSON-serializes every interop argument, so a CancellationToken smuggled
        // into `args` throws exactly like the real Blazor JS-interop layer.
        WebCryptoBridgeBootstrap.ResetForTesting();
        var js = new FakeJsRuntime();
        var factory = new WebCryptoSigningKeyFactory(js);
        var keyIri = new Iri("https://a.domain.local/ap/v1/u/alice#main-key");

        // Act
        var key = await factory.CreateAsync(Pem, KeyAlgorithm.Rsa, keyIri);

        // Assert — the bridge bootstrap was injected and the key imported, with the cancellation token
        // handled by the dedicated InvokeAsync overload (never present in the JSON args).
        Assert.NotNull(key);
        Assert.Equal(1, js.CallCount("webcryptoSignBootstrap.install"));
        Assert.Equal(1, js.CallCount("webcryptoSign.importPrivateKey"));

        var install = js.Calls.Single(c => c.Identifier == "webcryptoSignBootstrap.install");
        // The only JSON argument to install() is the bridge source string.
        Assert.Single(install.Args);
        Assert.IsType<string>(install.Args[0]);
        Assert.DoesNotContain(install.Args, a => a is CancellationToken);

        var import = js.Calls.Single(c => c.Identifier == "webcryptoSign.importPrivateKey");
        Assert.Single(import.Args);
        Assert.Equal(Pem, import.Args[0]);

        Assert.Equal(keyIri, key.KeyId);
        Assert.Equal(KeyAlgorithm.Rsa, key.Algorithm);
    }

    [Fact]
    public async Task CreateAsync_SignsUsingWebCrypto_DoesNotPassCancellationTokenAsJsonArgument()
    {
        // Arrange
        WebCryptoBridgeBootstrap.ResetForTesting();
        var js = new FakeJsRuntime();
        var factory = new WebCryptoSigningKeyFactory(js);
        var keyIri = new Iri("https://a.domain.local/ap/v1/u/alice#main-key");
        var key = (ISigningKey)await factory.CreateAsync(Pem, KeyAlgorithm.Rsa, keyIri);

        // Act — sign through the (asynchronous) WebCrypto path with a live token.
        using var cts = new CancellationTokenSource();
        await key.SignAsync([1, 2, 3], cts.Token);

        // Assert — every interop argument is JSON-serializable (no CancellationToken leaked into args).
        foreach (var call in js.Calls)
        {
            Assert.DoesNotContain(call.Args, a => a is CancellationToken);
        }
    }

    [Fact]
    public async Task CreateAsync_BridgeNotLoaded_ThrowsClearSetupError()
    {
        // Arrange — a fake that simulates the page missing the webcryptoSignBootstrap global.
        WebCryptoBridgeBootstrap.ResetForTesting();
        var js = new MissingBridgeJsRuntime();
        var factory = new WebCryptoSigningKeyFactory(js);
        var keyIri = new Iri("https://a.domain.local/ap/v1/u/alice#main-key");

        // Act / Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.CreateAsync(Pem, KeyAlgorithm.Rsa, keyIri));
        Assert.Contains("webcryptoSignBootstrap", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<script", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
