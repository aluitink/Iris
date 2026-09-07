using System.Security.Claims;
using System.Text.Json;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.Data;
using Iris.Server.Data.Accounts;
using Iris.Server.Data.Stores;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Server.Stores;
using Iris.Web.Accounts;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    /// The default cap on an inbound HTTP request body, in bytes (1 MiB). ActivityPub federation
    /// payloads (an activity JSON document) are small — well under a few tens of KB in practice — so a
    /// 1 MiB ceiling is generous for legitimate traffic. It exists to bound the memory an
    /// **unauthenticated** inbound <c>POST /ap/v1/u/{handle}/inbox</c> can force the app to buffer:
    /// without it, Kestrel's default (no request-body limit) lets a malicious peer stream an
    /// arbitrarily large body into memory (a denial-of-service vector for a public instance). An
    /// operator raises it via <c>Iris:MaxRequestBodySize</c> (env <c>IRIS_MAX_REQUEST_BODY_SIZE</c>) if
    /// a legitimate use case (e.g. very large embedded media on an activity) requires it.
    /// </summary>
    public const long DefaultMaxRequestBodySize = 1024L * 1024L;

    /// <summary>
    /// The configuration key for the inbound request-body size cap (see
    /// <see cref="DefaultMaxRequestBodySize"/>). Bound from <c>Iris:MaxRequestBodySize</c>.
    /// </summary>
    public const string MaxRequestBodySizeConfigKey = "Iris:MaxRequestBodySize";

    /// <summary>
    /// The default host-shutdown delivery drain budget (slice 33.6): how long the <c>DeliveryWorker</c>
    /// gets, once the host's stopping token is cancelled (a SIGTERM), to finish its in-flight outbound
    /// deliveries before it stops. The host itself stops after <c>HostOptions.ShutdownTimeout</c>
    /// (30 s by default, set by <c>WebApplicationBuilder</c>'s default) — and that 30 s covers
    /// <em>every</em> hosted service in registration order (the EF persistence provider's
    /// <c>StopAsync</c> runs before the delivery worker's), so a 60 s worker drain budget would be
    /// truncated by the host's own timeout. The default is therefore 15 s: comfortably above the
    /// seconds-scale delivery round-trips it protects, and comfortably below the 30 s host budget, so
    /// the worker always returns before the host force-stops it. An operator raises it via
    /// <c>Iris:Delivery:ShutdownDrainTimeout</c> (env <c>IRIS_SHUTDOWN_DRAIN_TIMEOUT</c>) when its
    /// delivery traffic (or a busy queue) needs a longer drain — and should then also raise
    /// <c>DOTNET_HOST__SHUTDOWNTIMEOUT</c> so the host's own budget covers it.
    /// </summary>
    public static readonly TimeSpan DefaultShutdownDrainTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The configuration key for the host-shutdown delivery drain budget (slice 33.6). Bound from
    /// <c>Iris:Delivery:ShutdownDrainTimeout</c> (env <c>IRIS_SHUTDOWN_DRAIN_TIMEOUT</c>); the value is
    /// a <see cref="TimeSpan"/> string (e.g. <c>"00:00:30"</c>) or a number of seconds (e.g. <c>"30"</c>).
    /// Unset → <see cref="DefaultShutdownDrainTimeout"/>.
    /// </summary>
    public const string ShutdownDrainTimeoutConfigKey = "Iris:Delivery:ShutdownDrainTimeout";

    /// <summary>
    /// The name of the CORS policy registered when an operator opts into cross-origin access (slice
    /// 33.5). The policy allows only the origins listed in <see cref="CorsOriginsConfigKey"/> — never
    /// <c>AllowAnyOrigin</c>. When no origins are configured, no policy is registered and no CORS
    /// middleware runs, so the app is same-origin-only by default (the safe default for a public
    /// instance).
    /// </summary>
    public const string CorsPolicyName = "iris-api";

    /// <summary>
    /// The configuration key for the comma-separated list of origins allowed cross-origin access to the
    /// instance's API (slice 33.5). Bound from <c>Iris:Cors:Origins</c> (env
    /// <c>IRIS_CORS_ORIGINS</c>). Empty/unset → same-origin-only (no cross-origin access); each listed
    /// origin (a scheme+host, e.g. <c>https://app.example.org</c>) is granted access via the
    /// <see cref="CorsPolicyName"/> policy.
    /// </summary>
    public const string CorsOriginsConfigKey = "Iris:Cors:Origins";

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
        //    default for the integration tests). Both bind the same IPersistenceProvider seam.
        //
        // The IKeyStore is the seed/provisioning target and what the signer signs with. Under EF it is
        // bound to the durable EfKeyStore (so a local actor's signing key survives a restart — slice
        // 33.2); under in-memory it is a fresh InMemoryKeyStore (the default, ephemeral by design). The
        // signer is wired to the SAME IKeyStore instance so it signs with whatever key is registered.
        if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Iris") ?? builder.Configuration["Iris:ConnectionString"]))
        {
            // In-memory (the default): bind the concrete instance so resolving IPersistenceProvider
            // returns it verbatim and never triggers AddActivityPubServer's fallback factory.
            builder.Services.AddSingleton<IPersistenceProvider>(new InMemoryPersistenceProvider());
            var keyStore = new InMemoryKeyStore();
            builder.Services.AddSingleton<IKeyStore>(keyStore);
            builder.Services.AddSingleton<ISignatureSigner>(new HttpSignatureSigner(keyStore));
        }
        else
        {
            // EF Core (PostgreSQL): registered by AddEntityFrameworkPersistence (instance binding).
            builder.Services.AddEntityFrameworkPersistence(builder.Configuration);
            // The durable key store: IKeyStore IS the EfKeyStore (the same object the provider's Keys
            // uses), so a PutKey persists the private key to Postgres and a restart reads it back.
            // The signer resolves it lazily so the single EfKeyStore instance is shared.
            builder.Services.AddSingleton<ISignatureSigner>(sp => new HttpSignatureSigner(sp.GetRequiredService<IKeyStore>()));
            builder.Services.AddSingleton<IKeyStore>(sp => sp.GetRequiredService<EfKeyStore>());
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
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("Admin", policy => policy.RequireRole("Admin"));
        });

        // 7a. Signing-identity resolution that outlives the in-process key provider (slice 33.3). The
        // server's default IKeyProvider is an InMemoryKeyProvider that only knows actors registered with
        // it — at startup (the seed) or at request time (a freshly provisioned local actor). A local
        // actor whose key was written to the durable store by a different path (e.g. admin-assisted
        // provisioning) would otherwise be un-signable until a restart. This delegating provider falls
        // back to resolving the actor's key from the (durable) IKeyStore by the well-known {actor}#key-1
        // convention, so any local actor is signable on demand. Registered after AddActivityPubServer so
        // this hard AddSingleton wins over its TryAddSingleton<IKeyProvider, InMemoryKeyProvider>.
        builder.Services.AddSingleton<IKeyProvider>(sp => new DelegatingKeyProvider(
            new InMemoryKeyProvider(sp.GetRequiredService<IKeyStore>()),
            sp.GetRequiredService<IKeyStore>()));

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
        builder.Services.AddScoped<Iris.Web.Ui.UiContext>();
        builder.Services.AddScoped<NotificationService>();

        // 8. Inbound request-body size cap (slice 33.4): bound the memory an unauthenticated inbound
        // federation POST can force the app to buffer. Kestrel's default imposes no request-body limit,
        // so a malicious peer streaming a very large body into the inbox would allocate memory with no
        // ceiling. Cap it at DefaultMaxRequestBodySize (1 MiB — generous for an activity JSON document)
        // unless an operator raises it via Iris:MaxRequestBodySize (env IRIS_MAX_REQUEST_BODY_SIZE). A
        // request whose body exceeds the cap is rejected by Kestrel before the pipeline runs (413).
        // Applied only to Kestrel (the production server); it is a no-op for the TestServer host the
        // integration tests use, so existing tests that POST large bodies are unaffected.
        var maxBodyRaw = builder.Configuration[MaxRequestBodySizeConfigKey];
        var maxBody = long.TryParse(maxBodyRaw, out var parsed) && parsed > 0 ? parsed : DefaultMaxRequestBodySize;
        if (maxBody > 0)
        {
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = maxBody;
            });
        }

        // 9. CORS (slice 33.5): same-origin-only by default, opt-in for a non-UI API consumer. The
        // Blazor UI is same-origin and needs no CORS. A third-party app that wants to call this
        // instance's API from a different origin must be granted access explicitly: the operator sets
        // Iris:Cors:Origins (env IRIS_CORS_ORIGINS) to a comma-separated allow-list, and a named policy
        // (CorsPolicyName) is registered that allows ONLY those origins — never AllowAnyOrigin, and with
        // credentials so a cookie-authenticated cross-origin consumer can work. When the allow-list is
        // empty/unset, NO policy is registered and no CORS middleware runs, so the app is
        // same-origin-only by default (the safe default for a public instance — a cross-origin
        // preflight/GET simply gets no Access-Control-Allow-Origin header and the browser blocks it).
        // ConfigurePipeline applies app.UseCors(CorsPolicyName) only when the policy is registered.
        // 9a. Graceful shutdown (slice 33.6): an explicit host shutdown timeout + the delivery worker's
        // drain budget. WebApplicationBuilder already sets HostOptions.ShutdownTimeout to 30 s (the
        // framework default), and the DeliveryWorker's in-flight-delivery drain (its ExecuteAsync drain
        // loop, bounded by Iris:Delivery:ShutdownDrainTimeout) is awaited INSIDE that 30 s window — the
        // host stops its hosted services in order (persistence, then the queue-completion service, then
        // the worker) and gives the whole stop phase at most ShutdownTimeout. The drain budget is
        // therefore set below the host budget so the worker always finishes draining (or hits its own
        // bound) before the host force-stops it. The default budget (15 s) is applied here so the
        // production wiring is explicit and a single knob (Iris:Delivery:ShutdownDrainTimeout, env
        // IRIS_SHUTDOWN_DRAIN_TIMEOUT) governs it; an operator that raises it must also raise
        // DOTNET_HOST__SHUTDOWNTIMEOUT (the host's own budget) to match.
        var drainRaw = builder.Configuration[ShutdownDrainTimeoutConfigKey];
        if (string.IsNullOrWhiteSpace(drainRaw))
        {
            builder.Configuration[ShutdownDrainTimeoutConfigKey] = DefaultShutdownDrainTimeout.ToString();
        }
        if (TimeSpan.TryParse(drainRaw, out var drainTimeout) && drainTimeout > TimeSpan.Zero)
        {
            builder.Host.ConfigureHostOptions(options => options.ShutdownTimeout = drainTimeout);
        }
        else
        {
            builder.Host.ConfigureHostOptions(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
        }

        var corsOriginsRaw = builder.Configuration[CorsOriginsConfigKey];
        var corsOrigins = (corsOriginsRaw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (corsOrigins.Length > 0)
        {
            builder.Services.AddCors(options =>
            {
                options.AddPolicy(CorsPolicyName, policy => policy
                    .WithOrigins(corsOrigins)
                    .AllowCredentials()
                    .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                    .WithHeaders("Content-Type", "Authorization", "Signature", "Host"));
            });
        }
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
        // CORS (slice 33.5): applied only when an operator opted in via Iris:Cors:Origins (the
        // CorsPolicyName policy is registered in ConfigureServices only in that case). When no origins are
        // configured there is no policy, so this is skipped and the app stays same-origin-only by
        // default. Placed after UseRouting (so the policy can match endpoints) and before auth, so a
        // cross-origin preflight (OPTIONS) is answered before the signature/cookie gate.
        var corsOptions = app.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>>().Value;
        if (corsOptions.GetPolicy(CorsPolicyName) is not null)
        {
            app.UseCors(CorsPolicyName);
        }
        // Forwarded headers (X-Forwarded-Proto / X-Forwarded-For / X-Forwarded-Host), so the app sees the
        // client's real scheme + host when it sits behind a TLS-terminating reverse proxy (e.g. the
        // https://iris.luit.ink proxy → host 8088, see production-app-deployment.md §5). Without it the
        // app would see plain `http` and the auth cookie's Secure flag / any scheme-dependent redirect
        // would be wrong. It is part of the ASP.NET Core shared framework (Microsoft.AspNetCore.HttpOverrides)
        // — no extra package. It must run before UseAuthentication so the cookie + redirects see the real
        // scheme. When not behind a proxy the headers are absent and this is a no-op.
        app.UseForwardedHeaders();
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
        MapNotificationEndpoints(app);

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
    /// Maps the notification read-state endpoints: <c>POST /local/v1/notifications/read</c> (marks all
    /// notifications as read) and <c>GET /local/v1/notifications/unread-count</c> (returns the number of
    /// inbox items newer than the account's <c>NotificationsReadAt</c> cursor). Both are
    /// <c>[Authorize]</c>-gated (cookie auth) and resolve the signed-in user's account via the
    /// <c>sub</c> claim.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    public static void MapNotificationEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/local/v1/notifications/read", async (
            HttpContext ctx,
            IUserAccountStore accounts,
            IPersistenceProvider persistence,
            CancellationToken ct) =>
        {
            var sub = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(sub, out var accountId))
            {
                return Results.Unauthorized();
            }

            var account = await accounts.FindByIdAsync(accountId, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            var now = DateTimeOffset.UtcNow;
            await accounts.UpdateNotificationsReadAtAsync(account.Id, now, ct);

            // Count unread (for the response body, so the client can update its badge without a second call).
            var inbox = await persistence.Activities.GetInboxAsync(account.ActorId, ct);
            var unread = CountUnread(inbox, now);
            return Results.Json(new { unread });
        }).RequireAuthorization();

        endpoints.MapGet("/local/v1/notifications/unread-count", async (
            HttpContext ctx,
            IUserAccountStore accounts,
            IPersistenceProvider persistence,
            CancellationToken ct) =>
        {
            var sub = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(sub, out var accountId))
            {
                return Results.Unauthorized();
            }

            var account = await accounts.FindByIdAsync(accountId, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            var inbox = await persistence.Activities.GetInboxAsync(account.ActorId, ct);
            var unread = CountUnread(inbox, account.NotificationsReadAt);
            return Results.Json(new { unread });
        }).RequireAuthorization();
    }

    /// <summary>
    /// Counts inbox items whose <c>Published</c> timestamp is strictly after the given read cursor.
    /// When the cursor is null (never read), all items are unread.
    /// </summary>
    internal static int CountUnread(IReadOnlyList<IObjectOrLink> inbox, DateTimeOffset? readAt)
    {
        if (inbox.Count == 0)
        {
            return 0;
        }

        if (readAt is null)
        {
            return inbox.Count;
        }

        var count = 0;
        foreach (var item in inbox)
        {
            if (item is Activity { Published: not null } act && act.Published > readAt.Value.DateTime)
            {
                count++;
            }
        }

        return count;
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

        // Re-register every local account's signing key with the server's key provider (slice 33.2). The
        // key *store* is durable (EF) so each key survives a restart, but the in-process key *provider*
        // (actor→key mapping) is not — so after a restart the registered user actors (and the admin)
        // would otherwise be unable to sign outbound federation. Runs after the admin bootstrap so the
        // just-provisioned admin's key is registered too.
        RestoreLocalSigningKeys(services);
    }

    /// <summary>
    /// Re-registers each local account's signing key with the server's <see cref="IKeyProvider"/>, so a
    /// local actor can sign outbound federation after a restart (slice 33.2). The keys themselves live
    /// in the (durable) <see cref="IKeyStore"/>; only the actor→key-IRI mapping in the in-process
    /// <see cref="IKeyProvider"/> is lost on restart, so this pass rebuilds it from the persisted
    /// accounts. No-op for accounts whose key is not present in the key store.
    /// </summary>
    /// <param name="services">The application service provider.</param>
    public static void RestoreLocalSigningKeys(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var keyStore = services.GetRequiredService<IKeyStore>();
        var keyProvider = services.GetRequiredService<IKeyProvider>();
        var accounts = services.GetRequiredService<IUserAccountStore>().GetAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        foreach (var account in accounts)
        {
            // The key IRI follows the same convention ActorProvisioner / SeedActor use: {actor}#key-1.
            var keyIri = new Iri($"{account.ActorId}#key-1");
            if (keyStore.TryGetKey(keyIri, out _))
            {
                keyProvider.RegisterKey(account.ActorId, keyIri);
            }
        }
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
    /// <c>publicKeyPem</c> in its document) under the given IRI/handle. Idempotent by IRI: the actor
    /// document is re-stored, but the signing key is **reused** when one is already present in the key
    /// store (slice 33.2) — so a restart with a durable key store keeps the *same* key (and thus the
    /// same public key in the actor document), rather than minting a new one each boot. A fresh
    /// deployment (no key yet) mints one.
    /// </summary>
    /// <param name="persistence">The persistence provider to seed.</param>
    /// <param name="keyStore">The key store the seeded signing key is read from / written to.</param>
    /// <param name="actorIri">The actor's IRI (<c>{base}/ap/v1/u/{handle}</c>).</param>
    /// <param name="handle">The actor's preferred username.</param>
    internal static void SeedActor(IPersistenceProvider persistence, IKeyStore keyStore, Iri actorIri, string handle)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(keyStore);
        var keyIri = new Iri($"{actorIri}#key-1");
        // Reuse the persisted key when present (restart) so the actor's public key is stable; otherwise
        // mint a fresh RSA key (first boot). PutKey is a no-op refresh for a reused key.
        var key = keyStore.TryGetKey(keyIri, out var existing) && existing is not null
            ? existing
            : KeyPairGenerator.GenerateRsa(keyIri);
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
