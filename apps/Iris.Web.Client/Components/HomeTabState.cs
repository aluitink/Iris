namespace Iris.Web.Client.Components;

/// <summary>
/// Holds the active home-feed tab (unified-home-feed Phase 4). The <c>FeedBar</c> (in
/// <c>MainLayout</c>) writes the active tab; <c>HomeTimeline</c> reads it to set the
/// feed's <c>?source=</c> filter. A scoped service so both components (layout + page)
/// share the same instance within a single circuit.
/// </summary>
public sealed class HomeTabState
{
    private string _tab = "posts";
    private event Action? TabChanged;

    /// <summary>
    /// The active tab: <c>"posts"</c>, <c>"local"</c>, or <c>"communities"</c>. Defaults to <c>"posts"</c>.
    /// </summary>
    public string Tab
    {
        get => _tab;
        set
        {
            if (!string.Equals(_tab, value, StringComparison.Ordinal))
            {
                _tab = value;
                TabChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// Raised when the active tab changes. Subscribers should call <c>StateHasChanged</c>.
    /// </summary>
    public event Action TabChangedExternal
    {
        add => TabChanged += value;
        remove => TabChanged -= value;
    }
}
