using Iris.Core;

namespace Iris.Core.Tests.Identity;

/// <summary>
/// Unit tests for <see cref="ReportStateRestore"/> — the decision that restores a post card's
/// "Reported" (flagged) state from the local actor's flags collection on load (S72). Without this
/// rule the Report button's state is in-memory only and is lost on every page load, so an already
/// reported author renders an actionable "Report" button.
/// </summary>
public class ReportStateRestoreTests
{
    [Fact]
    public void ModerationAuthor_InFlags_RestoresReportedState()
    {
        var flagged = new Iri("https://b.local/u/offender");
        var flags = new List<Iri> { new Iri("https://c.local/u/other"), flagged };

        Assert.True(ReportStateRestore.ShouldRestoreReportedState(flags, flagged));
    }

    [Fact]
    public void ModerationAuthor_NotInFlags_DoesNotRestore()
    {
        var flagged = new Iri("https://b.local/u/offender");
        var flags = new List<Iri> { new Iri("https://c.local/u/other") };

        Assert.False(ReportStateRestore.ShouldRestoreReportedState(flags, flagged));
    }

    [Fact]
    public void EmptyFlags_DoesNotRestore()
    {
        var flagged = new Iri("https://b.local/u/offender");

        Assert.False(ReportStateRestore.ShouldRestoreReportedState([], flagged));
    }

    [Fact]
    public void NullModerationAuthor_DoesNotRestore()
    {
        var flags = new List<Iri> { new Iri("https://b.local/u/offender") };

        Assert.False(ReportStateRestore.ShouldRestoreReportedState(flags, (Iri?)null));
    }

    [Fact]
    public void DistinctIris_AreNotTreatedAsReported()
    {
        // Two different actors must not collide: a flag on actor X must not mark actor Y reported.
        var actorX = new Iri("https://b.local/u/x");
        var actorY = new Iri("https://b.local/u/y");
        var flags = new List<Iri> { actorX };

        Assert.False(ReportStateRestore.ShouldRestoreReportedState(flags, actorY));
        Assert.True(ReportStateRestore.ShouldRestoreReportedState(flags, actorX));
    }
}
