using Microsoft.JSInterop;

namespace Iris.WebCrypto.Tests;

/// <summary>
/// An <see cref="IJSRuntime"/> that simulates a page that did not load the <c>WebCrypto.js</c> bridge:
/// calling the named <c>webcryptoSignBootstrap.install</c> entry point throws a <see cref="JSException"/>
/// with the "not a function" message Blazor produces for an undefined global. Used to verify the
/// bootstrap surfaces a clear setup error.
/// </summary>
internal sealed class MissingBridgeJsRuntime : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => throw new JSException("window.webcryptoSignBootstrap.install is not a function");

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        => throw new JSException("window.webcryptoSignBootstrap.install is not a function");

    public ValueTask<TValue> InvokeUnmarshalledAsync<TValue>(string identifier, object?[]? args)
        => throw new JSException("not a function");

    public ValueTask<TValue> InvokeUnmarshalledAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        => throw new JSException("not a function");

    public ValueTask InvokeVoidAsync(string identifier, object?[]? args)
        => throw new JSException("not a function");

    public ValueTask InvokeVoidAsync(string identifier, CancellationToken cancellationToken, object?[]? args)
        => throw new JSException("not a function");

    public ValueTask InvokeVoidUnmarshalledAsync(string identifier, object?[]? args)
        => throw new JSException("not a function");

    public ValueTask InvokeVoidUnmarshalledAsync(string identifier, CancellationToken cancellationToken, object?[]? args)
        => throw new JSException("not a function");

    public ValueTask<IJSObjectReference> BeginInvokeDotNet(object dotNetObjectId, int sequence, object?[]? args)
        => throw new NotSupportedException("Not used by the WebCrypto bridge.");

    public ValueTask<TValue> InvokeDotNetFromJS<TValue>(object dotNetObjectId, int sequence, string identifier, object?[]? args)
        => throw new NotSupportedException("Not used by the WebCrypto bridge.");

    public ValueTask InvokeVoidDotNetFromJS(object dotNetObjectId, int sequence, string identifier, object?[]? args)
        => throw new NotSupportedException("Not used by the WebCrypto bridge.");
}
