using Guardrails.Core.Io;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// GR2022 (issue #121): <see cref="PlanValidator"/> rejects, at load time, a guardrail or
/// script-action body that reads another task's state namespace (<c>$state.'&lt;id&gt;'</c> etc.)
/// when no <c>dependsOn</c> path makes that producer a transitive ancestor and no <c>seed.json</c>
/// top-level key provides it. The real bug was a guardrail rewired to consume task <c>35</c>'s
/// state while <c>task.json</c> still only declared a dependency on <c>24</c> — validate passed and
/// the consumer ran first, failing at runtime as needs-human. These tests build real plan folders on
/// disk (the check reads script bodies), then validate via the loader.
/// </summary>
public sealed class CrossTaskStateReferenceValidatorTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "gr-gr2022-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => SafeDelete.DeleteDirectory(_tempRoot);

    private const string Gr2022 = DiagnosticCodes.CrossTaskStateReferenceWithoutDependency;

    [Fact]
    public void Reference_WithDependencyEdge_IsClean()
    {
        // The exact good shape: a consumer reads $state.'<producer>'.fileHashes and DECLARES the edge.
        string dir = Plan(
            Task("01-author-tests", dependsOn: [], guardrail: "exit 0"),
            Task("02-implement", dependsOn: ["01-author-tests"],
                guardrail: "$state = $env:GUARDRAILS_STATE_IN\n$h = $state.'01-author-tests'.fileHashes\nexit 0"));

        Assert.DoesNotContain(Validate(dir), d => d.Code == Gr2022);
    }

    [Fact]
    public void Reference_WithoutDependencyEdge_ReportsGr2022()
    {
        // The bug: consumer reads task 01's state but declares no path to it.
        string dir = Plan(
            Task("01-author-tests", dependsOn: [], guardrail: "exit 0"),
            Task("02-implement", dependsOn: [],
                guardrail: "$state = $env:GUARDRAILS_STATE_IN\n$h = $state.'01-author-tests'.fileHashes\nexit 0"));

        Diagnostic diagnostic = Assert.Single(Validate(dir), d => d.Code == Gr2022);
        Assert.Contains("02-implement", diagnostic.Message);
        Assert.Contains("01-author-tests", diagnostic.Message);
    }

    [Fact]
    public void Reference_ViaTransitiveAncestor_IsClean()
    {
        // 03 depends on 02 depends on 01: 01 is a transitive (not direct) ancestor of 03 — satisfied.
        string dir = Plan(
            Task("01-author-tests", dependsOn: [], guardrail: "exit 0"),
            Task("02-middle", dependsOn: ["01-author-tests"], guardrail: "exit 0"),
            Task("03-consumer", dependsOn: ["02-middle"],
                guardrail: "$h = $state.'01-author-tests'.fileHashes\nexit 0"));

        Assert.DoesNotContain(Validate(dir), d => d.Code == Gr2022);
    }

    [Fact]
    public void Reference_OnScriptActionBody_AlsoReportsGr2022()
    {
        // The action body (not just guardrails) is linted too.
        string dir = Plan(
            Task("01-producer", dependsOn: [], guardrail: "exit 0"),
            Task("02-consumer", dependsOn: [], guardrail: "exit 0",
                action: "$h = $state.'01-producer'.fileHashes\nexit 0"));

        Assert.Single(Validate(dir), d => d.Code == Gr2022);
    }

    [Fact]
    public void Reference_SatisfiedBySeedTopLevelKey_IsClean()
    {
        // A pre-existing baseline: seed.json carries a top-level key equal to the referenced id, so
        // the value is present from run start regardless of scheduling — no edge required (SSOT §6.2).
        string dir = Plan(
            seedJson: """{ "01-baseline": { "fileHashes": {} } }""",
            Task("01-baseline", dependsOn: [], guardrail: "exit 0"),
            Task("02-consumer", dependsOn: [],
                guardrail: "$h = $state.'01-baseline'.fileHashes\nexit 0"));

        Assert.DoesNotContain(Validate(dir), d => d.Code == Gr2022);
    }

    [Fact]
    public void SelfReference_IsClean()
    {
        // A task reading its OWN namespace is always satisfiable — it writes that namespace.
        string dir = Plan(
            Task("01-only", dependsOn: [],
                guardrail: "$h = $state.'01-only'.fileHashes\nexit 0"));

        Assert.DoesNotContain(Validate(dir), d => d.Code == Gr2022);
    }

    [Fact]
    public void QuotedStringThatIsNotATaskId_IsClean()
    {
        // A state deref of a key that matches no task id is not a cross-task reference (could be a
        // seed/own scalar, or any quoted string) — must NOT false-positive.
        string dir = Plan(
            Task("01-only", dependsOn: [],
                guardrail: "$x = $state.'recipientName'\nexit 0"));

        Assert.DoesNotContain(Validate(dir), d => d.Code == Gr2022);
    }

    [Fact]
    public void BracketIndexForm_ReportsGr2022()
    {
        // The JS/Python/jq bracket-index idiom state["<id>"] is matched too (not just the PowerShell
        // $state.'<id>' property-access form).
        string dir = Plan(
            Task("01-producer", dependsOn: [], guardrail: "exit 0"),
            Task("02-consumer", dependsOn: [],
                guardrail: "h=$(jq -r 'state[\"01-producer\"].fileHashes' \"$GUARDRAILS_STATE_IN\")\nexit 0"));

        Assert.Single(Validate(dir), d => d.Code == Gr2022);
    }

    [Fact]
    public void IdentifierEndingInState_IsNotMatched()
    {
        // A variable like $myState or a word like substate must NOT be mistaken for a state access:
        // the leading word-boundary guard prevents matching inside a larger identifier.
        string dir = Plan(
            Task("01-producer", dependsOn: [], guardrail: "exit 0"),
            Task("02-consumer", dependsOn: [],
                guardrail: "$myState.'01-producer' = 1\nexit 0"));

        Assert.DoesNotContain(Validate(dir), d => d.Code == Gr2022);
    }

    [Fact]
    public void DuplicateReferences_InOneBody_ReportOnce()
    {
        string dir = Plan(
            Task("01-producer", dependsOn: [], guardrail: "exit 0"),
            Task("02-consumer", dependsOn: [],
                guardrail: "$a = $state.'01-producer'.fileHashes\n$b = $state.'01-producer'.other\nexit 0"));

        Assert.Single(Validate(dir), d => d.Code == Gr2022);
    }

    // --- on-disk plan builder ---------------------------------------------------------------

    private IReadOnlyList<Diagnostic> Validate(string dir)
    {
        PlanLoadResult result = new PlanLoader().Load(dir);
        Assert.NotNull(result.Plan);
        // Resolve every interpreter so .ps1/.sh extension probing never injects unrelated errors.
        return new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!);
    }

    private string Plan(params TaskSpec[] tasks) => Plan(seedJson: null, tasks);

    private string Plan(string? seedJson, params TaskSpec[] tasks)
    {
        string dir = Path.Combine(_tempRoot, "plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // maxParallelism omitted ⇒ default; a simple serial-friendly config keeps the parallel
        // gates (GR2015/2017/2018) from firing and muddying these assertions.
        File.WriteAllText(Path.Combine(dir, "guardrails.json"), """{ "version": 1, "maxParallelism": 1 }""");

        if (seedJson is not null)
        {
            string stateDir = Path.Combine(dir, "state");
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(Path.Combine(stateDir, "seed.json"), seedJson);
        }

        foreach (TaskSpec task in tasks)
        {
            string taskDir = Path.Combine(dir, "tasks", task.Id);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            string dependsOn = task.DependsOn.Length == 0
                ? "[]"
                : "[" + string.Join(", ", task.DependsOn.Select(d => $"\"{d}\"")) + "]";
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                $$"""{ "description": "fixture {{task.Id}}", "dependsOn": {{dependsOn}} }""");

            File.WriteAllText(Path.Combine(taskDir, "action.ps1"), task.Action);
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), task.Guardrail);
        }

        return dir;
    }

    private static TaskSpec Task(string id, string[] dependsOn, string guardrail, string action = "exit 0") =>
        new(id, dependsOn, guardrail, action);

    private sealed record TaskSpec(string Id, string[] DependsOn, string Guardrail, string Action);
}
