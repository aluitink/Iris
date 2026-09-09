using System.Text.Json;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

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
    /// <param name="mediaIri">
    /// The same-origin media IRI (the <c>/ap/v1/media/{id}</c> path minted on upload) of the note's image
    /// attachment (Phase 20.4 (a) / F-27), when one was uploaded. When set (non-null) the note carries a
    /// single <see cref="Image"/> <c>attachment</c> whose <c>url</c> (and mirroring <c>id</c>) is this IRI,
    /// with <c>mediaType</c> and <c>name</c> from <paramref name="mediaType"/> and
    /// <paramref name="mediaName"/>. When null the note carries no <c>attachment</c>.
    /// </param>
    /// <param name="mediaType">
    /// The attachment image's <c>mediaType</c> (the file's content type, e.g. <c>image/png</c>), as
    /// returned by the media upload. Used only when <paramref name="mediaIri"/> is set.
    /// </param>
    /// <param name="mediaName">
    /// The attachment image's <c>name</c> (the file's original name), as returned by the media upload.
    /// Used only when <paramref name="mediaIri"/> is set.
    /// </param>
    /// <param name="mentions">
    /// The IRIs of actors mentioned in the note (the <c>@handle</c> convention). When non-empty, each
    /// becomes a <see cref="Mention"/> <c>tag</c> entry whose <c>href</c> is the actor IRI (the
    /// ActivityPub @mention convention). When null or empty the note carries no mention <c>tag</c>.
    /// </param>
    /// <param name="hashtags">
    /// The hashtag names in the note (the <c>#hashtag</c> convention; each value should include the
    /// leading <c>#</c>, e.g. <c>"#hello"</c>). When non-empty, each becomes a <c>Hashtag</c>
    /// <c>tag</c> entry (an ActivityStreams object of type <c>Hashtag</c> whose <c>name</c> is the
    /// <c>#tag</c> text and whose <c>href</c> is the hashtag's browse URL, per
    /// <paramref name="hashtagHrefFactory"/>). When null or empty the note carries no hashtag
    /// <c>tag</c>. Mention and hashtag tags are combined into a single <c>tag</c> array.
    /// </param>
    /// <param name="hashtagHrefFactory">
    /// An optional factory that maps a hashtag name (e.g. <c>"#hello"</c>) to its browse/search URL
    /// (the <c>href</c> of the <c>Hashtag</c> tag). When null, the <c>Hashtag</c> tags carry no
    /// <c>href</c> (the name alone still identifies the hashtag per the AP convention). Supplying a
    /// factory lets a client point hashtag tags at its own hashtag page (e.g.
    /// <c>{origin}/search?q=%23hello</c>).
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
        IEnumerable<Iri>? to = null,
        Iri? mediaIri = null,
        string? mediaType = null,
        string? mediaName = null,
        IEnumerable<Iri>? mentions = null,
        IEnumerable<string>? hashtags = null,
        Func<string, string?>? hashtagHrefFactory = null)
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

        if (mediaIri is { } media)
        {
            // A note's image attachment (Phase 20.4 (a) / F-27): a single Image whose url is the
            // same-origin media IRI minted on upload (and whose id mirrors it, so a reader can resolve
            // the media without the url). mediaType + name come from the upload (Decision 056 (b): the
            // url is same-origin, never a cross-origin media host). The feed's ObjectView renders it via
            // GetMediaAttachments.
            var image = new Image
            {
                Url = [new Link { Href = media.Uri }],
                Id = media.Value,
                MediaType = mediaType,
            };
            // A blank (null/whitespace) file name yields no name entry; the Image.Name property is left
            // at its default. (A `{ Length: > 0 }` pattern alone would not catch a whitespace-only name.)
            if (mediaName is { } rawName && !string.IsNullOrWhiteSpace(rawName))
            {
                image.Name = [rawName];
            }
            note.Attachment = [image];
        }

        // Combine mention and hashtag tags into a single `tag` array (the AP convention: a note's
        // `tag` carries both Mention and Hashtag objects). Mentions are built first (their relative
        // order is preserved), then hashtags.
        var tags = new List<IObjectOrLink>();

        if (mentions is not null)
        {
            foreach (var mentionIri in mentions.Where(i => i != default))
            {
                tags.Add(new Mention { Href = mentionIri.Uri });
            }
        }

        if (hashtags is not null)
        {
            foreach (var rawName in hashtags)
            {
                if (string.IsNullOrWhiteSpace(rawName))
                {
                    continue;
                }

                // A Hashtag tag is an ActivityStreams object of type Hashtag whose name is the #tag text.
                // The library has no concrete Hashtag class, so it is built as a generic Object with the
                // type set explicitly (the constructor leaves Type unset for the base Object) and the
                // optional href in ExtensionData (the Object base does not model href as a property —
                // only Link does), exactly the shape GetHashtagTags reads back.
                var hashtag = new ActivityObject { Type = ["Hashtag"], Name = [rawName] };
                if (hashtagHrefFactory is { } factory
                    && factory(rawName) is { Length: > 0 } href
                    && Iri.TryParse(href, out var parsedHref))
                {
                    hashtag.ExtensionData ??= new Dictionary<string, JsonElement>();
                    hashtag.ExtensionData["href"] = JsonSerializer.SerializeToElement(href);
                }

                tags.Add(hashtag);
            }
        }

        if (tags.Count > 0)
        {
            note.Tag = tags;
        }

        return note;
    }
}
