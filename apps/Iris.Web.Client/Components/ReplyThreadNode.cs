using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Web.Client.Components;

/// <summary>
/// A node in the object-detail reply thread (Phase 132.3): one reply (its resolved document + IRI)
/// and its direct replies-to-replies (the nested children). The thread is a tree rooted at the object's
/// direct replies; each node's <see cref="Children"/> are the replies to that node.
/// </summary>
/// <param name="Iri">The reply's IRI (the node's identity key and the card's link target).</param>
/// <param name="Object">The resolved reply document (the card's content); null when unresolvable.</param>
/// <param name="Children">The direct replies to this node (may be empty when the node has no replies or
/// its replies were not walked — e.g. at the bounded walk depth).</param>
/// <remarks>
/// <see cref="ChildCount"/> is the total number of replies in this node's subtree (this node's own
/// children plus their subtrees), used for the collapsed "N replies" affordance.
/// </remarks>
public sealed record ReplyThreadNode(Iri Iri, IObject? Object, List<ReplyThreadNode> Children)
{
    /// <summary>
    /// The total number of replies in this node's subtree (its own children plus their subtrees).
    /// </summary>
    public int ChildCount
    {
        get
        {
            var count = 0;
            foreach (var child in Children)
            {
                count += 1 + child.ChildCount;
            }

            return count;
        }
    }

    /// <summary>
    /// Whether the node has at least one reply in its subtree (the "N replies" affordance is shown).
    /// </summary>
    public bool HasReplies => ChildCount > 0;
}
