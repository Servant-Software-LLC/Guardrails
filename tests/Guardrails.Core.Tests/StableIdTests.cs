using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Covers the optional <c>stableId</c> task field (SSOT §3/§11): the loader surfaces it
/// (trimmed, whitespace ⇒ null), and <see cref="PlanValidator"/> enforces that any declared
/// <c>stableId</c> is unique across tasks (GR2010) since the regeneration merge keys identity
/// on it. Plans are built on disk in temp folders so the fixtures stay self-contained.
/// </summary>
public sealed class StableIdTests : IDisposable
{
    private readonly string _planDir;

    public StableIdTests()
    {
        _planDir = Path.Combine(Path.GetTempPath(), "gr-stableid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_planDir, "tasks"));
        File.WriteAllText(Path.Combine(_planDir, "guardrails.json"), "{ \"version\": 1 }");
    }

    /// <summary>Add a task folder with an action + one guardrail, optionally declaring a stableId.</summary>
    private void AddTask(string id, string? stableId = null)
    {
        string taskDir = Path.Combine(_planDir, "tasks", id);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        string stableIdLine = stableId is null ? "" : $"  \"stableId\": \"{stableId}\",\n";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            "{\n" + stableIdLine + "  \"description\": \"Task " + id + "\",\n  \"dependsOn\": []\n}");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\nexit 0\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.sh"), "exit 0\n");
    }

    private PlanDefinition Load()
    {
        PlanLoadResult result = new PlanLoader().Load(_planDir);
        Assert.NotNull(result.Plan);
        return result.Plan!;
    }

    [Fact]
    public void Loader_DeclaredStableId_IsSurfacedOnTaskNode()
    {
        AddTask("01-a", stableId: "k3f9a1");

        TaskNode task = Assert.Single(Load().Tasks);
        Assert.Equal("k3f9a1", task.StableId);
    }

    [Fact]
    public void Loader_NoStableId_IsNull()
    {
        AddTask("01-a");

        TaskNode task = Assert.Single(Load().Tasks);
        Assert.Null(task.StableId);
    }

    [Fact]
    public void Loader_WhitespaceStableId_IsNull()
    {
        AddTask("01-a", stableId: "   ");

        TaskNode task = Assert.Single(Load().Tasks);
        Assert.Null(task.StableId);
    }

    [Fact]
    public void Loader_StableId_IsTrimmed()
    {
        AddTask("01-a", stableId: "  k3f9a1  ");

        TaskNode task = Assert.Single(Load().Tasks);
        Assert.Equal("k3f9a1", task.StableId);
    }

    [Fact]
    public void Validate_DuplicateStableId_ReportsGR2010()
    {
        AddTask("01-a", stableId: "dup");
        AddTask("02-b", stableId: "dup");

        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(Load());

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.DuplicateStableId);
        Assert.Contains("dup", diagnostic.Message);
    }

    [Fact]
    public void Validate_DistinctStableIds_NoDuplicateDiagnostic()
    {
        AddTask("01-a", stableId: "one");
        AddTask("02-b", stableId: "two");

        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(Load());

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.DuplicateStableId);
    }

    [Fact]
    public void Validate_TasksWithoutStableId_AreNotFlagged()
    {
        // stableId is optional; two tasks both omitting it must not collide on "null".
        AddTask("01-a");
        AddTask("02-b");

        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(Load());

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.DuplicateStableId);
    }

    // --- GR2011: stableId format (^[a-z0-9][a-z0-9._-]*$) ------------------------------

    [Theory]
    [InlineData("Bad_Caps")]   // uppercase not allowed
    [InlineData("has space")]  // whitespace not allowed
    [InlineData("-leading")]   // must start with an alphanumeric
    public void Validate_MalformedStableId_ReportsGR2011(string stableId)
    {
        AddTask("01-a", stableId: stableId);

        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(Load());

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.InvalidStableId);
        Assert.Contains(stableId, diagnostic.Message);
    }

    [Theory]
    [InlineData("k3f9a1")]
    [InlineData("a.b-c_d")]
    public void Validate_WellFormedStableId_NoInvalidStableIdDiagnostic(string stableId)
    {
        AddTask("01-a", stableId: stableId);

        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(Load());

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.InvalidStableId);
    }

    [Fact]
    public void Validate_FolderPrefixedStableId_IsRejected()
    {
        // The merge derives a synthetic 'folder:<name>' identity for unkeyed tasks; the format
        // reservation (a ':' is disallowed) keeps a real stableId from ever colliding with it.
        AddTask("01-a", stableId: "folder:thing");

        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(Load());

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.InvalidStableId);
        Assert.Contains("folder:thing", diagnostic.Message);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_planDir))
            {
                Directory.Delete(_planDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
