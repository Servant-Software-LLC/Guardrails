namespace Guardrails.Integration.Tests.LogSite;

/// <summary>
/// Reads a during-run page's <c>#gr-live-offline</c> notice: what a page opened as a file says, since it cannot
/// poll for updates. Shared by the tests that pin where that notice points (issue #714).
/// </summary>
internal static class OfflineNotice
{
    /// <summary>The notice element's content, from its id attribute to its closing tag.</summary>
    public static string In(string html)
    {
        int start = html.IndexOf("id=\"gr-live-offline\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"expected the offline notice on a during-run page:\n{html}");
        int end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        Assert.True(end > start, "expected the offline notice to be a closed element");
        return html[start..end];
    }
}
