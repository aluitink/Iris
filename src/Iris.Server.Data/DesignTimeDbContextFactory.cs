using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iris.Server.Data;

/// <summary>
/// A design-time <see cref="IDesignTimeDbContextFactory{IrisDbContext}"/> so <c>dotnet ef migrations</c>
/// can run with <c>Iris.Server.Data</c> as its own startup project (the tooling needs an entry point + a
/// way to build the context without a running host). The connection string here is a throwaway design
/// value — migrations are database-agnostic for this provider (Npgsql) and the real connection comes
/// from the host's configuration at runtime.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<IrisDbContext>
{
    /// <inheritdoc/>
    public IrisDbContext CreateDbContext(string[] args)
    {
        // 50.1 security hardening: read the connection string from the environment (IRIS_DB_CONNECTION)
        // instead of hardcoding credentials. Falls back to a trivial localhost value for CI/dev.
        var conn = Environment.GetEnvironmentVariable("IRIS_DB_CONNECTION")
            ?? "Host=localhost;Database=iris;Username=iris;Password=iris";
        return new(new DbContextOptionsBuilder<IrisDbContext>()
            .UseNpgsql(conn)
            .Options);
    }
}
