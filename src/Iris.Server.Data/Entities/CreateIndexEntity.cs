namespace Iris.Server.Data.Entities;

/// <summary>
/// The object → Create index (decision 055): records the <c>Create</c> that produced each content
/// object, so a later <c>Delete</c> can find the originating <c>Create</c> by lookup instead of by
/// deriving it from the (independent-ULID) object IRI.
/// </summary>
public sealed class CreateIndexEntity
{
    /// <summary>
    /// The created object's IRI (primary key).
    /// </summary>
    public string ObjectId { get; set; } = string.Empty;

    /// <summary>
    /// The <c>Create</c> activity's IRI.
    /// </summary>
    public string CreateActivityId { get; set; } = string.Empty;
}
