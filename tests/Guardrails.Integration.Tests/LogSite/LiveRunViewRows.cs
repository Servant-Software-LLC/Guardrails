using System.Text.RegularExpressions;

namespace Guardrails.Integration.Tests.LogSite;

/// <summary>
/// Reads one task's row off the log server's live run view (<c>GET /</c>). Shared by every test that pins
/// that page's Status column (issue #713), so they all parse the page the same way and a change to the row's
/// shape is made in one place.
/// </summary>
internal static class LiveRunViewRows
{
    /// <summary>One task's row: the word in its Status cell and the text of its Latest-attempt cell.</summary>
    public readonly record struct Row(string Status, string Attempt);

    /// <summary>
    /// Find <paramref name="taskId"/>'s row and read its two cells. Scoped to the ROW, never the page: an
    /// earlier root-page test matched a phrase that also appeared in the prose beside the table, and a
    /// mutation that should have failed it survived.
    /// </summary>
    public static Row Find(string html, string taskId)
    {
        Match row = Regex.Match(
            html,
            "<tr><td><a href=\"/tasks/" + Regex.Escape(Uri.EscapeDataString(taskId)) + "\">[^<]*</a></td>(?<cells>.*?)</tr>");
        Assert.True(row.Success, $"the live run view has no row for {taskId}:\n{html}");

        // The Status cell must have the static index's own shape: class="status" and a data-status the shared
        // CSS colors, repeating the word it shows. A page that fell back to "attempt N", or left the cell
        // empty, does not match — which is the point. That fallback has to fail here, loudly, rather than pass
        // as a plausible-looking cell.
        Match cells = Regex.Match(
            row.Groups["cells"].Value,
            "^<td class=\"status\" data-status=\"(?<word>[a-z-]+)\">\\k<word></td><td>(?<attempt>[^<]*)</td>$");
        Assert.True(cells.Success, $"{taskId}'s Status cell is not a status word:\n{row.Value}");

        return new Row(cells.Groups["word"].Value, cells.Groups["attempt"].Value);
    }
}
