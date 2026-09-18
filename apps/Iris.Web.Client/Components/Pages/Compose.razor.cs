using Iris.Web.Client.Components.Pages;

namespace Iris.Web.Client.Components.Pages;

/// <summary>
/// Disposes the compose page's transient resources (the autocomplete debounce timer and the in-flight
/// search cancellation) when the component is removed from the render tree (54.14).
/// </summary>
public partial class Compose : IDisposable
{
    /// <inheritdoc/>
    public void Dispose()
    {
        _autocompleteDebounce?.Dispose();
        _autocompleteSearchCts?.Cancel();
        _autocompleteSearchCts?.Dispose();
    }
}
