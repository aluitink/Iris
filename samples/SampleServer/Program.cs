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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Samples.SampleServer;

/// <summary>
/// The sample ActivityPub server: an ASP.NET Core host that wires Iris.Server (in-memory persistence,
/// per-actor Basic auth, inbound federation signature validation, and a rich seeded graph: three
/// actors, a community, follows, and outbox notes, replies, and likes) so the full client pipeline
/// (auth → sign → community feed → proxy fallback) and the federation path can be exercised against a
/// real, running instance.
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
    /// The sample actors' shared Basic-auth password (every seeded actor authenticates with it; the
    /// handle is the username).
    /// </summary>
    public const string Password = "iris-sample";

    /// <summary>
    /// The handle of the second seeded local actor (bob).
    /// </summary>
    public const string BobHandle = "bob";

    /// <summary>
    /// The handle of the third seeded actor (carla). Carla's IRI is derived from
    /// <see cref="RemoteHostName"/> so her document looks like it comes from a second instance, which
    /// makes the seeded follow edges read as cross-instance federation even though all actors are
    /// served by this one server.
    /// </summary>
    public const string CarlaHandle = "carla";

    /// <summary>
    /// The host label for the third seeded actor (carla) — a stand-in for a remote instance.
    /// </summary>
    public const string RemoteHostName = "remote.example";

    /// <summary>
    /// The <c>iris:</c> namespace base IRI advertised on every seeded actor and community document.
    /// </summary>
    public static readonly Iri NamespaceIri = new("https://iris.example/ns#");

    /// <summary>
    /// The public-audience link (<c>as:Public</c>) used on every seeded note.
    /// </summary>
    private static readonly Link PublicAudience = new() { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") };

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
            // A bare WebHostBuilder (the generic host's UseKestrel is not applied) does not register
            // IServer in .NET 10; UseKestrel registers the Kestrel web server so the host can start.
            .UseKestrel()
            .UseUrls($"{envScheme}://{envHost}:{envPort}")
            .ConfigureServices(services => ConfigureServices(services))
            .Configure(app => ConfigureApp(app));
    }

    /// <summary>
    /// Wires the Iris server, in-memory persistence, the seeded data, and the per-actor Basic-auth
    /// credential validator into the service collection.
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
        var seed = SeedSampleData(persistence, baseString, actorHandle, actorIri);

        // The client-side signer (used by the proxy endpoint to sign as the authenticated actor and by
        // the outbound DeliveryWorker) is not registered by AddActivityPubServer (which wires only the
        // inbound verifier); the sample registers it over the seeded key store so any seeded actor can
        // sign.
        services.AddSingleton<ISignatureSigner>(new HttpSignatureSigner(persistence.Keys));

        services.AddRouting();
        services.AddActivityPubServer(options =>
        {
            options.BaseUri = baseUri;
            options.InstanceName = $"iris-{hostName}";
            options.InstanceActorId = actorIri;
            options.NamespaceIri = NamespaceIri;
        });
        services.AddInMemoryPersistence();
        services.AddSingleton<IPersistenceProvider>(persistence);
        services.AddSingleton<IKeyStore>(persistence.Keys);
        // Every seeded actor authenticates with the shared sample password; the validator checks the
        // username against the seed's handle set (the seed is fixed for the lifetime of the host).
        var seedHandles = seed.Handles;
        services.AddSingleton<IActorCredentialValidator>(
            new BasicAuthCredentialValidator((_, username, password) =>
            {
                var valid = seedHandles.Contains(username)
                    && CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(password),
                        Encoding.UTF8.GetBytes(Password));
                return new ValueTask<bool>(valid);
            }));

        // The sample serves all seeded actors in-process (including carla, whose IRI is on the remote
        // host label), so the inbound key resolver resolves them against this same store rather than
        // over the network. This is what makes the sample "federation-ready": a signed delivery to a
        // local inbox is verified by resolving the sender's key from the sender's own (local) document.
        //
        // The fetcher also resolves *remote* actor documents over the network (the cross-instance case:
        // when this instance delivers to a peer's actor, or validates a peer's signature, the peer's key
        // is read from the peer's own actor document, fetched by the peer's public base address). This is
        // what enables the S10 signed cross-container federation (a real Follow a→b + the proxy fallback)
        // over the Docker network — the two instances resolve each other's keys the way two real
        // instances would. The peer's base address is supplied per instance by the Iris__PeerBase env var
        // (the compose file sets it to the other service's in-network address).
        services.AddSingleton<IActorDocumentFetcher>(sp =>
        {
            var factory = sp.GetRequiredService<IActivityPubClientFactory>();
            var options = sp.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value;
            var peerBase = Environment.GetEnvironmentVariable("Iris__PeerBase");
            return new FederatedActorDocumentFetcher(
                persistence,
                baseString,
                actorIri,
                factory,
                options.InstanceActorId,
                peerBase);
        });
    }

    /// <summary>
    /// Configures the application pipeline (inbound federation signature validation, the ActivityPub
    /// endpoints, and the local key registrations).
    /// </summary>
    /// <param name="app">The application builder. Must not be null.</param>
    public static void ConfigureApp(IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseRouting();
        // Inbound federation signature validation: a signed POST to a local inbox is verified
        // (unsigned inbox POSTs are rejected 401 by the inbox handler). This is what makes the sample
        // "federation-ready": a remote (or same-process) server can deliver to the sample's inbox and
        // the sample verifies the sender's key.
        app.UseSignatureValidation();
        // The Iris server endpoints are mapped onto an IEndpointRouteBuilder (MapActivityPubEndpoints),
        // so they must be registered via UseEndpoints. ASP0014 (prefer top-level route registrations)
        // is suppressed in the sample: the versioned route group cannot be expressed through minimal
        // APIs.
        app.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());

        // Register every seeded actor's key with the IKeyProvider so the proxy endpoint (which signs as
        // the authenticated actor via the X-Iris-Actor override) and the outbound DeliveryWorker can
        // find it. The Iri is a non-nullable value type, so the options value is null-checked directly
        // (a null-check on a value type is the idiomatic way to avoid the CA2264 no-op warning).
        var options = app.ApplicationServices.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value;
        if (options.InstanceActorId is { } actorIri)
        {
            var keyProvider = app.ApplicationServices.GetRequiredService<IKeyProvider>();
            foreach (var (handle, keyIri) in GetSeededKeyIris(actorIri))
            {
                keyProvider.RegisterKey(ActorIriFor(actorIri, handle), keyIri);
            }
        }
    }

    /// <summary>
    /// Seeds the persistence store with the sample graph: the primary actor (alice) and bob on this
    /// instance's host, carla on a second host label (standing in for a remote instance), a community
    /// (the library's <see cref="Group"/> actor) with both local actors as members and a follow of
    /// carla, follow edges (alice ↔ bob mutual, alice → carla, carla → alice), and outbox content (a
    /// note per actor, a reply from bob to alice's note, and a like from carla of alice's note) so the
    /// community feed and the federation path have real data. Every actor gets a key pair (RSA for the
    /// local hosts, Ed25519 for the remote-host actor) published as a <c>publicKeyPem</c> in its
    /// document, and every key is registered in the store so the proxy endpoint can sign as any of
    /// them.
    /// </summary>
    /// <param name="persistence">The persistence provider to seed.</param>
    /// <param name="baseString">The instance base URI as a slash-free string (e.g.
    /// <c>http://localhost:5000</c>). A slash-free string is required because a host-only <see
    /// cref="Iri"/>/<see cref="Uri"/> canonicalizes to carry a trailing slash, which would double the
    /// slash when a path segment is appended.</param>
    /// <param name="actorHandle">The primary actor's handle.</param>
    /// <param name="actorIri">The primary actor's IRI.</param>
    /// <returns>The seed metadata (handles and key IRIs) the host uses for credentials and key
    /// registration.</returns>
    public static SeedMetadata SeedSampleData(
        InMemoryPersistenceProvider persistence,
        string baseString,
        string actorHandle,
        Iri actorIri)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(baseString);
        ArgumentNullException.ThrowIfNull(actorHandle);

        var aliceIri = actorIri;
        var aliceKeyIri = new Iri($"{aliceIri}#key-1");
        var aliceKey = KeyPairGenerator.GenerateRsa(aliceKeyIri);
        persistence.Keys.PutKey(aliceKey);
        var alice = BuildActor(persistence, actorHandle, aliceIri, aliceKeyIri, aliceKey);

        // The Phase 8 S10 smoke test drives a signed cross-container Follow from this instance's alice,
        // which requires signing with alice's private key. curl (the smoke test's HTTP client) cannot
        // produce an ActivityPub HTTP signature, so the smoke test runs a small IrisSigner helper that
        // signs with the key. To give the helper the key without hard-coding a secret in the repo, the
        // sample can dump the acting actor's private-key PEM to a local path (the Iris__DumpKeyTo env
        // var; in-container, world-readable) when set. This is a sample-only, opt-in, local mechanism —
        // no secret is committed, and a production instance never sets it.
        var dumpKeyTo = Environment.GetEnvironmentVariable("Iris__DumpKeyTo");
        if (!string.IsNullOrWhiteSpace(dumpKeyTo))
        {
            File.WriteAllText(dumpKeyTo, aliceKey.ExportPrivateKeyPem());
        }

        var bobIri = new Iri($"{baseString}/ap/v1/u/{BobHandle}");
        var bobKeyIri = new Iri($"{bobIri}#key-1");
        var bobKey = KeyPairGenerator.GenerateRsa(bobKeyIri);
        persistence.Keys.PutKey(bobKey);
        var bob = BuildActor(persistence, BobHandle, bobIri, bobKeyIri, bobKey);

        var carlaIri = new Iri($"http://{RemoteHostName}/ap/v1/u/{CarlaHandle}");
        var carlaKeyIri = new Iri($"{carlaIri}#key-1");
        var carlaKey = Ed25519Key.Generate(carlaKeyIri);
        persistence.Keys.PutKey(carlaKey);
        var carla = BuildActor(persistence, CarlaHandle, carlaIri, carlaKeyIri, carlaKey);

        var communityIri = new Iri($"{baseString}/ap/v1/c/iris");
        var community = new Group
        {
            Id = communityIri.Value,
            Name = ["The Iris Community"],
            PreferredUsername = "iris",
        };
        community.ExtensionData ??= new Dictionary<string, JsonElement>();
        community.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = aliceKeyIri.Value,
            owner = aliceIri.Value,
            publicKeyPem = aliceKey.ExportPublicKeyPem(),
        });
        community.ExtensionData["capabilities"] = JsonSerializer.SerializeToElement(new[]
        {
            "community:feeds",
            "community:moderation",
            "activity:Create",
            "activity:Follow",
            "activity:Like",
        });
        persistence.Communities.PutCommunityAsync(community).GetAwaiter().GetResult();
        persistence.Communities.AddMemberAsync(communityIri, aliceIri).GetAwaiter().GetResult();
        persistence.Communities.AddMemberAsync(communityIri, bobIri).GetAwaiter().GetResult();
        persistence.Communities.AddFollowAsync(communityIri, carlaIri).GetAwaiter().GetResult();

        // Follow edges: alice ↔ bob (mutual, so the community feed federates their content) and
        // alice ↔ carla (a "cross-instance" pair; carla's content reaches the community via the
        // community's follow of her).
        persistence.Follows.RecordFollowAsync(aliceIri, bobIri).GetAwaiter().GetResult();
        persistence.Follows.RecordFollowAsync(bobIri, aliceIri).GetAwaiter().GetResult();
        persistence.Follows.RecordFollowAsync(aliceIri, carlaIri).GetAwaiter().GetResult();
        persistence.Follows.RecordFollowAsync(carlaIri, aliceIri).GetAwaiter().GetResult();

        // Outbox content: a note per actor, a reply from bob to alice's note, and a like from carla of
        // alice's note (the like exercises the Like activity type and the remote-host actor).
        var aliceNote = new Note
        {
            Id = $"{aliceIri.Value}/notes/1",
            AttributedTo = [alice],
            To = [PublicAudience],
            Content = ["<p>Welcome to the Iris sample server!</p>"],
        };
        var bobNote = new Note
        {
            Id = $"{bobIri.Value}/notes/1",
            AttributedTo = [bob],
            To = [PublicAudience],
            Content = ["<p>Bob says hello from the community.</p>"],
        };
        var carlaNote = new Note
        {
            Id = $"{carlaIri.Value}/notes/1",
            AttributedTo = [carla],
            To = [PublicAudience],
            Content = ["<p>Carla here on the second host — federation is alive.</p>"],
        };
        persistence.Activities.AddToOutboxAsync(aliceIri, aliceNote).GetAwaiter().GetResult();
        persistence.Activities.AddToOutboxAsync(bobIri, bobNote).GetAwaiter().GetResult();
        persistence.Activities.AddToOutboxAsync(carlaIri, carlaNote).GetAwaiter().GetResult();

        // The outbox holds the activity, but the object document endpoint (GET /ap/v1/{**path}) and
        // global search read the *object store*, not the outbox. Storing each note makes it fetchable
        // by IRI (the explorer's object view) and searchable (the directory / community search).
        persistence.Objects.PutObjectAsync(aliceNote).GetAwaiter().GetResult();
        persistence.Objects.PutObjectAsync(bobNote).GetAwaiter().GetResult();
        persistence.Objects.PutObjectAsync(carlaNote).GetAwaiter().GetResult();

        var reply = new Note
        {
            Id = $"{bobIri.Value}/notes/2",
            AttributedTo = [bob],
            To = [new Link { Href = aliceIri.Uri }],
            Content = ["<p>Bob replies: glad you are here, Alice!</p>"],
            InReplyTo = [new Link { Href = new Uri(aliceNote.Id!) }],
        };
        persistence.Activities.AddToOutboxAsync(bobIri, reply).GetAwaiter().GetResult();
        persistence.Objects.PutObjectAsync(reply).GetAwaiter().GetResult();
        persistence.Replies.RecordReplyAsync(new Iri(aliceNote.Id!), new Iri(reply.Id!)).GetAwaiter().GetResult();

        var like = new Like
        {
            Id = $"{carlaIri.Value}/likes/1",
            Actor = [new Link { Href = carlaIri.Uri }],
            Object = [new Link { Href = new Uri(aliceNote.Id!) }],
        };
        persistence.Activities.AddToOutboxAsync(carlaIri, like).GetAwaiter().GetResult();
        persistence.Likes.RecordLikeAsync(carlaIri, new Iri(aliceNote.Id!)).GetAwaiter().GetResult();

        return new SeedMetadata(
            [actorHandle, BobHandle, CarlaHandle],
            [
                (actorHandle, aliceKeyIri),
                (BobHandle, bobKeyIri),
                (CarlaHandle, carlaKeyIri),
            ]);
    }

    /// <summary>
    /// Returns the (handle, key IRI) pairs for the seeded actors, given the primary actor's IRI. The
    /// primary and bob derive from the primary's host; carla derives from <see
    /// cref="RemoteHostName"/>. Used by the host to register each actor's key with the
    /// <see cref="IKeyProvider"/>.
    /// </summary>
    /// <param name="primaryActorIri">The primary actor's IRI (its host and port are reused for bob).</param>
    /// <returns>The (handle, key IRI) pairs, in seed order: primary, bob, carla.</returns>
    public static IReadOnlyList<(string Handle, Iri KeyIri)> GetSeededKeyIris(Iri primaryActorIri)
    {
        var primaryHandle = primaryActorIri.Value[(primaryActorIri.Value.LastIndexOf('/') + 1)..];
        var hostBase = primaryActorIri.Value[..primaryActorIri.Value.IndexOf("/ap/v1/", StringComparison.Ordinal)];
        var bobIri = new Iri($"{hostBase}/ap/v1/u/{BobHandle}");
        var carlaIri = new Iri($"http://{RemoteHostName}/ap/v1/u/{CarlaHandle}");
        return
        [
            (primaryHandle, new Iri($"{primaryActorIri}#key-1")),
            (BobHandle, new Iri($"{bobIri}#key-1")),
            (CarlaHandle, new Iri($"{carlaIri}#key-1")),
        ];
    }

    /// <summary>
    /// Builds the actor IRI for a seeded handle, given the primary actor's IRI. The primary and bob
    /// derive from the primary's host; carla derives from <see cref="RemoteHostName"/>.
    /// </summary>
    /// <param name="primaryActorIri">The primary actor's IRI.</param>
    /// <param name="handle">The actor's handle.</param>
    /// <returns>The actor's IRI.</returns>
    public static Iri ActorIriFor(Iri primaryActorIri, string handle)
    {
        if (handle == CarlaHandle)
        {
            return new Iri($"http://{RemoteHostName}/ap/v1/u/{CarlaHandle}");
        }

        var hostBase = primaryActorIri.Value[..primaryActorIri.Value.IndexOf("/ap/v1/", StringComparison.Ordinal)];
        return new Iri($"{hostBase}/ap/v1/u/{handle}");
    }

    /// <summary>
    /// Builds a <see cref="Person"/> actor document (with a <c>publicKeyPem</c> extension and the
    /// <c>iris:</c> capabilities) and stores it.
    /// </summary>
    /// <param name="persistence">The persistence provider to store the actor in.</param>
    /// <param name="handle">The actor's preferred username (handle).</param>
    /// <param name="actorIri">The actor's IRI.</param>
    /// <param name="keyIri">The actor's key IRI (<c>actorIri#key-1</c>).</param>
    /// <param name="key">The actor's key pair (already registered in the store).</param>
    /// <returns>The stored actor.</returns>
    private static Person BuildActor(
        InMemoryPersistenceProvider persistence,
        string handle,
        Iri actorIri,
        Iri keyIri,
        ISigningKey key)
    {
        var actor = new Person
        {
            Id = actorIri.Value,
            PreferredUsername = handle,
            Name = [handle],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = keyIri.Value,
            owner = actorIri.Value,
            publicKeyPem = key.ExportPublicKeyPem(),
        });
        actor.ExtensionData["capabilities"] = JsonSerializer.SerializeToElement(new[]
        {
            "activity:Create",
            "activity:Follow",
            "activity:Like",
        });
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();
        return actor;
    }

}

