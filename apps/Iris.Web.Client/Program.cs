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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

var serverBaseUri = new Uri(builder.HostEnvironment.BaseAddress);

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
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
{
    // Use the named "iris" client (BaseAddress = the server origin) so the root-relative
    // "/local/v1/session" URL resolves. The default HttpClient has no base address and would
    // throw a UriFormatException, which the provider swallows → always unauthenticated.
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("iris");
    return new CookieAuthenticationStateProvider(http);
});
builder.Services.AddScoped<IActorSessionAccessor, ActorSessionAccessor>();
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
await host.RunAsync();
