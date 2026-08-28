using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Samples.SampleServer;

/// <summary>
/// The sample ActivityPub server: an ASP.NET Core host that wires Iris.Server (in-memory persistence,
/// Basic auth, and a seeded actor + community) so the full client pipeline (auth → sign → community
/// feed → proxy fallback) can be exercised against a real, running instance.
/// </summary>
/// <remarks>
/// <see cref="CreateWebHostBuilder"/> is the single composition root, used both by <see cref="Program"/>
/// (the runnable entry point) and by the integration tests (which host it in an in-process
/// <c>TestServer</c>). Configuration is read from the <c>Iris:</c> configuration section:
/// <c>HostName</c> (default <c>localhost</c>), <c>Port</c> (default <c>5000</c>), <c>Https</c>
/// (default <c>false</c>), and <c>Actor</c> (default <c>alice</c>).
/// </remarks>
public static partial class SampleServer
{
    /// <summary>
    /// The sample actor's Basic-auth password.
    /// </summary>
    public const string Password = "iris-sample";

    /// <summary>
    /// Creates the <see cref="IWebHostBuilder"/> for the sample server.
    /// </summary>
    /// <param name="args">The command-line arguments (added to the host's command-line
    /// configuration, so <c>--Iris:Port=8080</c> etc. work). May be null.</param>
    /// <returns>A configured web host builder (not yet started).</returns>
    public static IWebHostBuilder CreateWebHostBuilder(string[]? args = null)
    {
        // Resolve the bind URL early (from environment variables) so the WebHostBuilder can UseUrls it.
        // The full Iris: configuration (including any command-line overrides) is read again inside
        // ConfigureServices, which is the authoritative source for the server's base URI.
        var envHost = Environment.GetEnvironmentVariable("Iris__HostName") ?? "localhost";
        var envPort = int.TryParse(Environment.GetEnvironmentVariable("Iris__Port"), out var p) ? p : 5000;
        var envScheme = bool.TryParse(Environment.GetEnvironmentVariable("Iris__Https"), out var h) && h ? "https" : "http";

        return new WebHostBuilder()
            .ConfigureAppConfiguration(cfg =>
            {
                // The WebHostBuilder already adds the environment source; the args here are appended so
                // callers can override the Iris: section from the CLI (e.g. --Iris:Port=8080).
                if (args is { Length: > 0 })
                {
                    cfg.AddCommandLine(args);
                }
            })
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.AddSimpleConsole(o => o.SingleLine = true);
                l.SetMinimumLevel(LogLevel.Information);
            })
            .UseUrls($"{envScheme}://{envHost}:{envPort}")
            .ConfigureServices(services => ConfigureServices(services))
            .Configure(app => ConfigureApp(app));
    }

    /// <summary>
    /// Wires the Iris server, in-memory persistence, the seeded data, and the Basic-auth credential
    /// validator into the service collection.
    /// </summary>
    /// <param name="services">The service collection to configure. Must not be null.</param>
    /// <param name="configuration">The configuration to read the <c>Iris:</c> section from. Must not
    /// be null.</param>
    public static void ConfigureServices(
        IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        configuration ??= new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var hostName = configuration["Iris:HostName"] ?? "localhost";
        var port = int.TryParse(configuration["Iris:Port"], out var parsedPort) ? parsedPort : 5000;
        var useHttps = bool.TryParse(configuration["Iris:Https"], out var httpsFlag) && httpsFlag;
        var scheme = useHttps ? "https" : "http";
        var actorHandle = configuration["Iris:Actor"] ?? "alice";
        // The Uri (and therefore the Iri that wraps it) canonicalizes a host-only base (e.g.
        // http://localhost:5000) to carry a trailing slash (http://localhost:5000/). Interpolating an
        // Iri or Uri when a path segment is appended would therefore double the slash (e.g.
        // //ap/v1/u/alice). So path-derivation builds from a plain, slash-free base string, while the
        // Iri itself is used wherever an Iri is the expected type (options.BaseUri, the key
        // registration). The seeded actor/community IRIs and the handler's constructed IRIs both derive
        // from the same slash-free base, so they match.
        var baseString = $"{scheme}://{hostName}:{port}";
        var baseUri = new Iri(baseString);
        var actorIri = new Iri($"{baseString}/ap/v1/u/{actorHandle}");

        var persistence = new InMemoryPersistenceProvider();
        SeedSampleData(persistence, baseString, actorHandle, actorIri);

        services.AddRouting();
        services.AddActivityPubServer(options =>
        {
            options.BaseUri = baseUri;
            options.InstanceName = $"iris-{hostName}";
            options.InstanceActorId = actorIri;
        });
        services.AddInMemoryPersistence();
        services.AddSingleton<IPersistenceProvider>(persistence);
        services.AddSingleton<IKeyStore>(persistence.Keys);
        services.AddSingleton<IActorCredentialValidator>(
            new BasicAuthCredentialValidator((_, username, password) =>
            {
                var valid = username == actorHandle &&
                    CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(password),
                        Encoding.UTF8.GetBytes(Password));
                return new ValueTask<bool>(valid);
            }));
    }

    /// <summary>
    /// Configures the application pipeline (signature validation, the ActivityPub endpoints, and the
    /// local key registration).
    /// </summary>
    /// <param name="app">The application builder. Must not be null.</param>
    public static void ConfigureApp(IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseRouting();
        // The Iris server endpoints are mapped onto an IEndpointRouteBuilder (MapActivityPubEndpoints),
        // so they must be registered via UseEndpoints. ASP0014 (prefer top-level route registrations)
        // is suppressed in the sample: the versioned route group cannot be expressed through minimal
        // APIs.
        app.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());

        // Register the local actor's key with the IKeyProvider so the proxy endpoint (which signs as
        // the authenticated actor via the X-Iris-Actor override) and the outbound DeliveryWorker can
        // find it. The Iri is a non-nullable value type, so the options value is null-checked directly
        // (a null-check on a value type is the idiomatic way to avoid the CA2264 no-op warning).
        var options = app.ApplicationServices.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value;
        if (options.InstanceActorId is { } actorIri)
        {
            var keyProvider = app.ApplicationServices.GetRequiredService<IKeyProvider>();
            keyProvider.RegisterKey(actorIri, new Iri($"{actorIri}#key-1"));
        }
    }

    /// <summary>
    /// Seeds the persistence store with the sample actor (a <see cref="Person"/>), a second actor
    /// (bob), and a sample community (the library's <see cref="Group"/> actor) with both actors as
    /// members and a post in each actor's outbox (so the community feed has content).
    /// </summary>
    /// <param name="persistence">The persistence provider to seed.</param>
    /// <param name="baseString">The instance base URI as a slash-free string (e.g.
    /// <c>http://localhost:5000</c>). A slash-free string is required because a host-only <see
    /// cref="Iri"/>/<see cref="Uri"/> canonicalizes to carry a trailing slash, which would double the
    /// slash when a path segment is appended.</param>
    /// <param name="actorHandle">The primary actor's handle.</param>
    /// <param name="actorIri">The primary actor's IRI.</param>
    public static void SeedSampleData(
        InMemoryPersistenceProvider persistence,
        string baseString,
        string actorHandle,
        Iri actorIri)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(baseString);
        ArgumentNullException.ThrowIfNull(actorHandle);

        var keyId = new Iri($"{actorIri}#key-1");
        var key = KeyPairGenerator.GenerateEcP256(keyId);
        persistence.Keys.PutKey(key);

        var actor = new Person
        {
            Id = actorIri.Value,
            PreferredUsername = actorHandle,
            Name = [actorHandle],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = keyId.Value,
            owner = actorIri.Value,
            kty = "EC",
            crv = "P-256",
            x = ExtractJwkComponent(key, "x"),
            y = ExtractJwkComponent(key, "y"),
        });
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();

        // A second local actor (bob) so the community has more than one member.
        var bobIri = new Iri($"{baseString}/ap/v1/u/bob");
        var bob = new Person
        {
            Id = bobIri.Value,
            PreferredUsername = "bob",
            Name = ["bob"],
        };
        persistence.ActorStore.PutActorAsync(bob).GetAwaiter().GetResult();

        // A sample community (the library's Group actor) with both actors as members.
        var communityIri = new Iri($"{baseString}/ap/v1/c/iris");
        var community = new Group
        {
            Id = communityIri.Value,
            Name = ["The Iris Community"],
            PreferredUsername = "iris",
        };
        persistence.Communities.PutCommunityAsync(community).GetAwaiter().GetResult();
        persistence.Communities.AddMemberAsync(communityIri, actorIri).GetAwaiter().GetResult();
        persistence.Communities.AddMemberAsync(communityIri, bobIri).GetAwaiter().GetResult();

        // A post in each actor's outbox (so the community feed has content).
        var publicAudience = new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") };
        var note1 = new Note
        {
            Id = $"{actorIri.Value}/notes/1",
            AttributedTo = [actor],
            To = [publicAudience],
            Content = ["<p>Welcome to the Iris sample server!</p>"],
        };
        var note2 = new Note
        {
            Id = $"{bobIri.Value}/notes/1",
            AttributedTo = [bob],
            To = [publicAudience],
            Content = ["<p>Bob says hello from the community.</p>"],
        };
        persistence.Activities.AddToOutboxAsync(actorIri, note1).GetAwaiter().GetResult();
        persistence.Activities.AddToOutboxAsync(bobIri, note2).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Extracts a named component from a <see cref="KeyPair"/>'s public JWK.
    /// </summary>
    /// <param name="key">The key pair.</param>
    /// <param name="name">The JWK component name.</param>
    /// <returns>The component value.</returns>
    public static string ExtractJwkComponent(KeyPair key, string name)
    {
        using var doc = JsonDocument.Parse(key.GetPublicJwk());
        return doc.RootElement.GetProperty(name).GetString()!;
    }
}

/// <summary>
/// The runnable entry point for the sample server.
/// </summary>
public static class Program
{
    /// <summary>
    /// Starts the sample server.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    public static void Main(string[] args)
    {
        var host = SampleServer.CreateWebHostBuilder(args).Build();
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var hostName = configuration["Iris:HostName"] ?? "localhost";
        var port = int.TryParse(configuration["Iris:Port"], out var parsedPort) ? parsedPort : 5000;
        var actorHandle = configuration["Iris:Actor"] ?? "alice";
        var scheme = bool.TryParse(configuration["Iris:Https"], out var httpsFlag) && httpsFlag ? "https" : "http";
        var baseString = $"{scheme}://{hostName}:{port}";

        Console.WriteLine($"Iris SampleServer running at {baseString}");
        Console.WriteLine($"  Actor:      {baseString}/ap/v1/u/{actorHandle}  (Basic auth: {actorHandle} / {SampleServer.Password})");
        Console.WriteLine($"  Community:  {baseString}/ap/v1/c/iris");
        Console.WriteLine($"  WebFinger:  {baseString}/.well-known/webfinger?resource=acct:{actorHandle}@{hostName}");

        host.Run();
    }
}
