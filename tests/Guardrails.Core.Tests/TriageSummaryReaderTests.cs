using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #163: <see cref="TriageSummaryReader.TryRead"/> reads the <c>triage.json</c> sidecar the
/// needs-human triage leaves in its task-level log dir, so the run summary can surface the root-cause
/// category + one-line diagnosis. It must be best-effort: a missing/malformed/incomplete sidecar
/// returns null (the summary then falls back to the feedback pointer) and NEVER throws.
/// </summary>
public sealed class TriageSummaryReaderTests : IDisposable
{
    private readonly string _dir;

    public TriageSummaryReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gr-triage-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort teardown */ }
    }

    private void WriteSidecar(string json) =>
        File.WriteAllText(Path.Combine(_dir, "triage.json"), json);

    [Fact]
    public void MissingFile_ReturnsNull()
    {
        Assert.Null(TriageSummaryReader.TryRead(_dir));
    }

    [Fact]
    public void MalformedJson_ReturnsNull_DoesNotThrow()
    {
        WriteSidecar("{ not valid json");
        Assert.Null(TriageSummaryReader.TryRead(_dir));
    }

    [Fact]
    public void NoDiagnosisField_ReturnsNull()
    {
        WriteSidecar("""{ "summary": "something", "ghIssueTitle": "x" }""");
        Assert.Null(TriageSummaryReader.TryRead(_dir));
    }

    [Fact]
    public void DiagnosisNotAString_ReturnsNull()
    {
        WriteSidecar("""{ "diagnosis": 42 }""");
        Assert.Null(TriageSummaryReader.TryRead(_dir));
    }

    [Fact]
    public void FullSidecar_ReadsAllFields()
    {
        WriteSidecar(
            """
            {
              "diagnosis": "guardrails-tool",
              "summary": "plan-breakdown emits stableId as state-out key",
              "ghIssueTitle": "plan-breakdown: stableId used as state-out key"
            }
            """);

        TriageSummary? read = TriageSummaryReader.TryRead(_dir);

        Assert.NotNull(read);
        Assert.Equal("guardrails-tool", read!.Diagnosis);
        Assert.Equal("plan-breakdown emits stableId as state-out key", read.OneLine);
        Assert.Equal("plan-breakdown: stableId used as state-out key", read.GhIssueTitle);
    }

    [Fact]
    public void DiagnosisOnly_ReadsCategory_WithNullOptionalFields()
    {
        WriteSidecar("""{ "diagnosis": "local-repo" }""");

        TriageSummary? read = TriageSummaryReader.TryRead(_dir);

        Assert.NotNull(read);
        Assert.Equal("local-repo", read!.Diagnosis);
        Assert.Null(read.OneLine);
        Assert.Null(read.GhIssueTitle);
    }
}
