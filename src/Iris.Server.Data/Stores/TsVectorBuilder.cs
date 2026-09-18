using System.Text;
using System.Text.Json;

namespace Iris.Server.Data.Stores;

/// <summary>
/// Builds PostgreSQL <c>tsvector</c> strings from ActivityStreams JSON documents. The tsvector is
/// stored as a raw string (the <c>tsvector</c> column type) and is indexed with a GIN index for
/// ranked full-text search.
/// </summary>
/// <remarks>
/// The tsvector format is <c>lexeme:position</c> pairs separated by spaces (e.g.
/// <c>hello:1A world:1B</c>). Positions use a letter suffix (A, B, …) for the column/field
/// within the document. We use the <c>simple</c> text search configuration (no stemming, no
/// stopword removal) so that short handles and names match literally — the same behavior the
/// previous <c>ILIKE</c> substring search provided, but with index support and ranking.
/// </remarks>
internal static class TsVectorBuilder
{
    /// <summary>
    /// Builds a tsvector string from an object document's <c>content</c> and <c>name</c> fields.
    /// </summary>
    /// <param name="document">The object's JSON document string.</param>
    /// <returns>A tsvector string, or <c>null</c> when the document has no searchable text.</returns>
    public static string? BuildForObject(string document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(document);
        var root = doc.RootElement;

        var sb = new StringBuilder();
        int position = 0;

        // Content (field A). May be a single string or an array of strings.
        if (root.TryGetProperty("content", out var content))
        {
            AppendText(sb, content, "A", ref position);
        }

        // Name (field B). May be a single string or an array of strings.
        if (root.TryGetProperty("name", out var name))
        {
            AppendText(sb, name, "B", ref position);
        }

        // Summary (field C) — used by Article objects.
        if (root.TryGetProperty("summary", out var summary))
        {
            AppendText(sb, summary, "C", ref position);
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>
    /// Builds a tsvector string from an actor document's <c>name</c>, <c>preferredUsername</c>,
    /// and <c>summary</c> fields.
    /// </summary>
    /// <param name="document">The actor's JSON document string.</param>
    /// <returns>A tsvector string, or <c>null</c> when the document has no searchable text.</returns>
    public static string? BuildForActor(string document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(document);
        var root = doc.RootElement;

        var sb = new StringBuilder();
        int position = 0;

        // Name (field A). May be a single string or an array of strings.
        if (root.TryGetProperty("name", out var name))
        {
            AppendText(sb, name, "A", ref position);
        }

        // PreferredUsername (field B) — the handle, e.g. "alice".
        if (root.TryGetProperty("preferredUsername", out var preferred))
        {
            if (preferred.ValueKind == JsonValueKind.String)
            {
                AppendWords(sb, preferred.GetString()!, "B", ref position);
            }
        }

        // Summary (field C) — the actor's bio/description.
        if (root.TryGetProperty("summary", out var summary))
        {
            AppendText(sb, summary, "C", ref position);
        }

        // Handle (field D) — the relational column, also searchable.
        if (root.TryGetProperty("handle", out var handle))
        {
            if (handle.ValueKind == JsonValueKind.String)
            {
                AppendWords(sb, handle.GetString()!, "D", ref position);
            }
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>
    /// Appends words from a JSON value (string or array of strings) to the tsvector builder.
    /// </summary>
    private static void AppendText(StringBuilder sb, JsonElement element, string field, ref int position)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            AppendWords(sb, element.GetString()!, field, ref position);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    AppendWords(sb, item.GetString()!, field, ref position);
                }
            }
        }
    }

    /// <summary>
    /// Tokenizes a text string into words and appends them to the tsvector builder.
    /// </summary>
    private static void AppendWords(StringBuilder sb, string text, string field, ref int position)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Tokenize: split on non-alphanumeric characters. Each token is a lexeme.
        // Lowercase to match the 'simple' text search configuration (case-insensitive).
        int i = 0;
        while (i < text.Length)
        {
            // Skip non-alphanumeric characters.
            while (i < text.Length && !char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            // Collect the word.
            int start = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            var word = text[start..i].ToLowerInvariant();
            if (word.Length > 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(word).Append(':').Append(position).Append(field);
                position++;
            }
        }
    }
}
