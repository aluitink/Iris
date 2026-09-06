using System.Text.Json;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.Data;
using Iris.Server.Data.Accounts;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Server.Stores;
using Iris.Web.Accounts;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Iris.Web;

/// <summary>
/// The Iris production app's composition root. Wires the Blazor Web App, the ActivityPub server
/// (via <c>Iris.Server</c>'s <c>AddActivityPubServer</c> / <c>MapActivityPubEndpoints</c>, unchanged),
/// in-memory persistence, and the single seeded local actor, then builds the application. Exposed as a
/// public seam so the integration tests (<c>Iris.Web.Tests</c>) can boot the identical host in-process
/// (via <see cref="BuildApp(WebApplicationBuilder, string)"/>) against a <c>TestServer</c>.
/// </summary>
/// <remarks>
/// <para>
/// Slice 32.1 is the <em>bare</em> host: it proves the library's federation endpoints (WebFinger,
/// actor document, inbox, outbox) work inside the new Blazor Web App process, before production
/// persistence (32.2), local auth (32.3), and the product screens (32.4) are layered on. The
/// composition order follows <c>docs/plans/production-app-web-host-structure.md</c> §2: Razor
/// components first, then the ActivityPub server, then the app pipeline (signature validation +
/// ActivityPub endpoints, then the Razor component endpoints).
/// </para>
/// <para>
/// The host binds to <c>http://localhost:8088</c> by default (the production port, see
/// <c>production-app-overview.md</c> §2 — the reverse proxy for <c>https://iris.luit.ink</c> targets
/// host 8088). The <em>advertised</em> base URI defaults to <c>http://localhost:8088</c> too; an
/// operator sets <c>Iris:AdvertiseBase</c> to expose the instance under a public hostname while
/// listening elsewhere.
/// </para>
/// </remarks>
public static class WebAppFactory
{
    /// <summary>
    /// The handle of the single seeded local actor (the instance actor the server signs outbound
    /// federation requests as). Later slices (32.3, auth) replace the fixed seed with
    /// per-account-provisioned actors.
    /// </summary>
    public const string SeedHandle = "alice";

    /// <summary>
    /// The host port the app listens on and (by default) advertises. This is the production port for
    /// <c>https://iris.luit.ink</c> (the reverse proxy targets host 8088).
    /// </summary>
    public const int DefaultPort = 8088;

