using System.Text.Json;
using Microsoft.JSInterop;

namespace Iris.WebCrypto.Tests;

/// <summary>
/// An in-process <see cref="IJSRuntime"/> that records every interop call and — critically —
/// serializes each non-null element of the argument array through <see cref="JsonSerializer"/>
/// exactly the way Blazor's JS-interop layer does before handing it to the browser.
/// </summary>
/// <remarks>
/// The real <c>IJSRuntime</c> marshals the <c>object?[]</c> argument array to JS by running every
/// element through <c>System.Text.Json</c>. So any element that is not JSON-serializable (e.g. a
/// <see cref="CancellationToken" />, whose <c>WaitHandle.Handle</c> is a <see cref="IntPtr"/>) throws
/// a <c>JsonException</c> (<c>SerializeTypeInstanceNotSupported</c>) here — reproducing in-process the
/// exact failure the browser path used to throw (change 226, finding A). A correct call passes the
/// <see cref="CancellationToken"/> to the dedicated <c>InvokeAsync</c> overload (not into the args
/// array), so this fake never sees it.
/// </remarks>
internal sealed class FakeJsRuntime : IJSRuntime
{
    private readonly List<(string Identifier, object?[] Args)> _calls = [];

    /// <summary>Gets the recorded interop calls, in order.</summary>
    public IReadOnlyList<(string Identifier, object?[] Args)> Calls => _calls;

    /// <summary>Gets the number of times the named <paramref name="identifier"/> was invoked.</summary>
    public int CallCount(string identifier) => _calls.Count(c => c.Identifier == identifier);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => InvokeCoreAsync<TValue>(identifier, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        => InvokeCoreAsync<TValue>(identifier, args);

    public ValueTask<TValue> InvokeUnmarshalledAsync<TValue>(string identifier, object?[]? args)
        => InvokeCoreAsync<TValue>(identifier, args);

    public ValueTask<TValue> InvokeUnmarshalledAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        => InvokeCoreAsync<TValue>(identifier, args);

    public ValueTask InvokeVoidAsync(string identifier, object?[]? args)
        => InvokeVoidCoreAsync(identifier, args);

    public ValueTask InvokeVoidAsync(string identifier, CancellationToken cancellationToken, object?[]? args)
        => InvokeVoidCoreAsync(identifier, args);

    public ValueTask InvokeVoidUnmarshalledAsync(string identifier, object?[]? args)
        => InvokeVoidCoreAsync(identifier, args);

    public ValueTask InvokeVoidUnmarshalledAsync(string identifier, CancellationToken cancellationToken, object?[]? args)
        => InvokeVoidCoreAsync(identifier, args);

    public ValueTask<IJSObjectReference> BeginInvokeDotNet(object dotNetObjectId, int sequence, object?[]? args)
        => throw new NotSupportedException("BeginInvokeDotNet is not used by the WebCrypto bridge.");

    public ValueTask<TValue> InvokeDotNetFromJS<TValue>(object dotNetObjectId, int sequence, string identifier, object?[]? args)
        => throw new NotSupportedException("InvokeDotNetFromJS is not used by the WebCrypto bridge.");

    public ValueTask InvokeVoidDotNetFromJS(object dotNetObjectId, int sequence, string identifier, object?[]? args)
        => throw new NotSupportedException("InvokeVoidDotNetFromJS is not used by the WebCrypto bridge.");

    private ValueTask<TValue> InvokeCoreAsync<TValue>(string identifier, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        args ??= [];

        // Mirror the real JS-interop layer: every element must survive JSON serialization.
        foreach (var arg in args)
        {
            if (arg is null)
                continue;
            try
            {
                _ = JsonSerializer.SerializeToElement(arg);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"JS-interop argument of type {arg.GetType().Name} for '{identifier}' is not " +
                    $"JSON-serializable; pass a cancellation token to the dedicated InvokeAsync overload " +
                    $"instead of as a JSON argument. {ex.Message}",
                    ex);
            }
        }

        _calls.Add((identifier, args));
        return new ValueTask<TValue>(ResultFor<TValue>());
    }

    private ValueTask InvokeVoidCoreAsync(string identifier, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        args ??= [];

        foreach (var arg in args)
        {
            if (arg is null)
                continue;
            try
            {
                _ = JsonSerializer.SerializeToElement(arg);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"JS-interop argument of type {arg.GetType().Name} for '{identifier}' is not " +
                    $"JSON-serializable; pass a cancellation token to the dedicated InvokeAsync overload " +
                    $"instead of as a JSON argument. {ex.Message}",
                    ex);
            }
        }

        _calls.Add((identifier, args));
        return default;
    }

    // The WebCrypto bridge calls `InvokeAsync<bool>` (install), `InvokeAsync<int>` (importPrivateKey),
    // and `InvokeAsync<string>` (sign — a base64 signature); this fake supplies a sensible value for
    // each and `default` otherwise.
    private static TValue ResultFor<TValue>()
    {
        if (typeof(TValue) == typeof(bool))
            return (TValue)(object)true;
        if (typeof(TValue) == typeof(int))
            return (TValue)(object)1;
        if (typeof(TValue) == typeof(string))
            return (TValue)(object)Convert.ToBase64String([0, 1, 2, 3]);
        return default!;
    }
}
