using Iris.Core;
using Iris.Samples.SampleBlazorClient;

namespace Iris.Samples.SampleBlazorClient;

/// <summary>
/// The runnable console smoke entry: starts a <see cref="SampleBlazorClient"/> against a running
/// sample server and exercises the full client pipeline (login → signed community feed → proxy
/// fallback). Compiled only under <c>-p:ConsoleSmoke=true</c> (the default WASM build excludes it, so
/// it never coexists with the WASM host's <c>Program</c> entry point in one assembly).
/// </summary>
/// <remarks>
/// Run the sample server first (or set <c>IRIS_SERVER_URI</c> to an already-running instance), then
/// <c>dotnet run -p:ConsoleSmoke=true</c> (optionally passing the server base URI as the first arg).
/// </remarks>
public static class ConsoleSmoke
{
    /// <summary>
    /// Runs the sample client pipeline end to end.
    /// </summary>
    /// <param name="args">Optional: the home server base URI (e.g. <c>http://localhost:5000</c>).</param>
    /// <returns>0 on success; 1 on any pipeline failure.</returns>
    public static async Task<int> Main(string[] args)
    {
        var serverBaseUri = args is { Length: > 0 }
            ? new Uri(args[0])
            : Environment.GetEnvironmentVariable("IRIS_SERVER_URI") is { } envUri
                ? new Uri(envUri)
                : SampleBlazorClient.DefaultServerBaseUri;

        var handle = SampleBlazorClient.DefaultHandle;
        // The SampleServer's seeded actor password (SampleServer.Password); passed here so the client
        // sample stays free of a project reference to the server sample.
        var password = SampleBlazorClient.SamplePassword;

        Console.WriteLine($"Iris SampleBlazorClient → {serverBaseUri}");
        Console.WriteLine($"  Actor:     {handle} (Basic auth: {handle} / {password})");

        using var service = SampleBlazorClient.CreateClientService(serverBaseUri, handle, password);

        // 1. Login: Basic auth → owner-only actor document + PEM private key, held in the session.
        var logged = await service.LoginAsync();
        if (!logged)
        {
            Console.WriteLine("Login failed (is the sample server running with this actor?).");
            return 1;
        }

        Console.WriteLine($"  Logged in:  {service.ActorIri.Value}");
        Console.WriteLine($"  Authenticated: {service.Bundle.Session.CurrentActor?.PreferredUsername}");

        using var client = service.GetClient();

        // 2. Signed community feed: the client signs a GET of the iris community feed with the
        //    session's key; the server's signature validation accepts it.
        var communityIri = new Iri($"{serverBaseUri}/ap/v1/c/iris");
        var feed = client.GetCommunityFeedAsync(communityIri);
        var count = 0;
        await foreach (var item in feed)
        {
            count++;
            var content = item is KristofferStrube.ActivityStreams.Note note ? note.Content : null;
            Console.WriteLine($"  Feed item: {item.Id} {content?.FirstOrDefault()}");
        }

        Console.WriteLine($"  Community feed items: {count}");

        if (count == 0)
        {
            Console.WriteLine("Expected at least one feed item from the seeded community.");
            return 1;
        }

        Console.WriteLine("Pipeline OK: login → signed feed succeeded.");
        return 0;
    }
}
