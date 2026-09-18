using System.Net.Http;
using Iris.Client;
using Iris.Client.Auth;
using Iris.Core.Identity;
using Iris.Web.Client;
using Iris.Web.Client.Accounts;
using Iris.Web.Client.Components;
using Iris.Web.Client.Ui;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

var serverBaseUri = new Uri(builder.HostEnvironment.BaseAddress);

// The instance's advertised (public FQDN) ActivityPub base (IRIS_ADVERTISE_BASE). When the browser dials
// the instance on a different origin than this (e.g. http://localhost:8088 dialing a FQDN-advertised
// instance), the session's ActivityPub clients must rewrite FQDN-addressed requests to same-origin so the
// browser can sign writes and read the owner-only key (a cross-origin request would be CORS-blocked and
// cannot carry the site cookie). Null when unset (the client dials the same origin it advertises).
var advertiseBaseSetting = builder.Configuration["Iris:AdvertiseBase"];
// The canonical (public FQDN) advertised base, as configured in the WASM's appsettings.json. This is the
// host local actor IRIs use and what the SameOriginApHandler matches-and-rewrites to same-origin.
var canonicalAdvertiseBase =
    !string.IsNullOrWhiteSpace(advertiseBaseSetting) ? new Uri(advertiseBaseSetting) : null;

// Multi-instance: when the static AdvertiseBase host differs from the browser's origin (the same WASM
// build is deployed to multiple instances, each with its own FQDN), use the browser's origin as the
// EFFECTIVE advertise base. The effective base drives DialBaseUri / ProxyBaseUrl / NamespaceIri, so the
// session treats the browser's origin as its home instance (correct proxy routing + namespace for this
// instance). The ORIGINAL canonical FQDN is kept separately (canonicalAdvertiseBase) and passed to the
// session as the rewrite base, so the SameOriginApHandler still rewrites FQDN-addressed requests to
// same-origin (a cross-origin FQDN request would be CORS-blocked). No-op for single-instance deployments.
var advertiseBase = canonicalAdvertiseBase;
if (advertiseBase is not null
    && !string.Equals(advertiseBase.DnsSafeHost, serverBaseUri.DnsSafeHost, StringComparison.OrdinalIgnoreCase))
{
    advertiseBase = serverBaseUri;
}

// The plain "iris" client dials same-origin, but absolute FQDN IRIs (the instance's canonical
// advertised base, e.g. https://iris.luit.ink) would be cross-origin from the browser's dial host
// (e.g. http://localhost:8088) and CORS-blocked. Anonymous (signed-out) public reads — the actor
// document, the outbox/followers/following collections, the public feed — go through this client, so
// wrap it in the same SameOriginApHandler rewrite the signed session uses (88.4). When no advertised
// base is configured, the handler is a pass-through.
builder.Services
    .AddHttpClient("iris")
    .ConfigurePrimaryHttpMessageHandler(() => new SameOriginApHandler(new HttpClientHandler(), canonicalAdvertiseBase, serverBaseUri))
    .ConfigureHttpClient(client => client.BaseAddress = serverBaseUri);

builder.Services.AddHttpClient("iris-notifications", client =>
{
    client.BaseAddress = serverBaseUri;
});

// The WASM client uses an in-memory key store + key provider (keys are ephemeral per browser session).
builder.Services.AddSingleton<IKeyStore, InMemoryKeyStore>();
builder.Services.AddSingleton<IKeyProvider, InMemoryKeyProvider>();
builder.Services.AddAuthorizationCore(options =>
{
    options.AddPolicy("Admin", policy => policy.RequireRole("Admin"));
});
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
{
    // Use the named "iris" client (BaseAddress = the server origin) so the root-relative
    // "/local/v1/session" URL resolves. The default HttpClient has no base address and would
    // throw a UriFormatException, which the provider swallows → always unauthenticated.
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("iris");
    return new CookieAuthenticationStateProvider(http);
});
builder.Services.AddScoped<IActorSessionAccessor>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new ActorSessionAccessor(
        sp.GetRequiredService<AuthenticationStateProvider>(),
        factory.CreateClient("iris"),
        sp.GetRequiredService<IKeyStore>(),
        sp.GetRequiredService<IKeyProvider>(),
        sp.GetRequiredService<IActivityPubClientFactory>(),
        sp.GetRequiredService<IJSRuntime>(),
        advertiseBase,
        serverBaseUri,
        canonicalAdvertiseBase);
});
builder.Services.AddScoped<NotificationService>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new NotificationService(factory.CreateClient("iris-notifications"));
});
builder.Services.AddScoped<UiContext>();
builder.Services.AddSingleton<IActivityPubClientFactory, ActivityPubClientFactory>();

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var host = builder.Build();

// A fire-and-forget task (e.g. a page's `_ = LoadActorAsync(...)`, `PagedCollection`'s
// `_ = LoadInitialAsync()`, or the notification badge's poll) that throws an exception the task never
// observes would, by default, be reported to the Blazor renderer and trip the global "An unhandled
// error has occurred" overlay — even though the affected UI has already rendered fine and the user is
// interacting normally. Such exceptions are non-fatal (the component usually swallows or retries the
// next tick), so observe them here: log to the console for diagnosis and keep the UI up rather than
// showing a full-page error banner over a working app.
var unobservedLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Iris.Web.Client.UnobservedTasks");
TaskScheduler.UnobservedTaskException += (sender, args) =>
{
    unobservedLogger.LogError(
        "Unobserved exception in a fire-and-forget task (suppressed to avoid the Blazor error UI): {Message}",
        args.Exception.Message);
    args.SetObserved();
};

await host.RunAsync();
