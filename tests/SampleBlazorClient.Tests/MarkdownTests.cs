using Iris.Samples.SampleBlazorClient;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 20.4 (c) unit tests: the dependency-free <see cref="Markdown"/> renderer. These pin the
/// rendered HTML for the Markdown subset the object view supports (headings, code, links, lists,
/// emphasis) and — critically — the security guarantees (HTML is escaped, unsafe link schemes are
/// dropped). The renderer is pure (string → string), so unit tests are appropriate here.
/// </summary>
public sealed class MarkdownTests
{
    [Fact]
    public void ToHtml_NullOrBlank_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Markdown.ToHtml(null));
        Assert.Equal(string.Empty, Markdown.ToHtml(""));
        Assert.Equal(string.Empty, Markdown.ToHtml("   "));
    }

    [Fact]
    public void ToHtml_PlainText_WrapsInParagraph()
    {
        Assert.Equal("<p>hello world</p>", Markdown.ToHtml("hello world"));
    }

    [Fact]
    public void ToHtml_Heading_RendersH1ToH6()
    {
        Assert.Equal("<h1>Title</h1>", Markdown.ToHtml("# Title"));
        Assert.Equal("<h2>Sub</h2>", Markdown.ToHtml("## Sub"));
        Assert.Equal("<h3>Deep</h3>", Markdown.ToHtml("### Deep"));
        Assert.Equal("<h6>Six</h6>", Markdown.ToHtml("###### Six"));
    }

    [Fact]
    public void ToHtml_Heading_WithInlineMarkdown_RendersInline()
    {
        Assert.Equal(
            "<h1>See <a href=\"https://example.com\" rel=\"nofollow noopener\" target=\"_blank\">the docs</a></h1>",
            Markdown.ToHtml("# See [the docs](https://example.com)"));
    }

    [Fact]
    public void ToHtml_InlineCode_WrapsInCode()
    {
        Assert.Equal("<p>run <code>dotnet build</code> now</p>", Markdown.ToHtml("run `dotnet build` now"));
    }

    [Fact]
    public void ToHtml_InlineCode_ContentIsNotFurtherTransformed()
    {
        // A `#` or link inside a code span is literal, not a heading/link.
        Assert.Equal("<p><code># not a heading</code></p>", Markdown.ToHtml("`# not a heading`"));
    }

    [Fact]
    public void ToHtml_FencedCodeBlock_RendersPreCode()
    {
        var html = Markdown.ToHtml("```\nvar x = 1;\n```");
        Assert.Contains("<pre><code>", html);
        Assert.Contains("var x = 1;", html);
        Assert.Contains("</code></pre>", html);
    }

    [Fact]
    public void ToHtml_FencedCodeBlock_WithLanguage_AddsClass()
    {
        var html = Markdown.ToHtml("```csharp\nvar x = 1;\n```");
        Assert.Contains("<code class=\"language-csharp\">", html);
    }

    [Fact]
    public void ToHtml_FencedCodeBlock_ContentIsNotTransformed()
    {
        // A `#` inside a fence is literal (not a heading).
        var html = Markdown.ToHtml("```\n# not a heading\n- not a list\n```");
        Assert.Contains("# not a heading", html);
        Assert.DoesNotContain("<h1>", html);
        Assert.DoesNotContain("<ul>", html);
    }

    [Fact]
    public void ToHtml_Link_RendersAnchorWithRelAndTarget()
    {
        var html = Markdown.ToHtml("see [the site](https://example.com/x)");
        Assert.Equal(
            "<p>see <a href=\"https://example.com/x\" rel=\"nofollow noopener\" target=\"_blank\">the site</a></p>",
            html);
    }

    [Fact]
    public void ToHtml_Link_WithMailto_RendersAnchor()
    {
        var html = Markdown.ToHtml("[mail me](mailto:a@b.c)");
        Assert.Contains("<a href=\"mailto:a@b.c\"", html);
    }

    [Fact]
    public void ToHtml_Link_WithJavaScriptScheme_DropsTheLink()
    {
        // A javascript: (or other non-allowed) scheme must not become a live link.
        var html = Markdown.ToHtml("[click](javascript:alert(1))");
        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("click", html); // the link text remains as plain text
    }

    [Fact]
    public void ToHtml_Link_WithRelativeOrBareScheme_DropsTheLink()
    {
        Assert.DoesNotContain("<a ", Markdown.ToHtml("[x](/relative/path)"));
        Assert.DoesNotContain("<a ", Markdown.ToHtml("[x](ftp://example.com)"));
    }

    [Fact]
    public void ToHtml_UnorderedList_RendersUl()
    {
        var html = Markdown.ToHtml("- one\n- two\n- three");
        Assert.Contains("<ul>", html);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains("<li>two</li>", html);
        Assert.Contains("<li>three</li>", html);
        Assert.Contains("</ul>", html);
    }

    [Fact]
    public void ToHtml_UnorderedList_WithAsterisks_RendersUl()
    {
        var html = Markdown.ToHtml("* one\n* two");
        Assert.Contains("<ul>", html);
        Assert.Contains("<li>one</li>", html);
    }

    [Fact]
    public void ToHtml_OrderedList_RendersOl()
    {
        var html = Markdown.ToHtml("1. first\n2. second");
        Assert.Contains("<ol>", html);
        Assert.Contains("<li>first</li>", html);
        Assert.Contains("<li>second</li>", html);
        Assert.Contains("</ol>", html);
    }

    [Fact]
    public void ToHtml_ListItem_WithInlineMarkdown_RendersInline()
    {
        var html = Markdown.ToHtml("- a [link](https://example.com) item");
        Assert.Contains("<li>a <a href=\"https://example.com\" rel=\"nofollow noopener\" target=\"_blank\">link</a> item</li>", html);
    }

    [Fact]
    public void ToHtml_Bold_RendersStrong()
    {
        Assert.Equal("<p>some <strong>bold</strong> text</p>", Markdown.ToHtml("some **bold** text"));
    }

    [Fact]
    public void ToHtml_Italic_RendersEm()
    {
        Assert.Equal("<p>some <em>italic</em> text</p>", Markdown.ToHtml("some *italic* text"));
    }

    [Fact]
    public void ToHtml_BoldBeforeItalic_DoubleStarIsBold()
    {
        // ** must be treated as bold, not as two italics.
        Assert.Equal("<p><strong>bold</strong></p>", Markdown.ToHtml("**bold**"));
    }

    [Fact]
    public void ToHtml_RawHtml_IsEscapedNotEmitted()
    {
        // A <script> in the content must be escaped (inert), never emitted as a live tag.
        var html = Markdown.ToHtml("<script>alert(1)</script>");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;/script&gt;", html);
    }

    [Fact]
    public void ToHtml_RawHtmlAttributesAreEscaped()
    {
        var html = Markdown.ToHtml("<img src=x onerror=alert(1)>");
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img", html);
    }

    [Fact]
    public void ToHtml_HardLineBreak_RendersBr()
    {
        var html = Markdown.ToHtml("line one\nline two");
        Assert.Contains("line one<br/>line two", html);
    }

    [Fact]
    public void ToHtml_MultipleBlocks_AreAllRendered()
    {
        var html = Markdown.ToHtml("# Title\n\nA paragraph.\n\n- item one\n- item two");
        Assert.Contains("<h1>Title</h1>", html);
        Assert.Contains("<p>A paragraph.</p>", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("<li>item one</li>", html);
    }

    [Fact]
    public void ToHtml_Ampersand_IsEscaped()
    {
        Assert.Equal("<p>Tom &amp; Jerry</p>", Markdown.ToHtml("Tom & Jerry"));
    }
}
