using Iris.Core.Identity;
using Iris.Server.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Identity;

/// <summary>
/// S50: a top-level cross-post to a remote (non-Iris, e.g. Lemmy) community is authored as a
/// <see cref="Page"/> (the Lemmy-compatible top-level content type, 138.11). The server mints the
/// embedded object's id under a per-type namespace (<see cref="IdMinter.NamespaceFor(IObject)"/>).
/// A <see cref="Page"/> is content — a top-level post — so it must be minted under the content
/// namespace (<c>notes/</c>), the one the object document endpoint and remote peers resolve. Before
/// the S50 fix, <see cref="Page"/> (a <see cref="Document"/> subclass) fell through to the
/// <c>Document => "documents"</c> arm, minting a <c>documents/</c> IRI that a remote instance's GET
/// of the post could not resolve (404).
/// </summary>
public class IdMinterPageNamespaceTests
{
    private static readonly Iri Actor = new("https://a.test/ap/v1/u/alice");
    private readonly IdMinter _minter = new();

    [Fact]
    public void Page_Is_Minted_Under_Notes_Not_Documents()
    {
        // A Lemmy cross-post's embedded object: a top-level Page (no inReplyTo).
        var page = new Page
        {
            AttributedTo =
            [
                new Link { Href = new Uri("https://a.test/ap/v1/u/alice") },
                new Link { Href = new Uri("https://b.test/ap/v1/c/lemmy") },
            ],
            To = [new Link { Href = new Uri("https://b.test/ap/v1/c/lemmy") }],
            Content = ["<p>cross-post</p>"],
        };

        var minted = _minter.Mint(Actor, page).Value;

        Assert.StartsWith("https://a.test/ap/v1/u/alice/notes/", minted);
        Assert.DoesNotContain("/documents/", minted);
    }

    [Theory]
    [InlineData(typeof(Page), "notes")]
    [InlineData(typeof(Note), "notes")]
    [InlineData(typeof(Article), "articles")]
    [InlineData(typeof(Group), "groups")]
    public void NamespaceFor_Matches_Expected_Segment(Type objectType, string expected)
    {
        IObject obj = objectType switch
        {
            _ => (IObject)Activator.CreateInstance(objectType)!,
        };
        Assert.Equal(expected, IdMinter.NamespaceFor(obj));
    }

    [Fact]
    public void Page_Is_Classified_Before_Its_Base_Document()
    {
        // Guard the ordering: a Page must NOT be classified as a Document (which would mint
        // "documents/"). This is the exact S50 regression.
        var page = (IObject)new Page { Content = ["<p>x</p>"] };
        Assert.True(page is Page);
        Assert.True(page is Document); // Page derives from Document
        Assert.Equal("notes", IdMinter.NamespaceFor(page));
    }
}
