using System.IO;
using Iris.Web;

namespace Iris.Web.Tests;

/// <summary>
/// Phase 137.x — version-stamped WASM shell. The shell's FIXED-URL tags (the blazor.webassembly.js
/// loader, app.css, and the app's js/*.js) go stale after a redeploy; the server rewrites them in
/// memory with a per-build ?v= stamp so the browser fetches the fresh copy. These tests cover the two
/// pure seams: <c>WebAppFactory.StampShellVersion</c> (the in-memory HTML rewrite) and
/// <c>WebAppFactory.ResolveShellVersion</c> (the build-identity derivation from the published
/// _framework/ payload).
/// </summary>
public class ShellVersionStampingTests
{
    // A representative slice of the real shell: the two app css/js tags, the favicon (which must be
    // left alone), and the _framework/ loader.
    private const string Shell = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <link rel="icon" type="image/svg+xml" href="favicon.svg" />
            <link rel="stylesheet" href="css/app.css" />
        </head>
        <body>
            <script src="js/WebCrypto.js"></script>
            <script src="js/error-ui.js"></script>
            <script src="_framework/blazor.webassembly.js"></script>
        </body>
        </html>
        """;

    [Fact]
    public void StampShellVersion_StampsAllFixedUrlTags_LeavesFaviconAlone()
    {
        var result = WebAppFactory.StampShellVersion(Shell, "1.0.0+e37bba3b");

        Assert.Contains("href=\"css/app.css?v=1.0.0+e37bba3b\"", result);
        Assert.Contains("src=\"js/WebCrypto.js?v=1.0.0+e37bba3b\"", result);
        Assert.Contains("src=\"js/error-ui.js?v=1.0.0+e37bba3b\"", result);
        Assert.Contains("src=\"_framework/blazor.webassembly.js?v=1.0.0+e37bba3b\"", result);

        // The favicon has no .css/.js extension and must NOT be stamped.
        Assert.Contains("href=\"favicon.svg\"", result);
        Assert.DoesNotContain("favicon.svg?v=", result);
    }

    [Fact]
    public void StampShellVersion_IsIdempotent_ExistingStampIsReplacedNotAccumulated()
    {
        var once = WebAppFactory.StampShellVersion(Shell, "1.0.0+aaaa");
        var twice = WebAppFactory.StampShellVersion(once, "1.0.0+bbbb");

        Assert.Contains("href=\"css/app.css?v=1.0.0+bbbb\"", twice);
        // No doubled ?v=...?v= suffix.
        Assert.DoesNotContain("?v=1.0.0+aaaa?v=", twice);
        Assert.DoesNotContain("?v=" + "?v=", twice);
    }

    [Fact]
    public void StampShellVersion_NullVersion_ReturnsContentUnchanged()
    {
        Assert.Equal(Shell, WebAppFactory.StampShellVersion(Shell, null));
        Assert.Equal(Shell, WebAppFactory.StampShellVersion(Shell, ""));
    }

    [Fact]
    public void StampShellVersion_OnlyTouchesCssJsAndFrameworkSrc_DoesNotRewriteArbitraryAttrs()
    {
        var html = """
            <div data-src="not-a-real-tag.js">text</div>
            <a href="/abs/path.css">x</a>
            <link rel="stylesheet" href="css/app.css" />
            """;
        var result = WebAppFactory.StampShellVersion(html, "1.0.0+e37bba3b");

        // A non-<script>/non-<link> src-like attribute is not a tag the stamper targets, so it is
        // untouched. (The stamper only rewrites the literal src="/href=" of the shell's real tags.)
        Assert.Contains("css/app.css?v=1.0.0+e37bba3b", result);
    }

    [Fact]
    public void ResolveShellVersion_DerivesStableDistinctStampFromFrameworkPayload()
    {
        // ResolveShellVersion takes the WEB ROOT and looks for {webRoot}/_framework beneath it, so
        // create a temp web root with an _framework/ subdir holding the published payload.
        var webRoot = Directory.CreateTempSubdirectory("shellver_").FullName;
        var framework = Path.Combine(webRoot, "_framework");
        Directory.CreateDirectory(framework);
        try
        {
            // A content-hashed asset name (the Blazor WASM publish layout: <assembly>.<hash>.<ext>).
            File.WriteAllText(Path.Combine(framework, "blazor.webassembly.js"), "loader");
            File.WriteAllText(Path.Combine(framework, "Iris.Web.Client.abc123.wasm"), "wasm");
            File.WriteAllText(Path.Combine(framework, "Iris.Web.Client.abc123.wasm.br"), "wasm.br");

            var v1 = WebAppFactory.ResolveShellVersion(webRoot);
            Assert.NotNull(v1);
            // The stamp is <major.minor.patch>+<8 hex chars>.
            Assert.Matches(@"^\d+\.\d+\.\d+\+[0-9a-f]{8}$", v1!);

            // Stable: the same payload yields the same stamp.
            Assert.Equal(v1, WebAppFactory.ResolveShellVersion(webRoot));

            // Distinct: a changed payload (a renamed content-hashed asset) yields a different stamp.
            File.Delete(Path.Combine(framework, "Iris.Web.Client.abc123.wasm"));
            File.Delete(Path.Combine(framework, "Iris.Web.Client.abc123.wasm.br"));
            File.WriteAllText(Path.Combine(framework, "Iris.Web.Client.def456.wasm"), "wasm2");
            var v2 = WebAppFactory.ResolveShellVersion(webRoot);
            Assert.NotNull(v2);
            Assert.NotEqual(v1, v2);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveShellVersion_MissingOrEmptyDirectory_ReturnsNull()
    {
        // No _framework/ under the web root.
        Assert.Null(WebAppFactory.ResolveShellVersion("/nonexistent"));

        // An _framework/ that exists but is empty.
        var webRoot = Directory.CreateTempSubdirectory("shellver_empty_").FullName;
        Directory.CreateDirectory(Path.Combine(webRoot, "_framework"));
        try
        {
            Assert.Null(WebAppFactory.ResolveShellVersion(webRoot));
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }
}
