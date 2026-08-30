using Microsoft.AspNetCore.Builder;

namespace Iris.Samples.IrisStaticHost;

/// <summary>
/// The minimal ASP.NET Core static-file host that serves the published Blazor WebAssembly site (the
/// <c>iris-ui</c> container, Deliverable B). The Blazor WASM app is a *static* site — the browser
/// downloads <c>index.html</c> + <c>_framework</c> and runs the app client-side — so the host only
/// serves static files from the WebRoot (the publish output's <c>wwwroot</c> folder) and makes no
/// outbound ActivityPub calls. It runs on port <see cref="Port"/> (the bind address is read from
/// <c>ASPNETCORE_URLS</c>, set by the Docker runtime stage), matching the <c>iris-ui</c> compose
/// service's <c>EXPOSE</c>/health check and host mapping.
/// </summary>
public static class Program
{
    /// <summary>
    /// The port the static host binds (the <c>iris-ui</c> compose service's advertised port).
    /// </summary>
    public const int Port = 8090;

    /// <summary>
    /// Runs the static host.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    public static void Main(string[] args)
    {
        // WebApplication.CreateBuilder wires Kestrel + configuration (environment + command line)
        // automatically. The bind address is read from ASPNETCORE_URLS (set by the Docker runtime
        // stage); the default is all interfaces on Port.
        var bind = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? $"http://+:{Port}";
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(bind);
        var app = builder.Build();

        // The WASM app's entry document is index.html. UseDefaultFiles() (no options) resolves a
        // directory request (e.g. "/") to the first of its default file names — which includes
        // index.html — so the WASM host page is served at the site root.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // OAuth2 callback route (Phase 15.2): when the browser completes the OAuth2 authorization-code
        // flow the server 302-redirects it here (/{callback}?code=…&state=…). This is a real server
        // route (mapped before the SPA fallback) so the browser lands on it and the WASM app (which is
        // served at /) can read the code + state from the address bar. The static host itself does not
        // exchange the code (it has no ActivityPub client); the WASM app's Home screen does, via
        // Explorer.OAuth2BrowserFlow.ExchangeCodeAsync. The endpoint returns the WASM index.html so the
        // app boots on the callback URL (Blazor client-side routing then renders Home, which reads the
        // ?code=…&state=… query string).
        var indexHtmlPath = Path.Combine(app.Environment.WebRootPath, "index.html");
        app.MapGet("/callback", () =>
            Results.Text(System.IO.File.ReadAllText(indexHtmlPath), "text/html"));

        // A catch-all fallback: any path that is not a static file resolves to the WASM index.html, so
        // the app's client-side routing (e.g. /actors, /community) works on a hard reload. The
        // static-file middleware serves real files (index.html, _framework/*, css/*) before this
        // fallback is reached.
        app.MapFallbackToFile("index.html");
        app.Run();
    }
}
