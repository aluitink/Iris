using Iris.Core.Rendering;

namespace Iris.Core.Tests.Rendering;

/// <summary>
/// Unit tests for <see cref="HtmlSanitizer"/> — the dependency-free sanitizer that makes an
/// untrusted ActivityPub actor summary safe to render as Blazor markup. The security contract under
/// test: only an allow-listed subset of tags/attributes survives, and everything dangerous (scripts,
/// event handlers, dangerous URL schemes, style attributes) is stripped.
/// </summary>
public class HtmlSanitizerTests
{
    [Fact]
    public void Sanitize_NullOrBlank_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, HtmlSanitizer.Sanitize(null));
        Assert.Equal(string.Empty, HtmlSanitizer.Sanitize(""));
        Assert.Equal(string.Empty, HtmlSanitizer.Sanitize("   "));
    }

    [Fact]
    public void Sanitize_PlainText_ReturnsUnchanged()
    {
        Assert.Equal("just some text", HtmlSanitizer.Sanitize("just some text"));
    }

    [Fact]
    public void Sanitize_AllowedFormattingTags_AreKept()
    {
        var html = "<p>Hello <b>bold</b> and <i>italic</i> and <a href=\"https://example.com\">a link</a></p>";
        var result = HtmlSanitizer.Sanitize(html);

        Assert.Contains("<p>", result);
        Assert.Contains("<b>bold</b>", result);
        Assert.Contains("<i>italic</i>", result);
        Assert.Contains("<a", result);
    }

    [Fact]
    public void Sanitize_ScriptTag_IsRemovedWithItsContent()
    {
        var result = HtmlSanitizer.Sanitize("before<script>alert(1)</script>after");

        Assert.DoesNotContain("script", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", result);
        Assert.Contains("before", result);
        Assert.Contains("after", result);
    }

    [Fact]
    public void Sanitize_EventHandlerAttributes_AreStripped()
    {
        var result = HtmlSanitizer.Sanitize("<img src=\"x\" onerror=\"alert(1)\" />");

        // img is not an allowed tag AND onerror is not an allowed attribute — both stripped.
        Assert.DoesNotContain("onerror", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_EventHandlerOnAllowedTag_IsStripped()
    {
        // A click handler on an allowed element (a) must be dropped while the element survives.
        var result = HtmlSanitizer.Sanitize("<a href=\"https://example.com\" onclick=\"alert(1)\">x</a>");

        Assert.DoesNotContain("onclick", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<a", result);
    }

    [Fact]
    public void Sanitize_JavaScriptHref_IsStripped()
    {
        var result = HtmlSanitizer.Sanitize("<a href=\"javascript:alert(1)\">x</a>");

        Assert.DoesNotContain("javascript:", result, StringComparison.OrdinalIgnoreCase);
        // The link survives, but with no href (the dangerous scheme is dropped).
        Assert.Contains("<a", result);
    }

    [Fact]
    public void Sanitize_HttpsHref_IsKept()
    {
        var result = HtmlSanitizer.Sanitize("<a href=\"https://example.com\">x</a>");

        Assert.Contains("https://example.com", result);
    }

    [Fact]
    public void Sanitize_MailtoHref_IsKept()
    {
        var result = HtmlSanitizer.Sanitize("<a href=\"mailto:a@b.com\">x</a>");

        Assert.Contains("mailto:a@b.com", result);
    }

    [Fact]
    public void Sanitize_DataHref_IsStripped()
    {
        var result = HtmlSanitizer.Sanitize("<a href=\"data:text/html,<script>alert(1)</script>\">x</a>");

        Assert.DoesNotContain("data:", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_StyleAttribute_IsStripped()
    {
        var result = HtmlSanitizer.Sanitize("<p style=\"color: red\">x</p>");

        Assert.DoesNotContain("style=", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<p>", result);
    }

    [Fact]
    public void Sanitize_Iframe_IsRemoved()
    {
        var result = HtmlSanitizer.Sanitize("a<iframe src=\"https://evil.com\"></iframe>b");

        Assert.DoesNotContain("iframe", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.com", result);
    }

    [Fact]
    public void Sanitize_DisallowedNonCodeTag_DropsTagKeepsText()
    {
        // A disallowed but non-code element (e.g. a <form>-like tag not in the code set) — its text is kept.
        // Use a tag that is neither allowed nor content-is-code.
        var result = HtmlSanitizer.Sanitize("<customtag>inner</customtag>");

        Assert.DoesNotContain("<customtag", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inner", result);
    }

    [Fact]
    public void Sanitize_ListsAreKept()
    {
        var result = HtmlSanitizer.Sanitize("<ul><li>one</li><li>two</li></ul>");

        Assert.Contains("<ul>", result);
        Assert.Contains("<li>one</li>", result);
    }

    [Fact]
    public void Sanitize_HeadingsAreKept()
    {
        var result = HtmlSanitizer.Sanitize("<h2>Title</h2>");

        Assert.Contains("<h2>Title</h2>", result);
    }

    [Fact]
    public void Sanitize_Comments_AreRemoved()
    {
        var result = HtmlSanitizer.Sanitize("a<!-- secret -->b");

        Assert.DoesNotContain("secret", result);
        Assert.Contains("a", result);
        Assert.Contains("b", result);
    }

    [Fact]
    public void Sanitize_BrIsSelfClosingInOutput()
    {
        var result = HtmlSanitizer.Sanitize("line1<br>line2");

        Assert.Contains("<br />", result);
    }

    [Fact]
    public void Sanitize_TitleAttribute_IsKept()
    {
        var result = HtmlSanitizer.Sanitize("<a href=\"https://example.com\" title=\"Example\">x</a>");

        Assert.Contains("title=\"Example\"", result);
    }

    [Fact]
    public void Sanitize_RelTargetOnLink_AreNormalized()
    {
        var result = HtmlSanitizer.Sanitize("<a href=\"https://example.com\" rel=\"anything\" target=\"_self\">x</a>");

        Assert.Contains("rel=\"nofollow noopener\"", result);
        Assert.Contains("target=\"_blank\"", result);
    }

    [Fact]
    public void Sanitize_WhitespaceInHrefScheme_DoesNotBypass()
    {
        // Browsers strip tabs/newlines/space from the scheme; a "java\tscript:" must still be rejected.
        var result = HtmlSanitizer.Sanitize("<a href=\"java\tscript:alert(1)\">x</a>");

        Assert.DoesNotContain("javascript", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", result);
    }

    [Fact]
    public void Sanitize_AttributeValueQuotes_AreEscaped()
    {
        // A double-quote inside an attribute value must be escaped so it cannot terminate the attribute.
        var result = HtmlSanitizer.Sanitize("<a href=\"https://example.com\" title='he said \"hi\"'>x</a>");

        Assert.Contains("&quot;", result);
    }
}
