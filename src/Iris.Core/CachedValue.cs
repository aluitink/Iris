namespace Iris.Core;

/// <summary>
/// The result of reading through an <see cref="ICache{TValue}"/>: whether a usable entry was
/// found and, if so, the value and its freshness state.
/// </summary>
/// <typeparam name="TValue">The type of the cached value.</typeparam>
/// <remarks>
/// <see cref="Hit"/> is <see langword="true"/> for both fresh and stale entries (stale-while-
/// revalidate serves stale values immediately). <see cref="IsFresh"/> distinguishes the two: a
/// stale hit should trigger a background revalidation, while a fresh hit should not.
/// </remarks>
public readonly record struct CachedValue<TValue>(bool Hit, TValue? Value, CacheState State)
{
    /// <summary>
    /// A miss: no usable (fresh or stale) entry was found.
    /// </summary>
    public static CachedValue<TValue> Miss { get; } = new(Hit: false, Value: default, State: CacheState.Expired);

    /// <summary>
    /// Returns <see langword="true"/> when a usable (fresh or stale) entry was found.
    /// </summary>
    public bool IsFresh => Hit && State == CacheState.Fresh;

    /// <summary>
    /// Returns <see langword="true"/> when a usable entry was found but it is stale (should be
    /// revalidated in the background).
    /// </summary>
    public bool IsStale => Hit && State == CacheState.Stale;
}
