using System.Diagnostics;

namespace Iris.Core.Caching;

/// <summary>
/// A single cached item: the value plus the timestamp at which it was written, the time it
/// is considered fresh (<see cref="CachePolicy.Ttl"/>) after, and the time it is considered
/// usable-but-stale (<see cref="CachePolicy.StaleFor"/>) after.
/// </summary>
/// <typeparam name="TValue">The type of the cached value.</typeparam>
/// <remarks>
/// <see cref="Ttl"/> and <see cref="StaleFor"/> are captured from the
/// <see cref="CachePolicy"/> at write time, so a change to the policy does not retroactively
/// affect entries already in the cache.
/// </remarks>
public readonly record struct CacheEntry<TValue>
{
    /// <summary>
    /// Initializes a new <see cref="CacheEntry{TValue}"/>.
    /// </summary>
    /// <param name="value">The cached value.</param>
    /// <param name="createdAtUtc">The timestamp at which the entry was written.</param>
    /// <param name="ttl">How long the entry is fresh after <paramref name="createdAtUtc"/>.</param>
    /// <param name="staleFor">How long the entry is usable-but-stale after <paramref name="createdAtUtc"/>.</param>
    /// <remarks>
    /// A null value is rejected when <typeparamref name="TValue"/> is a reference type (the common
    /// case); value types always pass through.
    /// </remarks>
    public CacheEntry(TValue value, DateTime createdAtUtc, TimeSpan ttl, TimeSpan staleFor)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        Value = value;
        CreatedAtUtc = createdAtUtc;
        Ttl = ttl;
        StaleFor = staleFor;
    }

    /// <summary>
    /// Gets the cached value.
    /// </summary>
    public TValue Value { get; }

    /// <summary>
    /// Gets the timestamp at which the entry was written.
    /// </summary>
    public DateTime CreatedAtUtc { get; }

    /// <summary>
    /// Gets how long the entry is fresh after it was written.
    /// </summary>
    public TimeSpan Ttl { get; }

    /// <summary>
    /// Gets how long the entry is usable-but-stale after it was written.
    /// </summary>
    public TimeSpan StaleFor { get; }

    /// <summary>
    /// The instant at which the entry stopped being fresh.
    /// </summary>
    public DateTime FreshUntilUtc => CreatedAtUtc + Ttl;

    /// <summary>
    /// The instant at which the entry is no longer usable (must be refreshed).
    /// </summary>
    public DateTime UsableUntilUtc => CreatedAtUtc + StaleFor;

    /// <summary>
    /// Determines the state of the entry relative to <paramref name="nowUtc"/>.
    /// </summary>
    /// <param name="nowUtc">The reference "now" (injected for determinism in tests).</param>
    /// <returns>
    /// <see cref="CacheState.Fresh"/> when <c>nowUtc &lt;= FreshUntilUtc</c>;
    /// <see cref="CacheState.Stale"/> when <c>FreshUntilUtc &lt; nowUtc &lt;= UsableUntilUtc</c>;
    /// <see cref="CacheState.Expired"/> when <c>nowUtc &gt; UsableUntilUtc</c>.
    /// </returns>
    public CacheState GetState(DateTime nowUtc)
    {
        if (nowUtc <= FreshUntilUtc)
        {
            return CacheState.Fresh;
        }

        if (nowUtc <= UsableUntilUtc)
        {
            return CacheState.Stale;
        }

        return CacheState.Expired;
    }

    /// <summary>
    /// Returns a copy of this entry with a new value and timestamp (same policy).
    /// </summary>
    /// <param name="newValue">The new value.</param>
    /// <param name="nowUtc">The new "created at" timestamp.</param>
    public CacheEntry<TValue> With(TValue newValue, DateTime nowUtc)
    {
        if (newValue is null)
        {
            throw new ArgumentNullException(nameof(newValue));
        }

        return new CacheEntry<TValue>(newValue, nowUtc, Ttl, StaleFor);
    }
}
