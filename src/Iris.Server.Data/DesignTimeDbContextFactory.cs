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
        => new(new DbContextOptionsBuilder<IrisDbContext>()
            .UseNpgsql("Host=localhost;Database=iris;Username=iris;Password=iris")
            .Options);
}
