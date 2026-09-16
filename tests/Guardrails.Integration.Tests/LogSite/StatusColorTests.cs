using System.Text.RegularExpressions;
using Guardrails.Cli.Ui;

namespace Guardrails.Integration.Tests.LogSite;

/// <summary>
/// #713 review, N2: every status word a log-site page can show has a color. The live run view and the during-run index
/// both render the log-site observer's own words, and that observer says <c>cancelled</c> for a task a cancelled run
/// never finished, and <c>skipped</c> for one a resume found already succeeded. <c>cancelled</c> had no color rule, so
/// it rendered in the plain body color, indistinguishable at a glance from text that is not a status at all.
/// </summary>
public sealed class StatusColorTests
{
    [Theory]
    [InlineData("succeeded")]
    [InlineData("skipped")]
    [InlineData("running")]
    [InlineData("needs-human")]
    [InlineData("failed")]
    [InlineData("blocked")]
    [InlineData("pending")]
    [InlineData("cancelled")]
    [InlineData("unknown")]
    public void EveryStatusWordAPageCanShow_HasAColorRule(string status)
    {
        Assert.Matches(
            new Regex(@"\.status\[data-status=""" + Regex.Escape(status) + @"""\][^{]*\{[^}]*\bcolor:"),
            LogSiteRenderer.SharedStyle);
    }
}
