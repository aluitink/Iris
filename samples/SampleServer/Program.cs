using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Persistance;
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
    /// The name of the seeded community (its IRI is <c>{base}/ap/v1/c/{name}</c>).
    /// </summary>
    public const string SampleCommunityName = "iris";

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
        // The listen address is intentionally decoupled from the *advertised* host: the container
        // always binds http://+:8080 (so the compose port mapping 8081:8080 and the in-network peer
        // base iris-?:8080 keep working), while the advertised actor/community IRIs may carry a
        // public hostname (Iris__AdvertiseHost / Iris__AdvertiseHttps / Iris__AdvertisePort) that a
        // reverse proxy in front of the container terminates TLS for. When the advertise vars are
        // unset they default to the legacy behavior (advertise == listen host/scheme/port).
        var envPort = int.TryParse(Environment.GetEnvironmentVariable("Iris__Port"), out var p) ? p : 5000;

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
            .UseUrls($"http://+:{envPort}")
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

        // Advertised host/scheme/port default to the legacy values (Iris:HostName / Iris:Https /
        // Iris:Port) unless the operator overrides them with the Iris:Advertise* keys, which let the
        // instance expose its IRIs under a public hostname while still listening on http://+:8080.
        var legacyHost = configuration["Iris:HostName"] ?? "localhost";
        var legacyPort = int.TryParse(configuration["Iris:Port"], out var parsedPort) ? parsedPort : 5000;
        var legacyHttps = bool.TryParse(configuration["Iris:Https"], out var httpsFlag) && httpsFlag;

        var hostName = configuration["Iris:AdvertiseHost"] ?? legacyHost;
        var port = int.TryParse(configuration["Iris:AdvertisePort"], out var advertisedPort) ? advertisedPort : legacyPort;
        var useHttps = configuration["Iris:AdvertiseHttps"] is string ah
            ? bool.TryParse(ah, out var advertisedHttps) && advertisedHttps
            : legacyHttps;
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

        // Persistence is opt-in file-backed (Phase 19.0.1): when Iris__PersistenceDirectory is set the
        // server binds the Phase 16.4 file-backed provider (every store + the signing keys survive a
        // container recreation on the compose volume), and the outbound delivery queue is journaled in
        // the same directory (Phase 16.2) so pending deliveries survive too. When unset (the default —
        // every local dev run and the whole existing test surface) the in-memory provider is used,
        // exactly as before.
        //
        // The directory is read from the host's IConfiguration (not Environment.GetEnvironmentVariable)
        // so an in-process host (the TestServer test surface) cannot leak the setting across tests via
        // process-level environment state — the configuration is per-host. The container's env var
        // (Iris__PersistenceDirectory) lands in the same configuration via the host's default
        // environment source.
        var persistenceDirectory = configuration["Iris:PersistenceDirectory"];
        IPersistenceProvider persistence;
        if (string.IsNullOrWhiteSpace(persistenceDirectory))
        {
            persistence = new InMemoryPersistenceProvider();
        }
        else
        {
            Directory.CreateDirectory(persistenceDirectory);
            persistence = new FileBackedPersistenceProvider(persistenceDirectory);
            services.UseFileBackedDelivery(
                Path.Combine(persistenceDirectory, "delivery-queue.jsonl"),
                Path.Combine(persistenceDirectory, "delivery-dead-letter.jsonl"));
        }

        // Seed the sample graph (actors, community, follows, outbox content). The seed is idempotent by
        // IRI (Phase 19.0.2): it never re-mints a key or re-appends a seeded outbox item when the
        // volume already holds it, and it never touches state created after seeding (a follow made in a
        // prior evaluation turn survives a recreation). It returns the primary actor's key IRI whether
        // the key was minted now or recovered from the volume, so key registration below is uniform.
        var seed = SeedSampleData(persistence, baseString, actorHandle, actorIri, configuration);

        // The client-side signer (used by the proxy endpoint to sign as the authenticated actor and by
        // the outbound DeliveryWorker) is not registered by AddActivityPubServer (which wires only the
        // inbound verifier); the sample registers it over the seeded key store so any seeded actor can
        // sign.
        services.AddSingleton<ISignatureSigner>(new HttpSignatureSigner(persistence.Keys));

        services.AddRouting();
        // The Blazor WASM explorer dials the instance cross-origin (the browser base URL is the
        // host-published port / public proxy, not the server's own origin), so the server must answer
        // CORS preflights. The allowed origins come from Iris__CorsOrigins (comma-separated, e.g.
        // "http://localhost:8090,https://explorer.example"); when unset, only the local sample UI
        // origin is allowed. Credentials are enabled so Basic-auth / Bearer headers can be sent.
        var corsOriginsCsv = configuration["Iris:CorsOrigins"] ?? "http://localhost:8090";
        var corsOrigins = corsOriginsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        services.AddCors(options => options.AddDefaultPolicy(policy =>
            policy
                .WithOrigins(corsOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()));
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
                peerBase,
                actorIri.Value[..actorIri.Value.IndexOf("/ap/v1/", StringComparison.Ordinal)]);
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
        // Answer CORS preflights for the cross-origin WASM explorer (see AddCors in ConfigureServices).
        app.UseCors();
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
                // The seeded set includes the community (its key is the primary actor's key — the
                // community signs its own outbound deliveries as the community, so without this
                // registration the DeliveryWorker dead-letters them with "No signing identity
                // registered for actor '.../c/iris'" — F-1911-3).
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
    /// <remarks>
    /// <para>
    /// The seed is <em>idempotent by IRI</em> (Phase 19.0.2): it is safe to run against a non-empty
    /// (file-backed) store. A key is minted only when the key store has no entry for its key IRI — a
    /// recreated container reuses the persisted key, so a signature made before the recreation still
    /// verifies after it and no actor is ever re-keyed. A seeded outbox item (each note, the reply, the
    /// like) is appended only when its IRI is absent from the actor's outbox, so recreations never
    /// duplicate seeded content. Everything else — actor/community documents, membership, follow/like/
    /// reply edges — is keyed by IRI and put/recorded unconditionally, which is a no-op overwrite for an
    /// existing entry. State created <em>after</em> seeding (a follow made during an evaluation turn, a
    /// user post, a delivered inbound activity) is never touched: the seed writes only its own fixed
    /// IRIs, and the outbox guard is per-IRI, so post-seed outbox items survive a recreation untouched.
    /// </para>
    /// </remarks>
    /// <param name="persistence">The persistence provider to seed (in-memory or file-backed).</param>
    /// <param name="baseString">The instance base URI as a slash-free string (e.g.
    /// <c>http://localhost:5000</c>). A slash-free string is required because a host-only <see
    /// cref="Iri"/>/<see cref="Uri"/> canonicalizes to carry a trailing slash, which would double the
    /// slash when a path segment is appended.</param>
    /// <param name="actorHandle">The primary actor's handle.</param>
    /// <param name="actorIri">The primary actor's IRI.</param>
    /// <param name="configuration">The host's <see cref="IConfiguration"/>; read only for the
    /// opt-in <c>Iris:DumpKeyTo</c> switch (the key-dump mechanism for the S10 smoke test helper).
    /// Passing <see langword="null"/> (direct library callers that don't host a server) simply
    /// disables the key dump.</param>
    /// <returns>The seed metadata (handles and key IRIs) the host uses for credentials and key
    /// registration.</returns>
    public static SeedMetadata SeedSampleData(
        IPersistenceProvider persistence,
        string baseString,
        string actorHandle,
        Iri actorIri,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(baseString);
        ArgumentNullException.ThrowIfNull(actorHandle);

        // The actor IRIs (bob, the community) are derived from the PRIMARY ACTOR's advertised host,
        // not the base string: the base string is what the sample LISTENS on (the container's
        // in-network origin), while the primary actor's IRI carries the advertised host (the public
        // proxy's hostname, e.g. iris-dev1.luit.ink). A client that logs on as the primary actor sees
        // the advertised IRIs everywhere, so the seeded set it can sign as must use them too.
        var aliceIri = actorIri;
        var aliceKeyIri = new Iri($"{aliceIri}#key-1");
        var aliceKey = EnsureKey(persistence, aliceKeyIri, static _ => KeyPairGenerator.GenerateRsa(_));
        var alice = EnsureActor(persistence, actorHandle, aliceIri, aliceKeyIri, aliceKey);
        var hostBase = actorIri.Value[..actorIri.Value.IndexOf("/ap/v1/", StringComparison.Ordinal)];

        // Opt-in manually-approves-followers gate (Resolved Decision #46 / J-10): when the host sets
        // Iris__ManuallyApprovesFollowers=true, the primary actor (alice) is seeded with the
        // manuallyApprovesFollowers extension, so an inbound follow of alice is NOT auto-accepted —
        // the operator decides with the follow-decision endpoint (Accept/Reject). Set on the fresh
        // EnsureActor object (the seed's PutActorAsync overwrites the stored actor on each boot), so the
        // flag is idempotent across recreations. Default false keeps auto-accept (the usual sample path).
        if (string.Equals(configuration?["Iris:ManuallyApprovesFollowers"], "true", StringComparison.OrdinalIgnoreCase))
        {
            alice.ExtensionData ??= new Dictionary<string, JsonElement>();
            alice.ExtensionData[ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName] =
                JsonDocument.Parse("true").RootElement.Clone();
            // EnsureActor already stored alice (without the flag); re-persist now that the extension is set.
            persistence.Actors.PutActorAsync(alice).GetAwaiter().GetResult();
        }

        // The Phase 8 S10 smoke test drives a signed cross-container Follow from this instance's alice,
        // which requires signing with alice's private key. curl (the smoke test's HTTP client) cannot
        // produce an ActivityPub HTTP signature, so the smoke test runs a small IrisSigner helper that
        // signs with the key. To give the helper the key without hard-coding a secret in the repo, the
        // sample can dump the acting actor's private-key PEM to a local path (the Iris__DumpKeyTo env
        // var; in-container, world-readable) when set. This is a sample-only, opt-in, local mechanism —
        // no secret is committed, and a production instance never sets it. On a recreation the dumped
        // PEM is the recovered (persisted) key — the same key, re-exported. Read from the host's
        // IConfiguration (not the process environment) for the same per-host isolation reason as
        // PersistenceDirectory.
        var dumpKeyTo = configuration?["Iris:DumpKeyTo"];
        if (!string.IsNullOrWhiteSpace(dumpKeyTo))
        {
            File.WriteAllText(dumpKeyTo, aliceKey.ExportPrivateKeyPem());
        }

        var bobIri = new Iri($"{hostBase}/ap/v1/u/{BobHandle}");
        var bobKeyIri = new Iri($"{bobIri}#key-1");
        var bobKey = EnsureKey(persistence, bobKeyIri, static _ => KeyPairGenerator.GenerateRsa(_));
        var bob = EnsureActor(persistence, BobHandle, bobIri, bobKeyIri, bobKey);

        var carlaIri = new Iri($"http://{RemoteHostName}/ap/v1/u/{CarlaHandle}");
        var carlaKeyIri = new Iri($"{carlaIri}#key-1");
        var carlaKey = EnsureKey(persistence, carlaKeyIri, static id => Ed25519Key.Generate(id));
        var carla = EnsureActor(persistence, CarlaHandle, carlaIri, carlaKeyIri, carlaKey);

        var communityIri = new Iri($"{hostBase}/ap/v1/c/{SampleCommunityName}");
        var community = new Group
        {
            Id = communityIri.Value,
            Name = ["The Iris Community"],
            PreferredUsername = "iris",
        };
        community.ExtensionData ??= new Dictionary<string, JsonElement>();
        community.ExtensionData[ActivityPubExtensionNames.PublicKey] = JsonSerializer.SerializeToElement(new
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
        // community's follow of her). All edge stores are keyed (source, target) — recording an
        // existing edge is a no-op, so this is safe across recreations.
        persistence.Follows.RecordFollowAsync(aliceIri, bobIri).GetAwaiter().GetResult();
        persistence.Follows.RecordFollowAsync(bobIri, aliceIri).GetAwaiter().GetResult();
        persistence.Follows.RecordFollowAsync(aliceIri, carlaIri).GetAwaiter().GetResult();
        persistence.Follows.RecordFollowAsync(carlaIri, aliceIri).GetAwaiter().GetResult();

        // Outbox content: a note per actor, a reply from bob to alice's note, and a like from carla of
        // alice's note (the like exercises the Like activity type and the remote-host actor). Each
        // seeded outbox item is appended only when its IRI is not already in the outbox (idempotent by
        // IRI — see the remarks), so a recreation of a file-backed instance never duplicates them.
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
        AddSeededOutboxItem(persistence, aliceIri, aliceNote);
        AddSeededOutboxItem(persistence, bobIri, bobNote);
        AddSeededOutboxItem(persistence, carlaIri, carlaNote);

        // The outbox holds the activity, but the object document endpoint (GET /ap/v1/{**path}) and
        // global search read the *object store*, not the outbox. Storing each note makes it fetchable
        // by IRI (the explorer's object view) and searchable (the directory / community search). The
        // object store is keyed by IRI, so re-storing a recreation's copy is a no-op overwrite.
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
        AddSeededOutboxItem(persistence, bobIri, reply);
        persistence.Objects.PutObjectAsync(reply).GetAwaiter().GetResult();
        persistence.Replies.RecordReplyAsync(new Iri(aliceNote.Id!), new Iri(reply.Id!)).GetAwaiter().GetResult();

        var like = new Like
        {
            Id = $"{carlaIri.Value}/likes/1",
            Actor = [new Link { Href = carlaIri.Uri }],
            Object = [new Link { Href = new Uri(aliceNote.Id!) }],
        };
        AddSeededOutboxItem(persistence, carlaIri, like);
        persistence.Likes.RecordLikeAsync(carlaIri, new Iri(aliceNote.Id!)).GetAwaiter().GetResult();

        return new SeedMetadata(
            [actorHandle, BobHandle, CarlaHandle],
            [
                (actorHandle, aliceKeyIri),
                (BobHandle, bobKeyIri),
                (CarlaHandle, carlaKeyIri),
            ],
            communityIri,
            aliceKeyIri);
    }

    /// <summary>
    /// Resolves (or mints) a seeded actor's signing key: when the key store already holds a key for
    /// <paramref name="keyIri"/> (a recreation of a file-backed instance), that persisted key is
    /// recovered; otherwise a fresh key is generated and stored. This is what makes the seed idempotent
    /// across recreations — the actor keeps its key, so signatures made before the recreation still
    /// verify after it.
    /// </summary>
    /// <param name="persistence">The persistence provider (its key store is consulted).</param>
    /// <param name="keyIri">The key's IRI (the actor IRI + <c>#key-1</c>).</param>
    /// <param name="generate">Mints a fresh key when none is persisted.</param>
    /// <returns>The key to use (recovered or freshly minted and stored).</returns>
    private static ISigningKey EnsureKey(
        IPersistenceProvider persistence,
        Iri keyIri,
        Func<Iri, ISigningKey> generate)
    {
        if (persistence.Keys.TryGetKey(keyIri, out var existing) && existing is not null)
        {
            return existing;
        }

        var fresh = generate(keyIri);
        persistence.Keys.PutKey(fresh);
        return fresh;
    }

    /// <summary>
    /// Builds (or re-puts) a seeded <see cref="Person"/> actor document. The document is keyed by the
    /// actor IRI, so re-putting it on a recreation is a no-op overwrite (same IRI, same key IRI, same
    /// public key when the key was recovered from the volume).
    /// </summary>
    /// <param name="persistence">The persistence provider to store the actor in.</param>
    /// <param name="handle">The actor's preferred username (handle).</param>
    /// <param name="actorIri">The actor's IRI.</param>
    /// <param name="keyIri">The actor's key IRI (<c>actorIri#key-1</c>).</param>
    /// <param name="key">The actor's key pair (already in the key store).</param>
    /// <returns>The stored actor.</returns>
    private static Person EnsureActor(
        IPersistenceProvider persistence,
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
        actor.ExtensionData[ActivityPubExtensionNames.PublicKey] = JsonSerializer.SerializeToElement(new
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
        persistence.Actors.PutActorAsync(actor).GetAwaiter().GetResult();
        return actor;
    }

    /// <summary>
    /// Appends a seeded outbox item only when its IRI is not already present in the actor's outbox —
    /// the outbox's idempotency guard (a recreation never duplicates seeded notes/replies/likes). The
    /// guard is per-IRI, so outbox items created after seeding (user posts, delivered inbound
    /// activities) are never touched by the seed.
    /// </summary>
    /// <param name="persistence">The persistence provider (its activity store is read and appended
    /// to).</param>
    /// <param name="actorIri">The actor whose outbox receives the item.</param>
    /// <param name="item">The seeded outbox item (an <see cref="IObject"/> with a fixed <c>Id</c>).</param>
    private static void AddSeededOutboxItem(
        IPersistenceProvider persistence,
        Iri actorIri,
        IObject item)
    {
        var itemIri = new Iri(item.Id!);
        var outbox = persistence.Activities.GetOutboxAsync(actorIri).GetAwaiter().GetResult();
        var alreadyPresent = outbox.Any(entry =>
            (entry is IObject obj ? new Iri(obj.Id!) : new Iri(((Link)entry).Href!)) == itemIri);
        if (!alreadyPresent)
        {
            persistence.Activities.AddToOutboxAsync(actorIri, item).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Returns the (handle, key IRI) pairs for the seeded actors, given the primary actor's IRI. The
    /// primary and bob derive from the primary's host; carla derives from <see
    /// cref="RemoteHostName"/>. Used by the host to register each actor's key with the
    /// <see cref="IKeyProvider"/>.
    /// </summary>
    /// <param name="primaryActorIri">The primary actor's IRI (its host and port are reused for bob).</param>
    /// <returns>The (handle, key IRI) pairs, in seed order: primary, bob, carla, and the community
    /// (the community's key is the primary actor's key — its <c>publicKey</c> extension points at it).</returns>
    public static IReadOnlyList<(string Handle, Iri KeyIri)> GetSeededKeyIris(Iri primaryActorIri)
    {
        var primaryHandle = primaryActorIri.Value[(primaryActorIri.Value.LastIndexOf('/') + 1)..];
        var hostBase = primaryActorIri.Value[..primaryActorIri.Value.IndexOf("/ap/v1/", StringComparison.Ordinal)];
        var bobIri = new Iri($"{hostBase}/ap/v1/u/{BobHandle}");
        var carlaIri = new Iri($"http://{RemoteHostName}/ap/v1/u/{CarlaHandle}");
        var communityIri = new Iri($"{hostBase}/ap/v1/c/{SampleCommunityName}");
        return
        [
            (primaryHandle, new Iri($"{primaryActorIri}#key-1")),
            (BobHandle, new Iri($"{bobIri}#key-1")),
            (CarlaHandle, new Iri($"{carlaIri}#key-1")),
            (SampleCommunityName, new Iri($"{primaryActorIri}#key-1")),
        ];
    }

    /// <summary>
    /// Builds the actor IRI for a seeded handle, given the primary actor's IRI. The primary and bob
    /// derive from the primary's host under <c>/ap/v1/u/</c>; the community derives from the
    /// primary's host under <c>/ap/v1/c/</c>; carla derives from <see cref="RemoteHostName"/>.
    /// </summary>
    /// <param name="primaryActorIri">The primary actor's IRI.</param>
    /// <param name="handle">The actor's handle (or the community's name).</param>
    /// <returns>The actor's IRI.</returns>
    public static Iri ActorIriFor(Iri primaryActorIri, string handle)
    {
        if (handle == CarlaHandle)
        {
            return new Iri($"http://{RemoteHostName}/ap/v1/u/{CarlaHandle}");
        }

        var hostBase = primaryActorIri.Value[..primaryActorIri.Value.IndexOf("/ap/v1/", StringComparison.Ordinal)];
        if (handle == SampleCommunityName)
        {
            return new Iri($"{hostBase}/ap/v1/c/{handle}");
        }

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
        actor.ExtensionData[ActivityPubExtensionNames.PublicKey] = JsonSerializer.SerializeToElement(new
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
/// (for the credential validator), the seeded (handle, key IRI) pairs (for key-provider registration),
/// and the seeded community's (IRI, key IRI) pair (for the community's key-provider registration —
/// a community's outbound deliveries, e.g. a community Follow, sign as the community).
/// </summary>
/// <param name="Handles">The seeded actor handles.</param>
/// <param name="Keys">The seeded (handle, key IRI) pairs, in seed order.</param>
/// <param name="CommunityIri">The seeded community's IRI.</param>
/// <param name="CommunityKeyIri">The seeded community's key IRI (the community signs with the
/// primary actor's key — its <c>publicKey</c> extension points at it).</param>
public sealed record SeedMetadata(
    IReadOnlyCollection<string> Handles,
    IReadOnlyList<(string Handle, Iri KeyIri)> Keys,
    Iri? CommunityIri = null,
    Iri? CommunityKeyIri = null);

/// <summary>
/// An <see cref="IActorDocumentFetcher"/> that resolves a <em>local-host</em> actor document from the
/// sample's own in-process store instead of over the network. An actor whose IRI is on the sample's
/// own base (alice, bob, and the community) is served from the store; an actor whose IRI is on another
/// host (carla, the remote-host stand-in) is not served — the sample has no knowledge of true remote
/// actors, exactly as a real instance would for an unknown host.
/// </summary>
public sealed class LocalActorDocumentFetcher(
    IPersistenceProvider persistence,
    string baseString,
    string? advertisedBase = null)
    : IActorDocumentFetcher
{
    private readonly IPersistenceProvider _persistence = persistence;
    private readonly string _baseString = baseString;
    private readonly string? _advertisedBase = advertisedBase;

    /// <inheritdoc/>
    public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        // Only serve local-host actors; a remote-host IRI is not a local actor (the sample cannot
        // resolve a true remote key, matching a real instance's behavior for an unknown host). The
        // actor's IRI may be on EITHER the listen base (the container's in-network origin — the peer's
        // deliveries address IRIs it learned over the wire) or the advertised base (the public proxy's
        // hostname — what a client sees and dials); both are local-host IRIs.
        var localBase = actorIri.Value.StartsWith(_baseString, StringComparison.Ordinal)
            ? _baseString
            : _advertisedBase is { } advertised
                && actorIri.Value.StartsWith(advertised, StringComparison.Ordinal)
                    ? advertised
                    : null;
        if (localBase is null)
        {
            return null;
        }

        var handle = HandleFromIri(actorIri);
        var localIri = new Iri($"{localBase}/ap/v1/u/{handle}");
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
    string? peerBase,
    string advertisedBase)
    : IActorDocumentFetcher
{
    private readonly LocalActorDocumentFetcher _local = new(persistence, baseString, advertisedBase);
    private readonly Iri _ownActorIri = ownActorIri;
    private readonly IActivityPubClientFactory _clientFactory = clientFactory;
    private readonly Iri? _instanceActorIri = instanceActorIri;
    private readonly string _advertisedBase = advertisedBase;

    /// <inheritdoc/>
    public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        // Local-host actors (alice, bob, the community) are served from the store, no network I/O.
        // The actor's IRI may be on EITHER the listen base (the container's in-network origin — the
        // peer's deliveries address IRIs it learned over the wire) or the advertised base (the public
        // proxy's hostname — what a client sees and dials). Both are local-host IRIs.
        if (actorIri.Value.StartsWith(baseString, StringComparison.Ordinal)
            || actorIri.Value.StartsWith(_advertisedBase, StringComparison.Ordinal))
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
