using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// GR2056 (issue #473) — a guardrail SCRIPT that does not parse fails unconditionally, and no retry
/// can fix it: the script is not in the agent's write scope, so the task burns its whole budget and
/// settles <c>needs-human</c>. A live instance cost two attempts plus a halt for a stray backtick
/// inside a double-quoted string.
///
/// <para>The syntax probe is INJECTED here so these tests assert the validator's behaviour without
/// requiring <c>pwsh</c> or <c>bash</c> on the machine running them — and, more importantly, so the
/// "silence is not proof of validity" contract can be tested at all: a probe that reports nothing is
/// indistinguishable from an absent interpreter, and the validator must stay quiet in both cases.</para>
/// </summary>
public sealed class GuardrailScriptParseTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("gr2056-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void UnparseableGuardrail_EmitsGr2056_CitingTheInterpreterMessage()
    {
        GuardrailDefinition guardrail = WriteScript("01-broken", "if ($x -eq 1) { exit 1");
        var probe = new FakeSyntaxProbe { [guardrail.Path] = "Missing closing '}' in statement block." };

        Diagnostic d = Assert.Single(
            Validate(guardrail, probe),
            x => x.Code == DiagnosticCodes.GuardrailScriptDoesNotParse);

        // The interpreter's own words must survive into the diagnostic — a generic "does not parse"
        // sends the author hunting, and the parser already knows exactly what is wrong.
        Assert.Contains("Missing closing", d.Message, StringComparison.Ordinal);
        Assert.Contains("01-broken", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseableGuardrail_EmitsNothing()
    {
        GuardrailDefinition guardrail = WriteScript("01-fine", "exit 0");

        AssertSilent(guardrail, new FakeSyntaxProbe());
    }

    /// <summary>
    /// The load-bearing contract: a probe that reports nothing means "nothing PROVEN invalid", not
    /// "valid". An absent interpreter must never fail a plan — the plan author cannot control whether
    /// pwsh is installed on the validating machine, and a machine that cannot parse the script cannot
    /// run it either.
    /// </summary>
    [Fact]
    public void NoInterpreterAvailable_StaysSilent_RatherThanFlaggingEveryScript()
    {
        GuardrailDefinition guardrail = WriteScript("01-unknowable", "this is not valid powershell {{{");

        AssertSilent(guardrail, NullScriptSyntaxProbe.Instance);
    }

    /// <summary>
    /// A probe reporting a path the plan does not contain must not invent a diagnostic — the validator
    /// reports per KNOWN guardrail, keyed by path, not by whatever the probe happens to return.
    /// </summary>
    [Fact]
    public void ProbeReportingAnUnrelatedPath_IsIgnored()
    {
        GuardrailDefinition guardrail = WriteScript("01-fine", "exit 0");
        var probe = new FakeSyntaxProbe { [Path.Combine(_tempRoot, "somewhere-else.ps1")] = "boom" };

        AssertSilent(guardrail, probe);
    }

    // ============================================================================================
    // Helpers
    // ============================================================================================

    private sealed class FakeSyntaxProbe : Dictionary<string, string>, IScriptSyntaxProbe
    {
        public FakeSyntaxProbe() : base(StringComparer.OrdinalIgnoreCase) { }

        public IReadOnlyDictionary<string, string> FindSyntaxErrors(IReadOnlyList<string> scriptPaths) => this;
    }

    private IReadOnlyList<Diagnostic> Validate(GuardrailDefinition guardrail, IScriptSyntaxProbe probe) =>
        new PlanValidator(FakeExecutableProbe.All, new BannedPatternRegistry([]), probe)
            .Validate(PlanWithTaskGuardrail(guardrail));

    private void AssertSilent(GuardrailDefinition guardrail, IScriptSyntaxProbe probe) =>
        Assert.DoesNotContain(Validate(guardrail, probe),
            d => d.Code == DiagnosticCodes.GuardrailScriptDoesNotParse);

    private GuardrailDefinition WriteScript(string name, string body)
    {
        string dir = Path.Combine(_tempRoot, "tasks", "01-a", "guardrails");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".ps1");
        File.WriteAllText(path, body);
        return new GuardrailDefinition { Name = name, Path = path, Kind = ActionKind.Script };
    }

    private PlanDefinition PlanWithTaskGuardrail(GuardrailDefinition guardrail)
    {
        TaskNode task = new()
        {
            Id = "01-a",
            Directory = Path.Combine(_tempRoot, "tasks", "01-a"),
            Description = "task 01-a",
            Action = new ActionDefinition { Path = Path.Combine(_tempRoot, "tasks", "01-a", "action.ps1"), Kind = ActionKind.Script },
            Guardrails = [guardrail],
            Preflights = [],
        };

        return new PlanDefinition
        {
            PlanDirectory = _tempRoot,
            Workspace = _tempRoot,
            // Serial so the worktree-mode git-root / terminal-gate checks stay silent; GR2056 is the rule under test.
            Config = new RunConfig { Version = 1, MaxParallelism = 1 },
            Tasks = [task],
            PlanPreflights = [],
            PlanGuardrails = [],
        };
    }
}
