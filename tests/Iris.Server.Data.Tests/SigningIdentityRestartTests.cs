using System.Text;
using Iris.Client.Auth;
using Iris.Core.Signing;
using Iris.Server.Data.Accounts;
using Iris.Web;
using Iris.Web.Accounts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iris.Server.Data.Tests;

/// <summary>
/// The Phase 33 slice 33.2 guarantee, exercised at the <em>composition-root</em> level (not just the
/// store level): a local actor's signing identity survives a server restart under EF Core + PostgreSQL.
/// Before 33.2 the app unconditionally bound an in-memory <see cref="IKeyStore"/> even under EF, so the
/// seed minted a fresh key every boot and a registered user's key was lost entirely. After 33.2 the
/// <see cref="IKeyStore"/> is the durable <c>EfKeyStore</c>, the seed reuses a persisted key, and a
/// startup pass re-registers every local account's key with the in-process key provider.
/// </summary>
/// <remarks>
/// This test is intentionally UI-free: it builds the service container through the real
/// <see cref="WebAppFactory.ConfigureServices"/> + <see cref="WebAppFactory.InitializePersistence"/>
/// (the same code the production host runs at startup) against a Testcontainers Postgres, and a
/// "restart" is a brand-new container over the same database. No Blazor host, no pipeline, no TestServer.
/// </remarks>
public sealed class SigningIdentityRestartTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public SigningIdentityRestartTests(PostgresFixture fixture)
        => _fixture = fixture;

    private sealed record Boot(IServiceProvider Services, IKeyStore Keys, IPersistenceProvider Persistence,
        IUserAccountStore Accounts, IKeyProvider KeyProvider, Iri BaseUri);

    /// <summary>
    /// Boots the real composition root's service graph + runs its startup persistence pass (migrate →
    /// seed → register seed key → admin bootstrap → restore local signing keys) against the given
    /// database, with a distinct advertised base per boot. Returns the resolved DI seam the test asserts
    /// on. No web host / pipeline is built (this is a DI-level test, not a UI test).
    /// </summary>
    private static Boot BootApp(string connectionString, string? advertisedBase = null)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Iris:ConnectionString"] = connectionString,
            ["Iris:MediaBlobDir"] = Path.Combine(Path.GetTempPath(), "iris-332", Guid.NewGuid().ToString("N")),
        });
        // A distinct advertised base per boot (the seed actor IRI derives from it). The returned Boot
        // carries that base so the test can recompute the seed actor's key IRI the same way
        // WebAppFactory does ({base}/ap/v1/u/{SeedHandle}#key-1). A restart passes the SAME base as the
        // first boot (production fixes Iris:AdvertiseBase), so the actor + key IRIs are identical.
        var baseUri = advertisedBase ?? $"http://iris-{Guid.NewGuid():N}.local";
        WebAppFactory.ConfigureServices(builder, baseUri);
        var services = builder.Services.BuildServiceProvider();
        // The production startup persistence pass (the part that does the migrate + seed + key
        // registration) — called directly, exactly as BuildApp does, without the web pipeline.
        WebAppFactory.InitializePersistence(services, builder.Configuration, baseUri);
        return new Boot(
            services,
            services.GetRequiredService<IKeyStore>(),
            services.GetRequiredService<IPersistenceProvider>(),
            services.GetRequiredService<IUserAccountStore>(),
            services.GetRequiredService<IKeyProvider>(),
            new Iri(baseUri));
    }

    [Fact]
    public async Task InstanceActorSigningIdentity_SurvivesRestart()
    {
        // --- First boot: the seeded instance actor mints + persists its key. ---
        var first = BootApp(_fixture.ConnectionString);
        // The seed actor's key IRI is derived exactly as WebAppFactory does: {base}/ap/v1/u/{SeedHandle}#key-1.
        var actorIri = new Iri($"{first.BaseUri.Value.TrimEnd('/')}/ap/v1/u/{WebAppFactory.SeedHandle}");
        var keyIri = new Iri($"{actorIri}#key-1");
        Assert.True(first.Keys.TryGetKey(keyIri, out var firstKey));
        var firstPubPem = firstKey!.ExportPublicKeyPem();
        // The instance actor is registered + resolvable (the readiness-gate condition).
        Assert.True(first.KeyProvider.TryGetIdentity(actorIri, out _));

        // --- Restart: a brand-new host over the SAME database (same advertised base, as in production
        //     where Iris:AdvertiseBase is fixed). A restart is the same host coming back up, so the
        //     seed actor + key IRI are identical across boots; what must survive is the *key material*. ---
        var second = BootApp(_fixture.ConnectionString, first.BaseUri.Value);

        // (a) The persisted private key is read back into the durable key store and can sign.
        Assert.True(second.Keys.TryGetKey(keyIri, out var restoredKey));
        Assert.NotNull(restoredKey!);
        var payload = Encoding.UTF8.GetBytes("restart-332");
        var signature = restoredKey.Sign(payload);
        Assert.True(restoredKey.Verify(payload, signature));

        // (b) The SAME key is reused: the public key is unchanged across the restart (a fresh key would
        //     have a different public key — this is the 33.2 regression the test guards against).
        Assert.Equal(firstPubPem, restoredKey!.ExportPublicKeyPem());

        // (c) The instance actor is re-registered with the (fresh) in-process key provider.
        Assert.True(second.KeyProvider.TryGetIdentity(actorIri, out var identity));
        Assert.Equal(keyIri, identity!.KeyId);

        // (d) An outbound signature produced via the app's signer verifies (the key round-trips).
        var metadata = new HttpRequestMetadata(
            "POST", "/ap/v1/u/remote/inbox", "iris.local", "Thu, 01 Jan 2026 00:00:00 GMT",
            "application/activity+json", new byte[] { 1, 2, 3 }, new Dictionary<string, string>());
        var signatureHeader = new HttpSignatureSigner(second.Keys)
            .Sign(metadata, new SystemIdentity(actorIri, keyIri), SigningProfile.ServerToServer);
        Assert.Contains(keyIri.Value, signatureHeader);
    }

    [Fact]
    public async Task UserActorSigningIdentity_SurvivesRestart()
    {
        // --- First boot: register a local account (provisions a Person + an RSA key). ---
        var first = BootApp(_fixture.ConnectionString);
        var options = first.Services.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value;
        // WebAppFactory sets BaseUri to the advertised base, so it is present here.
        Iri baseUri = options.BaseUri ?? new Iri("http://localhost");
        var provisioner = new ActorProvisioner(first.Persistence, first.Keys, first.KeyProvider, baseUri);
        var userActor = await provisioner.ProvisionAsync("bob", "Bob");
        var userKey = new Iri($"{userActor}#key-1");
        Assert.True(first.Keys.TryGetKey(userKey, out var firstUserKey));
        var firstUserPub = firstUserKey!.ExportPublicKeyPem();
        await first.Accounts.CreateAsync(new UserAccount
        {
            Username = "bob",
            PasswordHash = "x",
            Role = UserRole.User,
            ActorId = userActor,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        Assert.True(first.KeyProvider.TryGetIdentity(userActor, out _));

        // --- Restart: a brand-new host over the SAME database. ---
        var second = BootApp(_fixture.ConnectionString);

        // The user actor's key is persisted and read back (the durable key store).
        Assert.True(second.Keys.TryGetKey(userKey, out var restoredUserKey));
        Assert.NotNull(restoredUserKey!);
        Assert.Equal(firstUserPub, restoredUserKey!.ExportPublicKeyPem());

        // The user actor is re-registered with the (fresh) in-process key provider — the 33.2 gap was
        // that a registered user's key was lost entirely on restart (not in the in-memory store, not
        // in the key provider). Now it is signable again.
        Assert.True(second.KeyProvider.TryGetIdentity(userActor, out var identity));
        Assert.Equal(userKey, identity!.KeyId);
    }
}
