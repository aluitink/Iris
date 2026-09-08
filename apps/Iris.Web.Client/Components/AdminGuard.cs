using Iris.Web.Client.Accounts;

namespace Iris.Web.Client.Components;

/// <summary>
/// Shared helpers for the administrator-only pages (<c>/admin/*</c>). The Blazor WebAssembly host does
/// not enforce <c>[Authorize]</c> on navigation — the server returns 401/403 (or a redirect to the login
/// page) for a non-admin's API call, and the WASM client would otherwise surface the raw failure (an
/// empty or non-JSON body) as a cryptic exception message. These helpers let an admin page detect the
/// signed-in user's role up front and render a friendly message instead of calling the admin API at all.
/// </summary>
internal static class AdminGuard
{
    /// <summary>
    /// The role claim value that grants access to the <c>/admin/*</c> pages (mirrors the server's
    /// <c>RequireRole("Admin")</c> policy).
    /// </summary>
    public const string AdminRole = "Admin";

    /// <summary>
    /// A friendly message for a signed-in user who is not an administrator.
    /// </summary>
    public const string NotAdminMessage =
        "This page is only available to instance administrators. " +
        "If you believe you should have access, contact your instance admin.";

    /// <summary>
    /// A friendly message for a signed-out user who reaches an admin page.
    /// </summary>
    public const string SignedOutMessage =
        "Please sign in with an administrator account to view this page.";

    /// <summary>
    /// Returns the friendly access-denied message for the given session, or null when the user is an
    /// administrator (and may proceed to load the admin data).
    /// </summary>
    public static string? AccessDenied(IActorSessionAccessor session)
    {
        if (!session.IsSignedIn)
        {
            return SignedOutMessage;
        }

        return string.Equals(session.Role, AdminRole, StringComparison.Ordinal)
            ? null
            : NotAdminMessage;
    }

    /// <summary>
    /// A user-facing message for an unexpected load failure. Never exposes the raw exception message
    /// (which can leak internal details such as JSON-parse errors, stack frames, or internal IRIs) —
    /// the 47.2 edge-case-hardening principle requires user-friendly copy instead.
    /// </summary>
    public static string FriendlyLoadError(string what)
    {
        return $"Couldn't load {what}. Please refresh the page and try again.";
    }
}
