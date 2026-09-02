using Iris.Server.Persistance;

namespace Iris.Server.Tests.Persistance;

/// <summary>
/// Unit tests for the <see cref="FileBackedMediaStore"/> (Phase 20.4 (a), production persistence):
/// uploaded media survives a host restart. The metadata (media id → content-type + file name) is a single
/// JSON <see cref="FilePersistence"/>; the bytes are one sibling file per media id (written atomically).
/// Covers the <see cref="Iris.Server.Stores.IMediaStore"/> contract: store + read back, a restart (a second store over
/// the same path) still serves the item, a missing-media miss, and that the bytes file is a sibling of the
/// metadata file.
/// </summary>
public sealed class FileBackedMediaStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("iris-media-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the temp dir is OS-managed.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    private static readonly Iri Base = new("https://a.test");
    private static readonly byte[] Pixels = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]; // a PNG-ish blob

    private FileBackedMediaStore NewStore()
        => new(Path.Combine(_dir, "media.json"));

    [Fact]
    public async Task Put_ThenTryGet_ReturnsBytesContentTypeAndFileName()
    {
        var sut = NewStore();

        var iri = await sut.PutAsync(Pixels, "image/png", "cat.png", Base);

        Assert.True(await sut.TryGetAsync(iri, out var content, out var contentType, out var fileName));
        Assert.Equal(Pixels, content);
        Assert.Equal("image/png", contentType);
        Assert.Equal("cat.png", fileName);
    }

    [Fact]
    public async Task Put_ReturnsSameOriginMediaIri()
    {
        var sut = NewStore();

        var iri = await sut.PutAsync(Pixels, "image/png", "cat.png", Base);

        Assert.StartsWith("https://a.test/ap/v1/media/", iri.Value);
        Assert.Equal(32, iri.Value["https://a.test/ap/v1/media/".Length..].Length);
    }

    [Fact]
    public async Task Restart_StillServesTheItem()
    {
        // The whole point of file-backed media: a host restart (a second store over the same path) still
        // serves an item put before the restart.
        var iri = default(Iri);
        using (var first = NewStore())
        {
            iri = await first.PutAsync(Pixels, "image/png", "cat.png", Base);
        }

        using var second = NewStore();
        Assert.True(await second.TryGetAsync(iri, out var content, out var contentType, out var fileName));
        Assert.Equal(Pixels, content);
        Assert.Equal("image/png", contentType);
        Assert.Equal("cat.png", fileName);
    }

    [Fact]
    public async Task TryGet_UnknownMedia_ReturnsFalse()
    {
        var sut = NewStore();

        var missing = new Iri("https://a.test/ap/v1/media/deadbeefdeadbeefdeadbeefdeadbeef");

        Assert.False(await sut.TryGetAsync(missing, out var content, out _, out _));
        Assert.Null(content);
    }

    [Fact]
    public async Task Put_StoresBytesAsASiblingOfTheMetadataFile()
    {
        // The bytes are one sibling file per media id (named by the id), next to the metadata JSON file.
        var sut = NewStore();
        var iri = await sut.PutAsync(Pixels, "image/png", "cat.png", Base);
        var id = iri.Value["https://a.test/ap/v1/media/".Length..];

        var metadataPath = Path.Combine(_dir, "media.json");
        var bytesPath = Path.Combine(_dir, id);

        Assert.True(File.Exists(metadataPath));
        Assert.True(File.Exists(bytesPath));
        Assert.Equal(Pixels, File.ReadAllBytes(bytesPath));
    }
}
