using Iris.Samples.SampleBlazorClient;
using Iris.Samples.SampleBlazorClient.Explorer;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace Iris.Samples.SampleBlazorClient;

/// <summary>
/// The Blazor WebAssembly host entry point (Deliverable B). Registers the explorer's client pipeline
/// (<see cref="ExplorerSession"/>) in DI and renders the routed app shell. This file is compiled only
/// for the WASM build (the default); the console smoke entry (<c>ConsoleSmoke</c>) is compiled only
/// under <c>-p:ConsoleSmoke=true</c>, so the two never coexist in one assembly.
/// </summary>
public static class Program
{
    /// <summary>
    /// Runs the WASM host.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    public static async Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);
        builder.RootComponents.Add<App>("#app");

        // The instance base-URL config surface (SAMPLE_PLAN §4.4): advertised host → browser base URL.
        // The public instance (iris.luit.ink) serves the explorer over https on port 8088; the local
        // Docker instance is reachable at the host-published port 8081. Pre-seeding both lets the UI
        // pre-fill the base URL for a known host so a logon only needs the address + password.
        builder.Services.AddIrisExplorer(new InstanceBaseUrls(new[]
        {
            new KeyValuePair<string, Uri>("iris.luit.ink", new Uri("https://iris.luit.ink")),
            new KeyValuePair<string, Uri>("localhost", new Uri("http://localhost:8081")),
            // The local two-instance compose stack advertises iris-a as iris-dev1.luit.ink (port 8081)
            // and iris-b as iris-dev2.luit.ink (port 8082); map each advertised host to its
            // host-published port so a logon by the advertised handle dials the right instance.
            new KeyValuePair<string, Uri>("iris-dev1.luit.ink", new Uri("http://localhost:8081")),
            new KeyValuePair<string, Uri>("iris-dev2.luit.ink", new Uri("http://localhost:8082")),
        }));

        var host = builder.Build();
        await host.RunAsync();
    }
}
