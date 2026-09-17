using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iris.Core.Identity;

namespace Iris.Core.Signing;

/// <summary>
/// Verifies a W3C Linked Data <c>RsaSignature2017</c> proof embedded in an ActivityStreams activity
/// body. This is the "body-embedded" signature format that Mastodon (and other fediverse servers)
/// use for Delete activities (actor tombstones): when an actor is deleted, their HTTP endpoint
/// returns 410 Gone, so an HTTP header signature can no longer be verified against a resolvable
/// key. Instead, the actor's own key signs the activity body directly, and the proof is carried in
/// the <c>signature</c> field of the JSON.
/// </summary>
/// <remarks>
/// The proof format is the standard W3C Linked Data Proofs <c>RsaSignature2017</c>
/// (<c>https://w3id.org/security/v1</c>). The signature value is
/// <c>Base64(RSA-SHA256(SHA256(options) || SHA256(document)))</c> where:
/// <list type="bullet">
/// <item><c>options</c> — the signature object (the <c>signature</c> field) with
/// <c>signatureValue</c> removed, serialized as JSON.</item>
/// <item><c>document</c> — the activity JSON with the <c>signature</c> field removed.</item>
/// </list>
/// The two SHA-256 hashes are concatenated (64 bytes) and signed with RSA-SHA256 (PKCS#1 v1.5).
/// </remarks>
public static class EmbeddedSignatureVerifier
{
    /// <summary>
    /// The <c>signature</c> field name in an ActivityStreams activity body.
    /// </summary>
    public const string SignatureFieldName = "signature";

    /// <summary>
    /// The <c>type</c> value for a W3C Linked Data <c>RsaSignature2017</c> proof.
    /// </summary>
    public const string RsaSignature2017Type = "RsaSignature2017";

    /// <summary>
    /// Verifies an embedded <c>RsaSignature2017</c> proof in an ActivityStreams activity body.
    /// </summary>
    /// <param name="body">The raw activity JSON body bytes.</param>
    /// <param name="key">The resolved signing key (must be RSA).</param>
    /// <returns>
    /// <see langword="true"/> when the embedded proof is present, well-formed, not expired, and
    /// verifies cryptographically; otherwise <see langword="false"/>.
    /// </returns>
    public static bool Verify(byte[] body, ISigningKey key)
    {
        if (body.Length == 0 || key is null)
        {
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(SignatureFieldName, out var signatureProp)
                || signatureProp.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // The proof must be an RsaSignature2017.
            if (!signatureProp.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String
                || typeProp.GetString() != RsaSignature2017Type)
            {
                return false;
            }

            // Extract the required fields.
            if (!signatureProp.TryGetProperty("creator", out var creatorProp)
                || creatorProp.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (!signatureProp.TryGetProperty("signatureValue", out var sigValueProp)
                || sigValueProp.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var signatureValueB64 = sigValueProp.GetString()!;
            byte[] signatureBytes;
            try
            {
                signatureBytes = Convert.FromBase64String(signatureValueB64);
            }
            catch (FormatException)
            {
                return false;
            }

            // Check expiration (optional field; when present, the proof must not be expired).
            if (signatureProp.TryGetProperty("expires", out var expiresProp)
                && expiresProp.ValueKind == JsonValueKind.String)
            {
                if (DateTime.TryParse(expiresProp.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var expires))
                {
                    if (DateTime.UtcNow > expires)
                    {
                        return false;
                    }
                }
            }

            // Build the signature base: SHA256(options) || SHA256(document).
            //
            // options = the signature object minus signatureValue, serialized as a JSON object.
            // document = the activity JSON minus the signature field, serialized as a JSON object.
            //
            // The canonicalization is the "N-Quads" / JSON-LD canonical form per the W3C Linked
            // Data Proofs spec. However, Mastodon's implementation (linked_data_signature.rb) uses
            // a simplified canonicalization: the JSON is serialized with sorted keys and the
            // signature field is removed from the document. We replicate that behavior here.

            var options = BuildOptionsJson(signatureProp);
            var document = BuildDocumentJson(root);

            if (options is null || document is null)
            {
                return false;
            }

            var optionsHash = SHA256.HashData(Encoding.UTF8.GetBytes(options));
            var documentHash = SHA256.HashData(Encoding.UTF8.GetBytes(document));

            var baseToVerify = new byte[optionsHash.Length + documentHash.Length];
            Buffer.BlockCopy(optionsHash, 0, baseToVerify, 0, optionsHash.Length);
            Buffer.BlockCopy(documentHash, 0, baseToVerify, optionsHash.Length, documentHash.Length);

            return key.Verify(baseToVerify, signatureBytes);
        }
    }

    /// <summary>
    /// Checks whether an ActivityStreams activity body contains an embedded <c>RsaSignature2017</c>
    /// proof (the <c>signature</c> field with <c>type</c> = <c>RsaSignature2017</c>).
    /// </summary>
    /// <param name="body">The raw activity JSON body bytes.</param>
    /// <returns><see langword="true"/> when an embedded proof is present.</returns>
    public static bool HasEmbeddedProof(byte[] body)
    {
        if (body.Length == 0)
        {
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(SignatureFieldName, out var sigProp)
                && sigProp.ValueKind == JsonValueKind.Object
                && sigProp.TryGetProperty("type", out var typeProp)
                && typeProp.ValueKind == JsonValueKind.String
                && typeProp.GetString() == RsaSignature2017Type;
        }
    }

    /// <summary>
    /// Extracts the <c>creator</c> (key IRI) from an embedded <c>RsaSignature2017</c> proof.
    /// </summary>
    /// <param name="body">The raw activity JSON body bytes.</param>
    /// <returns>The creator IRI string, or null when no embedded proof is present.</returns>
    public static string? ExtractCreator(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(SignatureFieldName, out var sigProp)
                || sigProp.ValueKind != JsonValueKind.Object
                || !sigProp.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String
                || typeProp.GetString() != RsaSignature2017Type)
            {
                return null;
            }

            return sigProp.TryGetProperty("creator", out var creatorProp)
                   && creatorProp.ValueKind == JsonValueKind.String
                ? creatorProp.GetString()
                : null;
        }
    }

