using System.Text.Json;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Core.Compose;

/// <summary>
/// Builds an authored <see cref="Note"/> from raw authoring inputs (22.3 US-11: a note with optional
/// Markdown content and a content-sensitivity flag + summary). This is the client-side composition of a
/// note's wire shape (content, attribution, sensitivity, and optional audience) before it is published
/// through the signed outbox pipeline by <c>IActivityPubClient</c>.
/// </summary>
/// <remarks>
/// <strong>Markdown:</strong> when the <c>markdownHtml</c> argument is supplied (a non-empty, already
/// rendered HTML string), it is used verbatim as the note's <c>content</c> — the authoring surface is
/// responsible for rendering the Markdown to safe HTML (e.g. the sample's dependency-free
/// <c>Markdown.ToHtml</c>); otherwise the raw <c>content</c> is used (posted as the user typed it).
/// The helper does not perform any Markdown rendering or HTML sanitization itself.
/// </remarks>
public static class ComposeNote
{
    /// <summary>
    /// Builds a <see cref="Note"/> from raw authoring inputs.
    /// </summary>
    /// <param name="actorId">The IRI of the authoring actor (set as the note's <c>attributedTo</c>; the
    /// authoring surface posts it as this actor, so the request is signed as that actor).</param>
    /// <param name="content">The note's content (plain text / HTML, as typed).</param>
    /// <param name="markdownHtml">
    /// When non-empty (and not just whitespace), the Markdown content already rendered to HTML — used
    /// verbatim as the note's <c>content</c> in place of <paramref name="content"/>. Null or
    /// whitespace to post the raw <paramref name="content"/>.
    /// </param>
    /// <param name="sensitive">
    /// Whether to mark the note content-sensitive (the ActivityStreams <c>sensitive</c> term, carried in
    /// <c>ExtensionData</c> since the library does not model it as a property — the same representation
    /// <see cref="IriExtensions.IsSensitive"/> reads).
    /// </param>
    /// <param name="summary">
    /// The sensitivity summary (the ActivityStreams <c>summary</c> term — the text a client shows in
    /// place of a sensitive note's content until the viewer reveals it). Ignored (not set) when
    /// <paramref name="sensitive"/> is <c>false</c> or the value is null/whitespace.
    /// </param>
    /// <param name="to">
    /// The optional audience link(s) (e.g. the public <c>as:Public</c> address). When null or empty the
    /// note carries no explicit <c>to</c>.
    /// </param>
    /// <returns>
    /// The composed <see cref="Note"/> (type <c>Note</c>, set by the constructor), ready to be published
    /// through the signed pipeline.
    /// </returns>
    /// <exception cref="ArgumentNullException">When <paramref name="content"/> is null.</exception>
    public static Note Build(
        Iri actorId,
        string content,
        string? markdownHtml = null,
        bool sensitive = false,
        string? summary = null,
        IEnumerable<Iri>? to = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        // When Markdown content was rendered to HTML, use it verbatim; otherwise the raw content.
        var noteContent = string.IsNullOrWhiteSpace(markdownHtml) ? content : markdownHtml;

        var note = new Note
        {
            Content = [noteContent],
            AttributedTo = [new Link { Href = actorId.Uri }],
        };

        if (sensitive)
        {
            // `sensitive` is a standard AS term the library leaves in ExtensionData (Rule 6) — set it
            // the same way the seeded/round-trip fixtures do, and the same way IsSensitive reads it.
            // (ExtensionData is null on a freshly-constructed note, so it must be initialized first.)
            note.ExtensionData ??= new Dictionary<string, JsonElement>();
            note.ExtensionData["sensitive"] = JsonSerializer.SerializeToElement(true);
        }

        if (sensitive && !string.IsNullOrWhiteSpace(summary))
        {
            // `summary` is a real AS property (the content-sensitivity preview).
            note.Summary = [summary];
        }

        if (to is not null)
        {
            var audience = to
                .Where(i => i != default)
                .Select(i => new Link { Href = i.Uri })
                .ToList();
            if (audience.Count > 0)
            {
                note.To = audience;
            }
        }

        return note;
    }
}
