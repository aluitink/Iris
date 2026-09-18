using System.Text.RegularExpressions;

namespace Iris.Web;

/// <summary>
/// The username / actor-handle rules enforced at registration. A username doubles as the linked
/// actor's handle (the local name in the WebFinger <c>acct:handle@host</c> URI and the
/// <c>/ap/v1/u/{handle}</c> route), so it must be a valid local handle: a short, URL-safe
/// identifier. The ActivityPub layer itself imposes no handle constraints (the server looks the
/// handle up by exact match and 404s if absent), so these are the app-level rules that keep handles
/// sane, unique, and unambiguous on the wire.
/// </summary>
public static partial class HandleRules
{
    /// <summary>The minimum handle length.</summary>
    public const int MinLength = 2;

    /// <summary>The maximum handle length (matches the <c>Actors.Handle</c> column width headroom).</summary>
    public const int MaxLength = 32;

    /// <summary>
    /// Handles that are reserved and cannot be registered: they collide with the server's well-known
    /// routes (the namespace document, the NodeInfo routes, etc.) or with the instance-level actors.
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ap", "ns", "nodeinfo", "admin", "api", "apiv1", "wellknown",
    };

    /// <summary>
    /// A handle is valid when it is <see cref="MinLength"/>–<see cref="MaxLength"/> characters of
    /// letters, digits, or hyphens; does not start or end with a hyphen; and is not
    /// <see cref="Reserved"/>. Only <c>[A-Za-z0-9-]</c> is allowed (no underscores, spaces, or
    /// punctuation) — a tight, URL-safe alphabet for the <c>/ap/v1/u/{handle}</c> route.
    /// </summary>
    /// <param name="handle">The candidate handle.</param>
    /// <returns>The validation error message, or <c>null</c> when the handle is valid.</returns>
    public static string? Validate(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return "A username is required.";
        }

        if (handle.Length < MinLength || handle.Length > MaxLength)
        {
            return $"The username must be {MinLength}–{MaxLength} characters.";
        }

        if (!Pattern().IsMatch(handle))
        {
            return "The username may contain only letters, digits, and hyphens.";
        }

        if (handle.StartsWith('-') || handle.EndsWith('-'))
        {
            return "The username must not start or end with a hyphen.";
        }

        if (Reserved.Contains(handle))
        {
            return $"The username '{handle}' is reserved.";
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex Pattern();
}
