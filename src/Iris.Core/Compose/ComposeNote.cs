using System.Text.Json;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Core.Compose;

/// <summary>
/// A single uploaded media file to attach to a composed note (the same-origin media IRI minted on
/// upload, its content-type, and its original file name). An <c>image/*</c> upload becomes an
/// <see cref="Image"/> attachment; any other content type (video, audio, document, …) becomes a
/// <see cref="Document"/> attachment (73.2 — multiple, mixed media per post).
/// </summary>
/// <param name="MediaIri">The same-origin media IRI (<c>{base}/ap/v1/media/{id}</c>) — set as the
/// attachment's <c>url</c> (and the mirroring <c>id</c>).</param>
/// <param name="ContentType">The stored media's <c>Content-Type</c> (e.g. <c>image/png</c>,
/// <c>video/mp4</c>) — set as the attachment's <c>mediaType</c>.</param>
/// <param name="FileName">The uploaded file's original name — set as the attachment's <c>name</c>.</param>
public sealed record MediaAttachment(Iri MediaIri, string ContentType, string? FileName);

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
    /// <param name="cc">
    /// The optional cc'd audience link(s) (e.g. the author's followers collection and/or the public
    /// address). When null or empty the note carries no explicit <c>cc</c>.
    /// </param>
    /// <param name="media">
    /// The uploaded media files to attach (73.2 — multiple, mixed media per post), when any were
    /// uploaded. Each entry becomes a single <c>attachment</c> whose <c>url</c> (and mirroring
    /// <c>id</c>) is the entry's <see cref="MediaAttachment.MediaIri"/>, with <c>mediaType</c> and
    /// <c>name</c> from the entry's <see cref="MediaAttachment.ContentType"/> and
    /// <see cref="MediaAttachment.FileName"/>. An <c>image/*</c> content type yields an
    /// <see cref="Image"/> attachment (rendered in the feed's media gallery); any other content type
    /// (video, audio, document, …) yields a <see cref="Document"/> attachment (rendered via the rich
    /// attachment path). When null or empty the note carries no <c>attachment</c>.
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
        IEnumerable<Iri>? cc = null,
        IEnumerable<MediaAttachment>? media = null,
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

        // Emit the `source` field (Mastodon/Pleroma convention) when the content is Markdown-rendered
        // HTML. The `source` carries the original Markdown text so remote clients can re-render or
        // display the source. Written into ExtensionData (Rule 6) since the library does not model it.
        if (!string.IsNullOrWhiteSpace(markdownHtml))
        {
            note.ExtensionData ??= new Dictionary<string, JsonElement>();
            note.ExtensionData["source"] = JsonSerializer.SerializeToElement(new
            {
                content,
                mediaType = "text/markdown",
            });
        }

        if (sensitive)
        {
            // `sensitive` is a standard AS term the library leaves in ExtensionData (Rule 6) — set it
            // the same way the seeded/round-trip fixtures do, and the same way IsSensitive reads it.
            // (ExtensionData is null on a freshly-constructed note, so it must be initialized first.)
            note.ExtensionData ??= new Dictionary<string, JsonElement>();
            note.ExtensionData["sensitive"] = JsonSerializer.SerializeToElement(true);
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            // `summary` is a real AS property (the content-sensitivity preview). It is independent of
            // the `sensitive` flag (Mastodon/Pleroma convention): a note can carry a content warning
            // without being marked sensitive.
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

        // cc (73.3): the secondary audience (the author's followers and/or the public). Written via
        // ExtensionData (Rule 6) since the library has no `cc` property; serialized verbatim on the
        // wire so a remote client's visibility logic (public in cc ⇒ public; else followers-only)
        // resolves it.
        if (cc is not null)
        {
            var ccIris = cc.Where(i => i != default).Select(i => i.Value).ToList();
            if (ccIris.Count > 0)
            {
                note.ExtensionData ??= new Dictionary<string, JsonElement>();
                note.ExtensionData["cc"] = JsonSerializer.SerializeToElement(ccIris);
            }
        }

        if (media is { } mediaList)
        {
            // A note's media attachments (Phase 20.4 (a) / F-27, extended to multiple + mixed types in
            // 73.2): one attachment object per uploaded file, in order. Each's url is the same-origin
            // media IRI minted on upload (and its id mirrors it, so a reader can resolve the media
            // without the url); mediaType + name come from the upload (Decision 056 (b): the url is
            // same-origin, never a cross-origin media host). An image/* upload becomes an Image
            // (rendered in the feed's media gallery); any other content type becomes a Document
            // (rendered via the rich-attachment path — video/audio players, PDF/other doc cards).
            var attachments = new List<IObjectOrLink>();
            foreach (var entry in mediaList)
            {
                var attachment = BuildMediaAttachment(entry);
                if (attachment is not null)
                {
                    attachments.Add(attachment);
                }
            }

            if (attachments.Count > 0)
            {
                note.Attachment = attachments;
            }
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

    /// <summary>
    /// Builds a single attachment object from an uploaded media file (73.2): an <see cref="Image"/>
    /// for <c>image/*</c> content, a <see cref="Document"/> for anything else (video, audio,
    /// application/pdf, …). The attachment's <c>url</c> (and mirroring <c>id</c>) is the same-origin
    /// media IRI, with <c>mediaType</c> and (when present) <c>name</c> from the upload. Returns null
    /// when the entry carries no usable media IRI (a defensive guard — uploads always mint one).
    /// </summary>
    /// <param name="entry">The uploaded media file to attach.</param>
    /// <returns>The attachment object, or null when <paramref name="entry"/> is null/invalid.</returns>
    private static IObjectOrLink? BuildMediaAttachment(MediaAttachment? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.MediaIri.Value))
        {
            return null;
        }

        var isImage = string.Equals(entry.ContentType, "image", StringComparison.Ordinal)
            || entry.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        if (isImage)
        {
            var image = new Image
            {
                Url = [new Link { Href = entry.MediaIri.Uri }],
                Id = entry.MediaIri.Value,
                MediaType = entry.ContentType,
            };
            SetAttachmentName(image, entry.FileName);
            return image;
        }

        var document = new Document
        {
            Url = [new Link { Href = entry.MediaIri.Uri }],
            MediaType = entry.ContentType,
        };
        SetAttachmentName(document, entry.FileName);
        return document;
    }

    /// <summary>
    /// Sets an attachment's <c>name</c> (a single-element <c>Name</c> list) from a file name, leaving
    /// it unset (null) when the file name is null or whitespace (a blank name yields no <c>name</c>
    /// entry on the wire).
    /// </summary>
    /// <param name="attachment">The attachment whose <c>Name</c> is set.</param>
    /// <param name="fileName">The file name, or null/whitespace to leave the name unset.</param>
    private static void SetAttachmentName(ActivityObject attachment, string? fileName)
    {
        if (fileName is { Length: > 0 } && !string.IsNullOrWhiteSpace(fileName))
        {
            attachment.Name = [fileName];
        }
    }
}
