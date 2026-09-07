namespace Iris.Web.Components;

/// <summary>
/// Shared relative-time formatting for the Blazor UI. Converts a UTC timestamp to a short
/// human-readable string ("just now", "5m ago", "3h ago", "2d ago") with a fallback to the
/// absolute local time for anything older than a week or in the future.
/// </summary>
internal static class TimeFormatting
{
    public static string Relative(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span < TimeSpan.Zero)
        {
            return utc.ToLocalTime().ToString("g");
        }

        if (span < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (span < TimeSpan.FromMinutes(60))
        {
            return $"{(int)span.TotalMinutes}m ago";
        }

        if (span < TimeSpan.FromHours(24))
        {
            return $"{(int)span.TotalHours}h ago";
        }

        if (span < TimeSpan.FromDays(7))
        {
            return $"{(int)span.TotalDays}d ago";
        }

        return utc.ToLocalTime().ToString("g");
    }
}
