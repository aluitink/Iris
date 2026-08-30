namespace Iris.Client;

/// <summary>
/// A minimal projection of an instance's RFC 8555 NodeInfo document (the instance metadata the server
/// serves at <c>{base}/ap/v1/nodeinfo/2.0</c>). The explorer's instance-overview screen reads this to
/// show the instance name, software, and protocols. Only the fields the explorer displays are mapped;
/// the rest of the document is ignored.
/// </summary>
/// <param name="Version">The NodeInfo spec version (e.g. <c>2.0</c>).</param>
/// <param name="SoftwareName">The server software name (e.g. <c>iris</c>), or null when absent.</param>
/// <param name="SoftwareVersion">The server software version, or null when absent.</param>
/// <param name="Protocols">The protocols the instance speaks (e.g. <c>["activitypub"]</c>).</param>
/// <param name="OpenRegistrations">Whether the instance accepts new registrations.</param>
/// <param name="InstanceName">The instance's display name (from <c>metadata.name</c>), or null when absent.</param>
/// <param name="Description">The instance's description (from <c>metadata.description</c>), or null when
/// absent.</param>
public sealed record NodeInfo(
    string Version,
    string? SoftwareName,
    string? SoftwareVersion,
    IReadOnlyList<string> Protocols,
    bool OpenRegistrations,
    string? InstanceName,
    string? Description)
{
    private const string JsonOptions = "nodeinfo";

    /// <summary>
    /// Parses a NodeInfo document from JSON, or <see langword="null"/> when the body is empty or not
    /// valid NodeInfo JSON.
    /// </summary>
    /// <param name="json">The raw NodeInfo document (the body of <c>GET /nodeinfo/2.0</c>).</param>
    public static NodeInfo? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            string Version(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()! : string.Empty;

            var protocols = new List<string>();
            if (root.TryGetProperty("protocols", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var el in p.EnumerateArray())
                {
                    if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        protocols.Add(el.GetString()!);
                    }
                }
            }

            string? instanceName = null;
            string? description = null;
            bool openRegistrations = false;
            if (root.TryGetProperty("openRegistrations", out var or))
            {
                openRegistrations = or.ValueKind == System.Text.Json.JsonValueKind.True;
            }

            if (root.TryGetProperty("metadata", out var meta) && meta.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                instanceName = meta.TryGetProperty("name", out var mn) && mn.ValueKind == System.Text.Json.JsonValueKind.String ? mn.GetString() : null;
                description = meta.TryGetProperty("description", out var md) && md.ValueKind == System.Text.Json.JsonValueKind.String ? md.GetString() : null;
            }

            var softwareName = root.TryGetProperty("software", out var sw) && sw.ValueKind == System.Text.Json.JsonValueKind.Object && sw.TryGetProperty("name", out var sn) && sn.ValueKind == System.Text.Json.JsonValueKind.String ? sn.GetString() : null;
            var softwareVersion = root.TryGetProperty("software", out var sw2) && sw2.ValueKind == System.Text.Json.JsonValueKind.Object && sw2.TryGetProperty("version", out var sv) && sv.ValueKind == System.Text.Json.JsonValueKind.String ? sv.GetString() : null;

            return new NodeInfo(
                Version("version"),
                softwareName,
                softwareVersion,
                [.. protocols],
                openRegistrations,
                instanceName,
                description);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
