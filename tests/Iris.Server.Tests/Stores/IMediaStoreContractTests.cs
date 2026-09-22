using Iris.Core;
using Iris.Server.Stores;

namespace Iris.Server.Tests.Stores;

/// <summary>
/// Shared <see cref="IMediaStore"/> contract behaviors (Phase 20.4 (a)): the put/read-back round-trip
/// (bytes, content-type, file name) and the same-origin media-IRI shape (<c>{base}/ap/v1/media/{id}</c>,
/// an unguessable 32-char id) that every media-store implementation must satisfy.
/// <para>
/// xunit does <em>not</em> run <c>[Fact]</c>/<c>[Theory]</c> methods inherited from a base class (only
/// traits and fixtures inherit), so the shared behaviors are ordinary methods invoked from each
/// implementation's test class via <see cref="RunSharedContractBehaviors"/> — each implementation runs the
/// same assertions against its own store (created by <see cref="CreateStore"/>). Each class keeps its
/// implementation-specific tests (persistence across a restart, on-disk layout, in-memory independence).
/// </para>
/// </summary>
public abstract class IMediaStoreContractTests
{
    private static readonly Iri Base = new("https://a.test");
    private static readonly byte[] Pixels = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]; // a PNG-ish blob

    /// <summary>
    /// The full media-IRI prefix an implementation builds on the shared base — the same-origin
    /// <c>https://a.test/ap/v1/media/</c> path (the browser loads the attachment from the same origin).
    /// Built as a literal (not <c>Base.Value + …</c>) because <see cref="Iri.Value"/> is the path
    /// component, not the scheme+authority.
    /// </summary>
    protected static readonly string MediaPrefix = "https://a.test/ap/v1/media/";

    /// <summary>Creates a fresh media store for the shared contract behaviors.</summary>
    protected abstract IMediaStore CreateStore();

    /// <summary>
    /// Runs the shared <see cref="IMediaStore"/> contract behaviors against this implementation's store.
    /// Each derived test class invokes this from its own <c>[Fact]</c> (e.g.
    /// <c>Contract_PutReadBackAndSameOriginIri()</c> → <c>RunSharedContractBehaviors()</c>).
    /// </summary>
    protected Task RunSharedContractBehaviors()
    {
        var sut = CreateStore();
        var iri = sut.PutAsync(Pixels, "image/png", "cat.png", Base).GetAwaiter().GetResult();

        Assert.True(sut.TryGetAsync(iri, out var content, out var contentType, out var fileName).GetAwaiter().GetResult());
        Assert.Equal(Pixels, content);
        Assert.Equal("image/png", contentType);
        Assert.Equal("cat.png", fileName);

        // The media IRI is the instance's base + /ap/v1/media/{id} (same-origin — the browser loads it
        // from the same origin, never a cross-origin media host).
        Assert.StartsWith(MediaPrefix, iri.Value);
        // The id is an unguessable 32-char Guid ("N").
        Assert.Equal(32, iri.Value[MediaPrefix.Length..].Length);

        return Task.CompletedTask;
    }
}
