using Iris.Core.Identity;
using Iris.Server.Data.Accounts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Accounts;

/// <summary>
/// Bootstraps the first <see cref="UserRole.Admin"/> account on a clean deployment. Invoked once at
/// startup, <em>after</em> persistence is ready (<see cref="WebAppFactory.InitializePersistence"/>,
/// i.e. after the EF migration + the seed actor): if no admin account exists and both
/// <c>App:Admin:Username</c> and <c>App:Admin:Password</c> are configured, it provisions one (same path
/// as registration, with <c>Role = Admin</c>). Idempotent — it never creates a second admin once one
/// exists. It is not an <see cref="IHostedService"/> (which would run at <c>Build()</c>, before the
/// migration); it is invoked directly once the provider is usable.
/// </summary>
/// <remarks>
/// This avoids the chicken-and-egg "how do I get my first admin" problem without a separate CLI. The
/// config keys are <c>App:Admin:*</c> (an <c>Iris.Web</c>-only concern — not <c>Iris:*</c>, which is
/// reserved for <c>AddActivityPubServer</c>'s bound options). The deployment doc notes these should be
/// unset (or rotated out of <c>.env</c>) after the first successful startup, since they otherwise stay
/// readable in the environment.
/// </remarks>
public sealed class AdminBootstrapper
{
    /// <summary>The configuration section for the bootstrap admin credentials.</summary>
    public const string Section = "App:Admin";

    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly Iri _baseUri;
    private readonly ILogger<AdminBootstrapper> _logger;

    /// <summary>
    /// Initializes the bootstrapper.
    /// </summary>
    /// <param name="services">The application service provider (the account store + provisioner are
    /// resolved here so they are only touched once the host has started).</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="baseUri">The advertised public base URI (used to derive the admin actor IRI).</param>
    /// <param name="logger">A logger.</param>
    public AdminBootstrapper(IServiceProvider services, IConfiguration configuration, Iri baseUri, ILogger<AdminBootstrapper> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _configuration = configuration;
        _baseUri = baseUri;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var username = _configuration[Section + ":Username"];
        var password = _configuration[Section + ":Password"];

        // No bootstrap configured — nothing to do (the operator sets the first admin by hand, or a
        // single-user deployment simply has no admin).
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return;
        }

        // Idempotent: never overwrite an existing admin.
        var accounts = _services.GetRequiredService<IUserAccountStore>();
        if (await accounts.AnyAdminExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var provisioner = _services.GetRequiredService<ActorProvisioner>();
        var hasher = _services.GetRequiredService<PasswordHasher>();

        Iri actorIri;
        try
        {
            actorIri = await provisioner.ProvisionAsync(username, ct: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin actor provisioning failed for '{Username}'.", username);
            return;
        }

        try
        {
            await accounts.CreateAsync(new UserAccount
            {
                Username = username,
                PasswordHash = hasher.Hash(password),
                Role = UserRole.Admin,
                ActorId = actorIri,
                CreatedAt = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // A concurrent startup created the account first — not an error.
            return;
        }

        _logger.LogInformation("Bootstrapped admin account '{Username}' (actor {ActorIri}).", username, actorIri.Value);
    }
}
