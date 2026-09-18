namespace Iris.Client;

/// <summary>
/// The viewer's vote state on a Lemmy post. Mirrors Lemmy's <c>score</c> parameter on the
/// <c>POST /api/v3/post/like</c> endpoint: <c>1</c> = upvoted, <c>-1</c> = downvoted,
/// <c>0</c> = no vote (or vote removed).
/// </summary>
public enum LemmyVoteState
{
    /// <summary>No vote cast (or the vote was removed).</summary>
    None = 0,
    /// <summary>The viewer upvoted the post.</summary>
    Up = 1,
    /// <summary>The viewer downvoted the post.</summary>
    Down = -1,
}

/// <summary>
/// A minimal projection of a Lemmy post's score data, fetched from the Lemmy REST API
/// (<c>GET {instance}/api/v3/post?id={id}</c>). The ActivityPub document for a Lemmy post
/// (a <c>Page</c> object) does not carry vote/score information; it is only available via
/// Lemmy's own REST API. The client uses this to display the score in a Lemmy-style
/// voting layout.
/// </summary>
/// <param name="Score">The net score (upvotes − downvotes).</param>
/// <param name="Upvotes">The upvote count.</param>
/// <param name="Downvotes">The downvote count.</param>
/// <param name="Comments">The comment count.</param>
/// <param name="ViewerVote">The viewer's vote state on this post.</param>
public sealed record LemmyPostScore(
    int Score,
    int Upvotes,
    int Downvotes,
    int Comments,
    LemmyVoteState ViewerVote = LemmyVoteState.None)
{
    /// <summary>
    /// Parses a Lemmy post-view document from JSON, or <see langword="null"/> when the body
    /// is empty, not valid Lemmy JSON, or does not contain a <c>post_view</c> object.
    /// </summary>
    /// <param name="json">The raw Lemmy post-view document.</param>
    /// <param name="viewerVote">The viewer's vote state (default: <see cref="LemmyVoteState.None"/>).</param>
    public static LemmyPostScore? FromJson(string? json, LemmyVoteState viewerVote = LemmyVoteState.None)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("post_view", out var pv) ||
                pv.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            if (!pv.TryGetProperty("counts", out var counts) ||
                counts.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            int GetInt(string key) =>
                counts.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? v.GetInt32()
                    : 0;

            return new LemmyPostScore(
                GetInt("score"),
                GetInt("upvotes"),
                GetInt("downvotes"),
                GetInt("comments"),
                viewerVote);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the Lemmy instance base URI from a post's ActivityPub IRI.
    /// A Lemmy post IRI has the form <c>{instance}/post/{id}</c>; this returns <c>{instance}</c>.
    /// Returns <see langword="null"/> when the IRI is not a recognizable Lemmy post IRI.
    /// </summary>
    /// <param name="postIri">The post's ActivityPub IRI (e.g. <c>https://lemmy.example/post/1</c>).</param>
    public static (Uri Instance, int PostId)? TryParsePostIri(Iri postIri)
    {
        var uri = postIri.Uri;
        var segments = uri.AbsolutePath.Trim('/').Split('/');

        // Lemmy post IRIs: /post/{id} (local) or /post/{id} on a remote instance.
        if (segments.Length == 2 && segments[0] == "post" && int.TryParse(segments[1], out var id))
        {
            var instance = new UriBuilder(uri)
            {
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            }.Uri;

            return (instance, id);
        }

        return null;
    }
}
