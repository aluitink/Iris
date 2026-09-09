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
Uri? advertiseBase = !string.IsNullOrWhiteSpace(advertiseBaseSetting) ? new Uri(advertiseBaseSetting) : null;

builder.Services.AddHttpClient("iris", client =>
{
    client.BaseAddress = serverBaseUri;
});

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
        serverBaseUri);
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