/// <summary>
/// The seed metadata returned by <see cref="SampleServer.SeedSampleData"/>: the seeded actor handles
/// (for the credential validator) and the seeded (handle, key IRI) pairs (for key-provider
/// registration).
/// </summary>
/// <param name="Handles">The seeded actor handles.</param>
/// <param name="Keys">The seeded (handle, key IRI) pairs, in seed order.</param>
public sealed record SeedMetadata(
    IReadOnlyCollection<string> Handles,
    IReadOnlyList<(string Handle, Iri KeyIri)> Keys);

/// <summary>
/// An <see cref="IActorDocumentFetcher"/> that resolves a <em>local-host</em> actor document from the
/// sample's own in-process store instead of over the network. An actor whose IRI is on the sample's
/// own base (alice, bob, and the community) is served from the store; an actor whose IRI is on another
/// host (carla, the remote-host stand-in) is not served — the sample has no knowledge of true remote
/// actors, exactly as a real instance would for an unknown host.
/// </summary>
public sealed class LocalActorDocumentFetcher(IPersistenceProvider persistence, string baseString)
    : IActorDocumentFetcher
{
    private readonly IPersistenceProvider _persistence = persistence;
    private readonly string _baseString = baseString;

    /// <inheritdoc/>
    public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        // Only serve local-host actors; a remote-host IRI is not a local actor (the sample cannot
        // resolve a true remote key, matching a real instance's behavior for an unknown host).
        if (!actorIri.Value.StartsWith(_baseString, StringComparison.Ordinal))
        {
            return null;
        }

        var handle = HandleFromIri(actorIri);
        var localIri = new Iri($"{_baseString}/ap/v1/u/{handle}");
        if (await _persistence.Actors.TryGetActorAsync(localIri, out var actor, ct).ConfigureAwait(false)
            && actor is not null)
        {
            return actor;
        }

        return null;
    }

    private static string HandleFromIri(Iri actorIri)
    {
        var value = actorIri.Value;
        var lastSlash = value.LastIndexOf('/');
        return lastSlash >= 0 ? value[(lastSlash + 1)..] : value;
    }
}

