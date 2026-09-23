namespace Iris.Core.Identity;

/// <summary>
/// Decision logic for restoring a moderation "Reported" (flagged) state from a local actor's flags
/// collection. Lives here (rather than in the Blazor WASM client) so the rule is unit-testable
/// without booting a browser runtime.
/// </summary>
/// <remarks>
/// S72: the post card's Report button remembers its "Reported" state only in in-memory component
/// state, so a fresh page load (or any navigation away and back) loses it and the button looks
/// actionable even though the flag is still recorded. On load the card should restore the state from
/// the local actor's flags collection ({actor}/flags) when the card's moderation author is already in
/// that set. <see cref="ShouldRestoreReportedState"/> encodes that decision so it can be tested
/// directly.
/// </remarks>
public static class ReportStateRestore
{
    /// <summary>
    /// Decides whether the "Reported" state should be restored for a card's moderation author given
    /// the local actor's current flags collection.
    /// </summary>
    /// <param name="flaggedAuthorIris">The actors the local actor has already reported (flagged).</param>
    /// <param name="moderationAuthor">The card's moderation author (the Report target).</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="moderationAuthor"/> is already present in
    /// <paramref name="flaggedAuthorIris"/> (the button should render "Reported" / disabled);
    /// <see langword="false"/> otherwise, including when the moderation author is null.
    /// </returns>
    public static bool ShouldRestoreReportedState(IReadOnlyCollection<Iri> flaggedAuthorIris, Iri? moderationAuthor)
    {
        if (moderationAuthor is null)
        {
            return false;
        }

        return flaggedAuthorIris.Any(a => a.Equals(moderationAuthor));
    }
}
