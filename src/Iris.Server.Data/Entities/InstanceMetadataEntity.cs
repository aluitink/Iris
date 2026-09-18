namespace Iris.Server.Data.Entities;

/// <summary>
/// The instance metadata row (a single row, key = 0). Holds the admin-editable NodeInfo fields
/// (51.3).
/// </summary>
public sealed class InstanceMetadataEntity
{
    /// <summary>
    /// The row key (always 0 — there is exactly one instance per server).
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The instance display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The instance description (nullable).
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// When the metadata was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
