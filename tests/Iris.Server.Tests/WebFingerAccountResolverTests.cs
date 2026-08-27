using Iris.Client;
using Iris.Core;

namespace Iris.Server.Tests;

/// <summary>
/// Unit tests for <see cref="WebFingerAccountResolver"/>: it resolves a remote account to its actor IRI
/// through the Phase 3 <see cref="WebFingerCache"/> so an account is resolved once and reused across
/// lookups, an absent result is not cached (retried on the next lookup), and <c>forceRefresh</c>
/// bypasses a fresh entry.
/// </summary>
/// <remarks>
/// The outbound WebFinger transport is a fake <see cref="WebFingerClient"/> (a counting subclass) so
/// cache hit/miss/refresh behavior is observable without a network.
/// </remarks>
public class WebFingerAccountResolverTests
{
    private const string BHost = "b.domain.local";

    [Fact]
    public async Task Resolve_Miss_ResolvesAndCaches()
    {
        var webFinger = new StubWebFingerResolver(new Iri($"https://{BHost}/ap/v1/u/bob"));
        var cache = new WebFingerCache();
        var sut = new WebFingerAccountResolver(webFinger, cache);

        var actorIri = await sut.ResolveAsync($"bob@{BHost}");

        Assert.Equal(new Iri($"https://{BHost}/ap/v1/u/bob"), actorIri);
        Assert.Equal(1, webFinger.ResolveCalls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task Resolve_FreshHit_IsCached()
    {
        var webFinger = new StubWebFingerResolver(new Iri($"https://{BHost}/ap/v1/u/alice"));
        var cache = new WebFingerCache();
        var sut = new WebFingerAccountResolver(webFinger, cache);

        var first = await sut.ResolveAsync($"alice@{BHost}");
        var second = await sut.ResolveAsync($"alice@{BHost}");

        // Same actor IRI served from the cache on the second call; the client is not hit again.
        Assert.Equal(first, second);
        Assert.Equal(new Iri($"https://{BHost}/ap/v1/u/alice"), second);
        Assert.Equal(1, webFinger.ResolveCalls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task Resolve_Absent_IsNotCached()
    {
        var webFinger = new StubWebFingerResolver(null); // the account does not resolve (404 / no self link)
        var cache = new WebFingerCache();
        var sut = new WebFingerAccountResolver(webFinger, cache);

        var first = await sut.ResolveAsync($"nobody@{BHost}");
        var second = await sut.ResolveAsync($"nobody@{BHost}");

        // Absent results are never cached, so the second call retries the resolution.
        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(2, webFinger.ResolveCalls);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Resolve_DistinctAccounts_ResolvesEach()
    {
        var webFinger = new StubWebFingerResolver(null)
        {
            Resolutions =
            {
                ["acct:carol@b.domain.local"] = new Iri($"https://{BHost}/ap/v1/u/carol"),
                ["acct:dave@b.domain.local"] = new Iri($"https://{BHost}/ap/v1/u/dave"),
            },
        };
        var cache = new WebFingerCache();
        var sut = new WebFingerAccountResolver(webFinger, cache);

        var carol = await sut.ResolveAsync($"carol@{BHost}");
        var dave = await sut.ResolveAsync($"dave@{BHost}");

        Assert.Equal(new Iri($"https://{BHost}/ap/v1/u/carol"), carol);
        Assert.Equal(new Iri($"https://{BHost}/ap/v1/u/dave"), dave);
        Assert.Equal(2, webFinger.ResolveCalls);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task Resolve_ForceRefresh_BypassesCacheAndWritesBack()
    {
        var webFinger = new StubWebFingerResolver(new Iri($"https://{BHost}/ap/v1/u/eve"));
        var cache = new WebFingerCache();
        var sut = new WebFingerAccountResolver(webFinger, cache);
        var account = $"eve@{BHost}";

        _ = await sut.ResolveAsync(account);
        Assert.Equal(1, webFinger.ResolveCalls);

        // forceRefresh=true → the client is consulted even though a fresh entry exists.
        var refreshed = await sut.ResolveAsync(account, forceRefresh: true);

        Assert.Equal(new Iri($"https://{BHost}/ap/v1/u/eve"), refreshed);
        Assert.Equal(2, webFinger.ResolveCalls);
        Assert.Equal(1, cache.Count);
    }

    // --- Helpers -----------------------------------------------------------------

    /// <summary>
    /// A counting <see cref="IWebFingerResolver"/> that returns a fixed actor IRI (or a per-account
    /// map) instead of hitting the network, and tracks <see cref="IWebFingerResolver.ResolveActorAsync"/>
    /// call count.
    /// </summary>
    private sealed class StubWebFingerResolver(Iri? actorIri) : IWebFingerResolver
    {
        private readonly Iri? _actorIri = actorIri;

        /// <summary>
        /// An optional per-account (normalized <c>acct:</c> subject) resolution map; when set and the
        /// subject is present, overrides the fixed actor IRI.
        /// </summary>
        public Dictionary<string, Iri> Resolutions { get; } = new();

        /// <summary>
        /// The number of times <see cref="ResolveActorAsync"/> has been invoked.
        /// </summary>
        public int ResolveCalls { get; private set; }

        /// <inheritdoc/>
        public Task<Iri?> ResolveActorAsync(string account, CancellationToken ct = default)
        {
            ResolveCalls++;
            var subject = WebFingerClient.NormalizeSubject(account);
            if (Resolutions.TryGetValue(subject, out var resolved))
            {
                return Task.FromResult<Iri?>(resolved);
            }

            return Task.FromResult(_actorIri);
        }
    }
}