    /// <summary>
    /// Wires the services (Blazor, ActivityPub server, in-memory persistence, seeded actor, key
    /// registration) onto <paramref name="builder"/>'s service collection.
    /// </summary>
    /// <param name="builder">The web application builder. Must not be null.</param>
    /// <param name="advertisedBase">
    /// The advertised public base URI (e.g. <c>http://localhost:8088</c> or
    /// <c>https://iris.luit.ink</c>). Path segments (<c>/ap/v1/u/{handle}</c>, <c>/ns#</c>) are
    /// appended to this, so it must be slash-free. When null, <c>http://localhost:8088</c> is used.
    /// </param>
    public static void ConfigureServices(WebApplicationBuilder builder, string? advertisedBase = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var baseString = string.IsNullOrWhiteSpace(advertisedBase)
            ? $"http://localhost:{DefaultPort}"
            : advertisedBase.TrimEnd('/');
        // Actor/community IRIs must be built exactly the way the server derives them (BuildActorIri:
        // BaseUri.Value.TrimEnd('/') + "/ap/v1/...") — so the seeded actor's IRI is identical to the one
        // the WebFinger / actor-document / inbox handlers resolve (otherwise a trailing-slash mismatch
        // makes the seeded actor 404).
        var baseUri = new Iri(baseString);
        var baseNoSlash = baseUri.Value.TrimEnd('/');
        var actorIri = new Iri($"{baseNoSlash}/ap/v1/u/{SeedHandle}");

        // 1. The Blazor Web App (Interactive Server render mode is the MVP's whole-app render mode,
        //    per production-app-web-host.md §2).
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        // 2. The ActivityPub server (unchanged library call). The namespace is derived from the
        //    advertised base URI ({base}/ns#) — the production default when NamespaceIri is unset
        //    (Phase 31.8) — and hosted as a resolvable JSON-LD context at {base}/ns.
        builder.Services.AddActivityPubServer(options =>
        {
            options.BaseUri = baseUri;
            options.InstanceName = $"iris-{HostLabel(baseString)}";
            options.InstanceActorId = actorIri;
            options.NamespaceIri = new Iri($"{baseNoSlash}/{ActivityPubServerConstants.NamespaceRouteSegment}#");
        });

        // 3. Persistence: the EF Core (PostgreSQL) provider when a connection string is configured
        //    (Iris:ConnectionString), otherwise the in-memory provider (the slice 32.1 default, and the
        //    default for the integration tests). Both bind the same IPersistenceProvider seam. A shared
        //    IKeyStore singleton is the seed target; the signer and the provider's Keys both use it.
        var keyStore = new InMemoryKeyStore();
        builder.Services.AddSingleton<IKeyStore>(keyStore);
        builder.Services.AddSingleton<ISignatureSigner>(new HttpSignatureSigner(keyStore));
        if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Iris") ?? builder.Configuration["Iris:ConnectionString"]))
        {
            // In-memory (the default): bind the concrete instance so resolving IPersistenceProvider
            // returns it verbatim and never triggers AddActivityPubServer's fallback factory.
            builder.Services.AddSingleton<IPersistenceProvider>(new InMemoryPersistenceProvider());
        }
        else
        {
            // EF Core (PostgreSQL): registered by AddEntityFrameworkPersistence (instance binding).
            builder.Services.AddEntityFrameworkPersistence(builder.Configuration);
        }

        // 6. Owner credential validation. AddActivityPubServer registers a no-op default (which denies
        //    every Basic-auth read); replace it so the seeded actor can read its owner-only surfaces
        //    (the inbox collection and the actor document's privateKey extension). The bare host's
        //    seed credential is handle/handle; slice 32.3 (local auth) replaces this with real accounts.
        builder.Services.AddSingleton<IActorCredentialValidator>(new BasicAuthCredentialValidator(
            (actorIri, username, password) =>
            {
                var valid = actorIri == new Iri($"{baseNoSlash}/ap/v1/u/{SeedHandle}")
                    && username == SeedHandle
                    && password == SeedHandle;
                return new ValueTask<bool>(valid);
            }));

        // 7. Local auth (slice 32.3). Cookie authentication for the browser session + the account/
        //    actor services. The in-memory account store is the default; the EF path registers its own
        //    IUserAccountStore via AddEntityFrameworkPersistence (registered later, so it wins for the
        //    EF provider — the in-memory one is only used when the EF provider is not registered).
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "iris.auth";
                options.LoginPath = "/login";
                options.LogoutPath = "/logout";
                options.ExpireTimeSpan = TimeSpan.FromDays(14);
                options.SlidingExpiration = true;
            });
        builder.Services.AddAuthorization();

        // The account + actor-provisioning services (the "bootstrap mechanism").
        builder.Services.TryAddSingleton<PasswordHasher>();
        builder.Services.TryAddSingleton<ILoginRateLimiter>(
            new SlidingWindowLoginRateLimiter(maxAttempts: 5, window: TimeSpan.FromMinutes(15)));
        builder.Services.AddSingleton<ActorProvisioner>(sp => new ActorProvisioner(
            sp.GetRequiredService<IPersistenceProvider>(),
            sp.GetRequiredService<IKeyStore>(),
            sp.GetRequiredService<IKeyProvider>(),
            baseUri));
        builder.Services.TryAddSingleton<IUserAccountStore, InMemoryUserAccountStore>();
        builder.Services.AddSingleton<RegistrationService>();
        builder.Services.AddSingleton<LoginService>();
        builder.Services.AddScoped<IActorSessionAccessor, ActorSessionAccessor>();
    }

    /// <summary>
    /// Builds the application: runs the middleware pipeline (routing, signature validation, the
    /// ActivityPub endpoints, then the Razor component endpoints) and registers the seeded actor's key
    /// with the server's <c>IKeyProvider</c> (so the proxy endpoint and outbound DeliveryWorker can
    /// sign as it).
    /// </summary>
    /// <param name="builder">The web application builder (its services must already be configured via
    /// <see cref="ConfigureServices"/>).</param>
    /// <param name="advertisedBase">
    /// The advertised public base URI (must match the one passed to
    /// <see cref="ConfigureServices"/>; when null, <c>http://localhost:8088</c>).
    /// </param>
    /// <returns>The fully built <see cref="WebApplication"/> (not yet run).</returns>
    public static WebApplication BuildApp(WebApplicationBuilder builder, string? advertisedBase = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var baseString = string.IsNullOrWhiteSpace(advertisedBase)
            ? $"http://localhost:{DefaultPort}"
            : advertisedBase.TrimEnd('/');
        var baseNoSlash = new Iri(baseString).Value.TrimEnd('/');
        var app = builder.Build();

        // Migrate the database (EF only; no-op for in-memory), seed the single local actor, and
        // register its key.
        InitializePersistence(app.Services, builder.Configuration, baseNoSlash);

        ConfigurePipeline(app, baseNoSlash);
        return app;
    }

    /// <summary>
    /// Applies the full middleware + endpoint pipeline (routing, antiforgery, signature validation,
    /// cookie auth, the Blazor Web App, static assets, the local-auth endpoints, and the ActivityPub
    /// endpoints) to a built <see cref="WebApplication"/>. The integration tests reproduce this
    /// pipeline on a <c>TestServer</c> (see <c>LocalAuthIntegrationTests</c>) so the exact production
    /// behavior — including the <c>/register</c>/<c>/login</c>/<c>/logout</c> auth endpoints and cookie
    /// auth — is exercised in-process.
    /// </summary>
    /// <param name="app">The built application.</param>
    /// <param name="baseNoSlash">The advertised public base URI (slash-free).</param>
    public static void ConfigurePipeline(WebApplication app, string baseNoSlash)
    {
        app.UseRouting();
        app.UseAntiforgery();
        // Inbound federation signature validation (a signed POST to a local inbox is verified; unsigned
        // inbox POSTs are rejected 401 by the inbox handler).
        app.UseSignatureValidation();
        // Cookie authentication + authorization (the local-account auth scheme).
        app.UseAuthentication();
        app.UseAuthorization();
        // The Blazor Web App (the product UI; serves / and the static assets).
        app.MapRazorComponents<Components.App>().AddInteractiveServerRenderMode();
        // The Blazor framework files (_framework/blazor.web.js) + the app's static assets (wwwroot,
        // e.g. css/app.css) — served via the static web assets endpoint (required for the interactive
        // circuit to boot; without it the page renders but the JS cannot load).
        app.UseStaticFiles();
        app.MapStaticAssets();
        // The local-account auth endpoints (see <see cref="MapAuthEndpoints"/>). The interactive Blazor
        // circuit cannot set cookies (read-only response headers), so sign-in/out happen here in plain
        // HTTP requests.
        MapAuthEndpoints(app);

        // The versioned ActivityPub endpoints (/.well-known/webfinger, /ap/v1/...).
        app.MapActivityPubEndpoints();
    }

    /// <summary>
    /// Maps the local-account auth endpoints: <c>POST /register</c> (handle + password),
    /// <c>POST /login</c> (handle + password), and <c>POST /logout</c>. Each runs in a real HTTP
    /// request (writable response) so it can set the auth cookie via <c>SignInAsync</c> /
    /// <c>SignOutAsync</c>, then redirects to the home page. The matching <c>GET</c> routes are
    /// served by the Razor pages (the forms). These are the only way a local account signs in —
    /// the interactive Blazor circuit cannot set cookies.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder (a <see cref="WebApplication"/>, or the
    /// builder from <c>UseEndpoints</c> in a <c>TestServer</c> pipeline).</param>
    public static void MapAuthEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/register", async (
            [FromForm] string? handle,
            [FromForm] string? password,
            [FromForm] string? displayName,
            RegistrationService registration,
            HttpContext ctx) =>
        {
            var result = await registration.RegisterAsync(handle, password ?? string.Empty,
                string.IsNullOrWhiteSpace(displayName) ? null : displayName);
            if (!result.Succeeded)
            {
                // Redirect back with the error in the query (the page reads it and displays it).
                return Results.Redirect($"/register?error={Uri.EscapeDataString(result.Error!)}");
            }
            await ctx.SignInAsync(
                Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
                new System.Security.Claims.ClaimsPrincipal(Iris.Web.Accounts.ClaimsFactory.CreateIdentity(result.Account!)),
                new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true });
            return Results.Redirect("/");
        });

        endpoints.MapPost("/login", async (
            [FromForm] string? handle,
            [FromForm] string? password,
            LoginService login,
            HttpContext ctx) =>
        {
            var remoteIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var result = await login.LoginAsync(handle, password ?? string.Empty, remoteIp);
            if (!result.Succeeded)
            {
                return Results.Redirect($"/login?error={Uri.EscapeDataString(result.Error!)}");
            }
            await ctx.SignInAsync(
                Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
                new System.Security.Claims.ClaimsPrincipal(Iris.Web.Accounts.ClaimsFactory.CreateIdentity(result.Account!)),
                new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true });
            return Results.Redirect("/");
        });

        endpoints.MapGet("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(
                Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });
    }

    /// <summary>
    /// Migrates the database (EF provider only; a no-op for the in-memory provider), seeds the single
    /// local actor (writing its signing key to the registered <see cref="IKeyStore"/>), and registers
    /// that key with the server's <see cref="IKeyProvider"/>. Called by <see cref="BuildApp"/> and by
    /// the integration-test host (which re-runs the endpoint pipeline over the same service collection).
    /// </summary>
    /// <param name="services">The application service provider (from a built app or test host).</param>
    /// <param name="configuration">The application configuration (for the EF connection string).</param>
    /// <param name="baseString">The advertised public base URI (slash-free).</param>
    public static void InitializePersistence(IServiceProvider services, IConfiguration configuration, string baseString)
    {
        var baseNoSlash = new Iri(baseString).Value.TrimEnd('/');
        var persistence = services.GetRequiredService<IPersistenceProvider>();
        var keyStore = services.GetRequiredService<IKeyStore>();
        if (persistence is EntityFrameworkPersistenceProvider)
        {
            persistence.EnsureCreatedAsync(configuration).GetAwaiter().GetResult();
        }
        SeedActor(persistence, keyStore, new Iri($"{baseNoSlash}/ap/v1/u/{SeedHandle}"), SeedHandle);
        // Register the seeded actor's key so the proxy / DeliveryWorker can sign as it.
        RegisterSeedKey(services, baseNoSlash);

        // Bootstrap the first admin (idempotent; no-op unless App:Admin:Username/Password are set). This
        // runs here — after the migration + seed, once persistence is ready — rather than as an
        // IHostedService (which would run at Build(), before the EF migration creates the tables).
        var bootstrapper = new AdminBootstrapper(
            services, configuration, new Iri(baseString),
            services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AdminBootstrapper>>());
        bootstrapper.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Boots the host end-to-end (services + pipeline) with the given advertised base, returning the
    /// built app. The single seam the integration tests use to host the app in a <c>TestServer</c>.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <param name="advertisedBase">The advertised public base URI (see
    /// <see cref="ConfigureServices"/>).</param>
    /// <returns>The fully built <see cref="WebApplication"/>.</returns>
    public static WebApplication CreateWebApplication(WebApplicationBuilder builder, string? advertisedBase = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ConfigureServices(builder, advertisedBase);
        return BuildApp(builder, advertisedBase);
    }

    /// <summary>
    /// Registers the seeded local actor's signing key with the server's <see cref="IKeyProvider"/>, so the
    /// proxy endpoint and the outbound <c>DeliveryWorker</c> can sign as it. Resolves the
    /// <see cref="IKeyProvider"/> from the given provider and registers the seeded key IRI
    /// (<c>{actor}/#key-1</c>).
    /// </summary>
    /// <param name="services">The application service provider.</param>
    /// <param name="baseString">The advertised public base URI (slash-free).</param>
    public static void RegisterSeedKey(IServiceProvider services, string baseString)
    {
        var baseNoSlash = new Iri(baseString).Value.TrimEnd('/');
        var actorIri = new Iri($"{baseNoSlash}/ap/v1/u/{SeedHandle}");
        services.GetRequiredService<IKeyProvider>().RegisterKey(actorIri, new Iri($"{actorIri}#key-1"));
    }

    /// <summary>
    /// Seeds a local <see cref="Person"/> actor (with an RSA signing key, served as
    /// <c>publicKeyPem</c> in its document) under the given IRI/handle. Idempotent by IRI (re-seeding
    /// replaces the actor and re-mints the key).
    /// </summary>
    /// <param name="persistence">The persistence provider to seed.</param>
    /// <param name="keyStore">The key store the seeded signing key is written to.</param>
    /// <param name="actorIri">The actor's IRI (<c>{base}/ap/v1/u/{handle}</c>).</param>
    /// <param name="handle">The actor's preferred username.</param>
    internal static void SeedActor(IPersistenceProvider persistence, IKeyStore keyStore, Iri actorIri, string handle)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(keyStore);
        var keyIri = new Iri($"{actorIri}#key-1");
        var key = KeyPairGenerator.GenerateRsa(keyIri);
        keyStore.PutKey(key);

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
        persistence.Actors.PutActorAsync(actor).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Extracts the host label (hostname) from a base URI string (used to derive the instance name).
    /// </summary>
    private static string HostLabel(string baseString)
        => Uri.TryCreate(baseString, UriKind.Absolute, out var uri) ? uri.Host : baseString;
}
