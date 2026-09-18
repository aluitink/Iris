using KristofferStrube.ActivityStreams;
using Xunit;

namespace Iris.Server.Data.Tests;

/// <summary>
/// The Phase 32 step-2 durability requirement: data written by the EF Core provider must survive a
/// "restart" (a fresh <see cref="IPersistenceProvider"/> over the same database + blob directory).
/// This is the production guarantee that restarting the <c>Iris.Web</c> container (without wiping
/// volumes) preserves actors, activities, follows, keys, and media.
/// </summary>
public sealed class EfDurabilityTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public EfDurabilityTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static Link Link(string iri) => new() { Href = new Uri(iri) };

    private static (IPersistenceProvider Provider, string BlobDir) NewProvider(string connectionString, string? blobDir = null)
    {
        blobDir ??= Path.Combine(Path.GetTempPath(), "iris-durability", Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Iris:ConnectionString"] = connectionString,
                ["Iris:MediaBlobDir"] = blobDir,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddEntityFrameworkPersistence(config);
        return (services.BuildServiceProvider().GetRequiredService<IPersistenceProvider>(), blobDir);
    }

    [Fact]
    public async Task Data_SurvivesRestart()
    {
        // --- "First boot": write data through a provider instance. ---
        var (first, blobDir) = NewProvider(_fixture.ConnectionString);
        var actorIri = new Iri($"https://test.local/ap/v1/u/dur-{Guid.NewGuid():N}");
        var note = new Note { Id = $"https://test.local/ap/v1/objects/dn-{Guid.NewGuid():N}", Content = ["durable"] };
        var create = new Create
        {
            Id = $"https://test.local/ap/v1/activities/dc-{Guid.NewGuid():N}",
            Actor = [Link(actorIri.Value)],
            Object = [note],
        };

        await first.Actors.PutActorAsync(new Person { Id = actorIri.Value, PreferredUsername = "dur" });
        await first.Activities.PutActivityAsync(create);
        await first.Activities.AddToOutboxAsync(actorIri, create);
        await first.Follows.RecordFollowAsync(actorIri, new Iri($"https://test.local/ap/v1/u/dt-{Guid.NewGuid():N}"));

        var keyIri = new Iri($"{actorIri}#key-1");
        var key = KeyPairGenerator.GenerateRsa(keyIri);
        first.Keys.PutKey(key);
        var mediaIri = await first.Media.PutAsync(new byte[] { 1, 2, 3, 4 }, "application/octet-stream", "bin.dat", new Iri("https://test.local"));

        // --- "Restart": a brand-new provider over the SAME database + blob directory. ---
        var (second, _) = NewProvider(_fixture.ConnectionString, blobDir);

        Assert.True(await second.Actors.TryGetActorAsync(actorIri, out var actor));
        Assert.Equal("dur", actor!.PreferredUsername);

        Assert.True(await second.Activities.TryGetActivityAsync(new Iri(create.Id), out var activity));
        Assert.Equal("Create", activity!.Type?.FirstOrDefault());

        var outbox = await second.Activities.GetOutboxAsync(actorIri);
        Assert.Single(outbox);

        Assert.True(await second.Follows.GetFollowingAsync(actorIri) is var following && following.Count == 1);

        Assert.True(second.Keys.TryGetKey(keyIri, out var restoredKey));
        Assert.NotNull(restoredKey);

        Assert.True(await second.Media.TryGetAsync(mediaIri, out var mediaBytes, out _, out _));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, mediaBytes);
    }
}
