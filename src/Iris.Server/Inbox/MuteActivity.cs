using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// An Iris-specific <c>Mute</c> activity: the directed moderation decision <c>muter → muted</c> that
/// silences the muted actor's content for the muter (the inverse of which is an <see cref="Undo"/> of
/// this activity).
/// </summary>
/// <remarks>
/// <strong>Why a wrapper, not a library type.</strong> Unlike <see cref="Block"/> and <see cref="Flag"/>,
/// <c>Mute</c> is <em>not</em> an ActivityStreams 2.0 type, so the ActivityStreams library has no
/// <c>Mute</c> class: an inbound <c>"type": "Mute"</c> deserializes to a generic
/// <see cref="KristofferStrube.ActivityStreams.Object"/> (not an
/// <see cref="KristofferStrube.ActivityStreams.Activity"/>). Per the coding-style rule against
/// re-declaring library types, this is an Iris-specific <em>wrapper</em> (Rule 6): a thin
/// <see cref="KristofferStrube.ActivityStreams.Activity"/> subclass that pins <c>Type</c> to
/// <c>["Mute"]</c> and carries the <c>actor</c> (the muter) and <c>object</c> (the muted actor) through
/// the inherited properties — it does not add new properties. The server
/// <see cref="MuteActivityHandler"/> records the edge from this type, the inbox endpoint wraps an
/// inbound generic <c>Object</c> whose <c>type</c> is <c>Mute</c> into this type, and the outbox-publish
/// path recognizes a local actor's <c>Mute</c> so it federates to the muted actor's inbox (24.2).
/// </remarks>
public sealed class MuteActivity : Activity
{
    /// <summary>
    /// The <c>type</c> term for an Iris <c>Mute</c> activity, as it appears in the wire
    /// <c>"type"</c> property.
    /// </summary>
    public const string MuteType = "Mute";

    /// <summary>
    /// Initializes a new <see cref="MuteActivity"/>. The constructor pins <c>Type</c> to
    /// <c>["Mute"]</c> so the wire form round-trips as <c>"type": "Mute"</c> (mirroring how the library's
    /// concrete activity constructors auto-set <c>Type</c>).
    /// </summary>
    public MuteActivity()
    {
        Type = [MuteType];
    }
}
