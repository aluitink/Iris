using Iris.Core.Identity;
using Iris.Core.Rendering;

namespace Iris.Core.Tests.Rendering;

/// <summary>
/// Unit tests for <see cref="MentionLinkify"/> — specifically the handling of hyphenated local
/// handles (S45), which the over-narrow token regex previously truncated at the hyphen.
/// </summary>
public class MentionLinkifyTests
{
    private static readonly Iri HyphenatedActor = new("https://a.luit.ink/ap/v1/u/ii-a2");
    private static readonly Iri PlainActor = new("https://a.luit.ink/ap/v1/u/ii");
    private static readonly Iri FederatedActor = new("https://b.luit.ink/ap/v1/u/user");

    [Fact]
    public void Linkify_HyphenatedMention_ProducesFullHandleLink()
    {
        var html = "hello @ii-a2 world";
        var result = MentionLinkify.Linkify(html, [new MentionLinkify.Mention("@ii-a2", HyphenatedActor)], null);

        Assert.Contains("href=\"https://a.luit.ink/ap/v1/u/ii-a2\"", result);
        Assert.Contains(">@ii-a2</a>", result);
        Assert.DoesNotContain("-a2 world</a>", result);
        // The residual "-a2" must not appear outside the anchor.
        var afterAnchor = result[(result.IndexOf("</a>") + 3)..];
        Assert.DoesNotContain("-a2", afterAnchor);
    }

    [Fact]
    public void LinkifyPlain_HyphenatedMention_MatchesDeclaredTag()
    {
        var plain = "hello @ii-a2 world";
        var result = MentionLinkify.LinkifyPlain(plain, "https://a.luit.ink", [HyphenatedActor], null);

        Assert.Contains("href=\"https://a.luit.ink/ap/v1/u/ii-a2\"", result);
        Assert.Contains("@ii-a2</a>", result);
    }

    [Fact]
    public void Linkify_HyphenatedMention_AtStartOfText()
    {
        var html = "@ii-a2 hello";
        var result = MentionLinkify.Linkify(html, [new MentionLinkify.Mention("@ii-a2", HyphenatedActor)], null);

        Assert.Contains("<a class=\"mention\" href=\"https://a.luit.ink/ap/v1/u/ii-a2\">@ii-a2</a>", result);
    }

    [Fact]
    public void Linkify_PlainHandle_StillWorks()
    {
        var html = "hello @ii world";
        var result = MentionLinkify.Linkify(html, [new MentionLinkify.Mention("@ii", PlainActor)], null);

        Assert.Contains("href=\"https://a.luit.ink/ap/v1/u/ii\"", result);
        Assert.Contains(">@ii</a>", result);
    }

    [Fact]
    public void Linkify_FederatedMention_StillWorks()
    {
        var html = "hello @user@b.luit.ink world";
        var result = MentionLinkify.Linkify(html, [new MentionLinkify.Mention("@user@b.luit.ink", FederatedActor)], null);

        Assert.Contains("href=\"https://b.luit.ink/ap/v1/u/user\"", result);
        Assert.Contains("@user@b.luit.ink</a>", result);
    }

    [Fact]
    public void Linkify_MultipleHyphenatedMentions_EachLinked()
    {
        var a = new Iri("https://a.luit.ink/ap/v1/u/ii-a1");
        var b = new Iri("https://a.luit.ink/ap/v1/u/ii-a2");
        var html = "hello @ii-a1 and @ii-a2 world";
        var result = MentionLinkify.Linkify(html,
            [new MentionLinkify.Mention("@ii-a1", a), new MentionLinkify.Mention("@ii-a2", b)], null);

        Assert.Contains("href=\"https://a.luit.ink/ap/v1/u/ii-a1\"", result);
        Assert.Contains("href=\"https://a.luit.ink/ap/v1/u/ii-a2\"", result);
        Assert.Contains(">@ii-a1</a>", result);
        Assert.Contains(">@ii-a2</a>", result);
    }

    [Fact]
    public void Linkify_HyphenatedHandleFollowedByHyphenThenWord_LinksFullRun()
    {
        // A handle that itself contains multiple hyphens: the full run is one token.
        var actor = new Iri("https://a.luit.ink/ap/v1/u/ii-a2-x");
        var html = "hello @ii-a2-x world";
        var result = MentionLinkify.Linkify(html, [new MentionLinkify.Mention("@ii-a2-x", actor)], null);

        Assert.Contains("href=\"https://a.luit.ink/ap/v1/u/ii-a2-x\"", result);
        Assert.Contains(">@ii-a2-x</a>", result);
    }

    [Fact]
    public void Linkify_PlainHandleIsNotPrefixOfLongerHyphenatedToken()
    {
        // A declared '@ii' must not be linked as the prefix of a longer '@ii-a2' run in the body —
        // the trailing boundary excludes a following hyphen so the shorter token is left as plain text.
        var html = "hello @ii-a2 world";
        var result = MentionLinkify.Linkify(html, [new MentionLinkify.Mention("@ii", PlainActor)], null);

        Assert.DoesNotContain("<a class=\"mention\"", result);
        Assert.Contains("@ii-a2", result);
    }

    [Fact]
    public void LinkifyPlain_ShorterDeclaredHandleDoesNotMatchLongerBodyToken()
    {
        // Display-side: only '@ii-a2' is declared, but the body contains the shorter '@ii'. The token
        // handle 'ii' must not match the declared 'ii-a2', so it stays plain text (no dead link).
        var plain = "hello @ii world";
        var result = MentionLinkify.LinkifyPlain(plain, "https://a.luit.ink", [HyphenatedActor], null);

        Assert.DoesNotContain("<a class=\"mention\"", result);
        Assert.Contains("@ii", result);
    }

    [Fact]
    public void Linkify_HashtagStillWorks_AlongsideHyphenatedMention()
    {
        var html = "hello @ii-a2 #qatag world";
        var result = MentionLinkify.Linkify(html,
            [new MentionLinkify.Mention("@ii-a2", HyphenatedActor)],
            [new MentionLinkify.Hashtag("#qatag", "https://a.luit.ink/search?q=%23qatag")]);

        Assert.Contains("href=\"https://a.luit.ink/ap/v1/u/ii-a2\"", result);
        Assert.Contains("href=\"https://a.luit.ink/search?q=%23qatag\"", result);
        Assert.Contains(">@ii-a2</a>", result);
        Assert.Contains("#qatag</a>", result);
    }

    [Fact]
    public void Linkify_DefaultIriMention_DoesNotThrowAndLeavesTokenPlain()
    {
        // S76: a mention whose actor IRI is the default value (no underlying Uri) must not crash
        // Linkify. Before the Iri null-safety fix, mention.Iri.Value threw NullReferenceException here.
        var html = "hello @ii world";
        var result = MentionLinkify.Linkify(html, [new MentionLinkify.Mention("@ii", default)], null);

        // The empty-IRI mention is skipped; the token stays plain text.
        Assert.DoesNotContain("<a class=\"mention\"", result);
        Assert.Contains("@ii", result);
    }

    [Fact]
    public void LinkifyPlain_DefaultIriMention_DoesNotThrow()
    {
        // S76: a declared mention IRI that is the default value must not crash LinkifyPlain (which
        // reads mentionIris[i].Value through HandleOfIri).
        var plain = "hello @ii world";
        var result = MentionLinkify.LinkifyPlain(plain, "https://a.luit.ink", [default], null);

        Assert.DoesNotContain("<a class=\"mention\"", result);
        Assert.Contains("@ii", result);
    }
}