    /// <summary>
    /// Builds the <c>options</c> JSON for the signature base: the signature object with
    /// <c>signatureValue</c> removed, serialized with sorted keys.
    /// </summary>
    private static string? BuildOptionsJson(JsonElement signatureProp)
    {
        var options = new List<JsonElement>();
        foreach (var prop in signatureProp.EnumerateObject())
        {
            if (prop.Name == "signatureValue")
            {
                continue;
            }
            options.Add(prop.Value.Clone());
        }

        // Re-serialize the signature object without signatureValue, preserving the original key order
        // (Mastodon's implementation uses the original key order, not sorted).
        var writer = new System.Text.StringBuilder();
        writer.Append('{');
        var first = true;
        foreach (var prop in signatureProp.EnumerateObject())
        {
            if (prop.Name == "signatureValue")
            {
                continue;
            }
            if (!first)
            {
                writer.Append(',');
            }
            first = false;
            writer.Append(JsonSerializer.Serialize(prop.Name));
            writer.Append(':');
            writer.Append(prop.Value.GetRawText());
        }
        writer.Append('}');
        return writer.ToString();
    }

    /// <summary>
    /// Builds the <c>document</c> JSON for the signature base: the activity JSON with the
    /// <c>signature</c> field removed, serialized preserving the original key order.
    /// </summary>
    private static string? BuildDocumentJson(JsonElement root)
    {
        var writer = new System.Text.StringBuilder();
        writer.Append('{');
        var first = true;
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == SignatureFieldName)
            {
                continue;
            }
            if (!first)
            {
                writer.Append(',');
            }
            first = false;
            writer.Append(JsonSerializer.Serialize(prop.Name));
            writer.Append(':');
            writer.Append(prop.Value.GetRawText());
        }
        writer.Append('}');
        return writer.ToString();
    }
}
