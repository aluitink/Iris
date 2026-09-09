using Iris.Server;
using Iris.Server.Data.Accounts;

namespace Iris.Server.Data;

/// <summary>
/// EF Core implementation of <see cref="IInstanceStatsProvider"/> that returns the real
/// local user account count from the <see cref="IUserAccountStore"/>.
/// </summary>
public sealed class EfInstanceStatsProvider(IUserAccountStore accounts) : IInstanceStatsProvider
{
    private readonly IUserAccountStore _accounts = accounts;

    /// <inheritdoc/>
    public async Task<int> GetLocalUserCountAsync(CancellationToken ct = default)
    {
        var all = await _accounts.GetAllAsync(ct).ConfigureAwait(false);
        return all.Count;
    }
}
