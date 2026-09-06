using Microsoft.EntityFrameworkCore;

namespace Iris.Server.Data;

/// <summary>
/// The <see cref="IDbContextFactory{IrisDbContext}"/> for the production EF Core provider: creates a
/// short-lived <see cref="IrisDbContext"/> over the instance's PostgreSQL connection. The stores open a
/// context per operation (a short read/write), so a factory (not a scoped context) is the right seam.
/// </summary>
public sealed class IrisDbContextFactory : IDbContextFactory<IrisDbContext>
{
    private readonly DbContextOptions<IrisDbContext> _options;

    /// <summary>
    /// Initializes the factory over a Npgsql connection string.
    /// </summary>
    /// <param name="connectionString">The PostgreSQL connection string. Must not be null or empty.</param>
    public IrisDbContextFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A connection string is required.", nameof(connectionString));
        }

        _options = new DbContextOptionsBuilder<IrisDbContext>().UseNpgsql(connectionString).Options;
    }

    /// <inheritdoc/>
    public IrisDbContext CreateDbContext() => new(_options);
}
