using Testcontainers.PostgreSql;


namespace Iris.Server.Data.Tests;

/// <summary>
/// A shared PostgreSQL container for the <see cref="Iris.Server.Data.Tests"/> class (one container
/// for the whole test run). The container's database is migrated once in the constructor; each test
/// then uses a distinct IRI namespace to avoid interference. <see cref="BlobRoot"/> is a temp
/// directory for the media store's blob bytes (the media store writes raw bytes to disk and metadata
/// to Postgres).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    /// <summary>The connection string to the running container.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>A temp root directory for the media store's blob files.</summary>
    public string BlobRoot { get; } = Path.Combine(Path.GetTempPath(), "iris-data-tests", Guid.NewGuid().ToString("N"));

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(BlobRoot);
        var builder = new PostgreSqlBuilder("postgres:16-alpine")
            .WithUsername("iris")
            .WithPassword("iris")
            .WithDatabase("iris");
        _container = builder.Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        // Run the EF migrations so the schema exists before any test.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Iris:ConnectionString"] = ConnectionString })
            .Build();
        var services = new ServiceCollection();
        services.AddEntityFrameworkPersistence(config);
        using var provider = services.BuildServiceProvider();
        var persistence = provider.GetRequiredService<Iris.Server.Stores.IPersistenceProvider>();
        await persistence.EnsureCreatedAsync(config);
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
        try
        {
            if (Directory.Exists(BlobRoot))
            {
                Directory.Delete(BlobRoot, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