/// <summary>
/// An <see cref="IActorDocumentFetcher"/> for the two-instance sample composition: it resolves a
/// <em>local-host</em> actor document from this instance's own store (the local actors and community)
/// and a <em>remote-host</em> actor document by fetching it over the network from the peer instance's
/// public actor-document endpoint. This is what lets the two sample instances federate: when this
/// instance validates a signature from the peer's actor (or resolves the peer actor to deliver to), it
/// reads the peer's key from the peer's own actor document, fetched by the peer's base address — exactly
/// as two real instances resolve each other's keys.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LocalActorDocumentFetcher"/> serves only local-host actors (a remote-host IRI returns
/// null). This fetcher layers a network fetch on top for the remote case: a local-host IRI is served
/// from the store (no I/O); a remote-host IRI is fetched by <see cref="IActivityPubClient.GetActorAsync"/>
/// (the client's outbound federation transport, signed as this instance's actor) and deserialized to an
/// <see cref="Actor"/>. When no peer base is configured (the peer-base constructor argument is null —
/// e.g. a single-instance dev run or a unit test) the remote case returns null, preserving the original
/// local-only behavior.
/// </para>
/// </remarks>
public sealed class FederatedActorDocumentFetcher(
    IPersistenceProvider persistence,
    string baseString,
    Iri ownActorIri,
    IActivityPubClientFactory clientFactory,
    Iri? instanceActorIri,
    string? peerBase)
    : IActorDocumentFetcher
{
    private readonly LocalActorDocumentFetcher _local = new(persistence, baseString);
    private readonly Iri _ownActorIri = ownActorIri;
    private readonly IActivityPubClientFactory _clientFactory = clientFactory;
    private readonly Iri? _instanceActorIri = instanceActorIri;

    /// <inheritdoc/>
    public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        // Local-host actors (alice, bob, the community) are served from the store, no network I/O.
        if (actorIri.Value.StartsWith(baseString, StringComparison.Ordinal))
        {
            return _local.GetActorAsync(actorIri, ct);
        }

        // Remote-host actor (the peer instance's alice, e.g. http://iris-b:8080/ap/v1/u/alice). Without a
        // configured peer base there is nothing to fetch (a single-instance dev run) — return null, the
        // same as the local-only fetcher. With a peer base, fetch the peer's actor document over the
        // network: the client signs the GET as this instance's actor (a real federation fetch), and the
        // peer's document (public) is returned. The actorIri passed to GetActorAsync is the absolute
        // actor IRI (e.g. http://iris-b:8080/ap/v1/u/alice); the client dials it directly.
        if (peerBase is null)
        {
            return Task.FromResult<Actor?>(null);
        }

        return FetchRemoteActorAsync(actorIri, ct);
    }

    private async Task<Actor?> FetchRemoteActorAsync(Iri actorIri, CancellationToken ct)
    {
        // The instance actor signs the outbound fetch (a real federation fetch). When no instance actor
        // is configured (defensive), there is nothing to sign with — return null.
        if (_instanceActorIri is not { } signerIri)
        {
            return null;
        }

        using var client = _clientFactory.Create(
            new ActivityPubClientOptions { ActorId = signerIri, EnableRetry = false },
            new HttpClientHandler());
        var actor = await client.GetActorAsync(actorIri, ct).ConfigureAwait(false);
        return actor as Actor;
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
