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

        builder.Services.AddIrisExplorer();

        var host = builder.Build();
        await host.RunAsync();
    }
}
