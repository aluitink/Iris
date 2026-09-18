namespace Iris.Server.Data.Accounts;

/// <summary>
/// Persists the instance-level metadata (name and description) that an admin can edit at runtime
/// (51.3). The values override the static <c>ActivityPubServerOptions.InstanceName</c> when serving
/// the NodeInfo document.
/// </summary>
/// <remarks>
/// Declared and implemented in <c>Iris.Server.Data</c> (same pattern as <see cref="IUserAccountStore"/>):
/// it is a local concern with no federation surface, so it does not belong in <c>Iris.Server.Stores</c>.
/// </remarks>
public interface IInstanceMetadataStore
{
    /// <summary>
    /// Returns the current instance metadata, or <see langword="null"/> when the admin has not yet
    /// customized it (the NodeInfo handler falls back to the static options).
    /// </summary>
    Task<InstanceMetadata?> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Replaces the instance metadata (upsert: the row is created or overwritten).
    /// </summary>
    Task UpdateAsync(InstanceMetadata metadata, CancellationToken ct = default);
}

/// <summary>
/// The instance's human-readable metadata (the <c>metadata.name</c> and <c>metadata.description</c>
/// fields of the NodeInfo document).
/// </summary>
/// <param name="Name">The instance display name (e.g. "Iris on luit.ink").</param>
/// <param name="Description">The instance description (a one-liner shown on the instance page).</param>
/// <param name="UpdatedAt">When the metadata was last edited.</param>
public sealed record InstanceMetadata(
    string Name,
    string? Description,
    DateTimeOffset UpdatedAt);
