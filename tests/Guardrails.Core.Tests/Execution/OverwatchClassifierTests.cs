using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.Execution;

/// <summary>
/// The MECHANICAL ASYMMETRY (SSOT §9.2, doc 11 §3): the pure <see cref="OverwatchFixClassifier"/> is the
/// load-bearing guarantee that self-healing can never soften a deterministic guardrail's verdict. These
/// tests pin the allowlist/denylist/default polarity that the maintainer's adversarial pass scrutinizes —
/// can it EVER classify a verdict-surface change (guardrail body / writeScope / scope / dependsOn /
/// integrationGate) as anything other than denylist, or launder a non-budget field onto the allowlist?
/// </summary>
public sealed class OverwatchClassifierTests
{
    private static readonly string PlanDir = Path.Combine(Path.GetTempPath(), "gr-overwatch-classifier-plan");
    private static readonly string TaskDir = Path.Combine(PlanDir, "tasks", "03-impl");
    private static readonly string OtherTaskDir = Path.Combine(PlanDir, "tasks", "05-other");
    private static readonly string WaveDir = Path.Combine(PlanDir, "wave-02-build");

    private static TaskNode Task(string id, string dir) => new()
    {
        Id = id,
        Directory = dir,
        Description = "t",
        Action = new ActionDefinition { Path = Path.Combine(dir, "action.prompt.md"), Kind = ActionKind.Prompt },
        Guardrails =
        [
            new GuardrailDefinition { Name = "01-check", Path = Path.Combine(dir, "guardrails", "01-check.ps1"), Kind = ActionKind.Script }
        ]
    };

    private static PlanDefinition Plan(bool waved = false)
    {
        TaskNode task = Task("03-impl", TaskDir);
        TaskNode other = Task("05-other", OtherTaskDir);
        return new PlanDefinition
        {
            PlanDirectory = PlanDir,
            Workspace = PlanDir,
            Config = new RunConfig { Version = 1 },
            Tasks = [task, other],
            Waves = waved
                ? [new WaveNode { Dir = "wave-02-build", Number = 2, Slug = "build", Directory = WaveDir, Tasks = [] }]
                : []
        };
    }

    private static OverwatchAuthorityClass Classify(OverwatchFixOp op, bool waved = false) =>
        OverwatchFixClassifier.Classify(op, Task("03-impl", TaskDir), Plan(waved));

    // ── Allowlist: the action/budget layer ──────────────────────────────────────────────────────

    [Fact]
    public void GuidanceInjection_IsAllowlist()
    {
        OverwatchAuthorityClass c = Classify(new OverwatchFixOp
        {
            Kind = OverwatchFixKind.GuidanceInjection,
            Guidance = "focus on the failing assertion in Foo.Tests"
        });
        Assert.Equal(OverwatchAuthorityClass.Allowlist, c);
    }

    [Theory]
    [InlineData("maxTurns")]
    [InlineData("retries")]
    [InlineData("timeoutSeconds")]
    [InlineData("MAXTURNS")] // case-insensitive
    public void BudgetOverride_SanctionedField_IsAllowlist(string field)
    {
        OverwatchAuthorityClass c = Classify(new OverwatchFixOp
        {
            Kind = OverwatchFixKind.BudgetOverride,
            BudgetField = field,
            BudgetValue = 40
        });
        Assert.Equal(OverwatchAuthorityClass.Allowlist, c);
    }

    [Fact]
    public void BudgetOverride_NonBudgetField_IsNotLaunderedToAllowlist()
    {
        // A judge that mislabels "writeScope" as a budget field must NOT be laundered onto the allowlist.
        OverwatchAuthorityClass c = Classify(new OverwatchFixOp
        {
            Kind = OverwatchFixKind.BudgetOverride,
            BudgetField = "writeScope",
            BudgetValue = 1
        });
        Assert.Equal(OverwatchAuthorityClass.Default, c);
    }

    // ── Denylist: the verdict surface — task.json verdict fields ─────────────────────────────────

    [Theory]
    [InlineData("writeScope")]
    [InlineData("scope")]
    [InlineData("dependsOn")]
    [InlineData("integrationGate")]
    [InlineData("WriteScope")] // case-insensitive
    public void TaskFieldEdit_VerdictField_IsDenylist(string field)
    {
        OverwatchAuthorityClass c = Classify(new OverwatchFixOp
        {
            Kind = OverwatchFixKind.TaskFieldEdit,
            TaskField = field
        });
        Assert.Equal(OverwatchAuthorityClass.Denylist, c);
    }

    [Fact]
    public void TaskFieldEdit_NonVerdictField_IsDefault()
    {
        OverwatchAuthorityClass c = Classify(new OverwatchFixOp
        {
            Kind = OverwatchFixKind.TaskFieldEdit,
            TaskField = "description"
        });
        Assert.Equal(OverwatchAuthorityClass.Default, c);
    }

    // ── Denylist: the verdict surface — guardrail/preflight bodies (the four folders) ────────────

    [Fact]
    public void FileEdit_TaskGuardrailBody_IsDenylist()
    {
        OverwatchAuthorityClass c = Classify(new OverwatchFixOp
        {
            Kind = OverwatchFixKind.FileEdit,
            TargetPath = Path.Combine(TaskDir, "guardrails", "01-check.ps1")
        });
        Assert.Equal(OverwatchAuthorityClass.Denylist, c);
    }

    [Fact]
    public void FileEdit_TaskPreflightBody_IsDenylist()
    {
        OverwatchAuthorityClass c = Classify(new OverwatchFixOp
        {
            Kind = OverwatchFixKind.FileEdit,
            TargetPath = Path.Combine(TaskDir, "preflights", "01-delivered.ps1")
        });
        Assert.Equal(OverwatchAuthorityClass.Denylist, c);
    }

    [Fact]
    public void FileEdit_AnotherTaskGuardrailBody_IsDenylist()
    {
        // A proposal for THIS task must never be able to edit ANOTHER task's guardrail body either.
        OverwatchAuthorityClass c = Classify(new OverwatchFixOp
        {
            Kind = OverwatchFixKind.FileEdit,
            TargetPath = Path.Combine(OtherTaskDir, "guardrails", "02-other.ps1")
        });
        Assert.Equal(OverwatchAuthorityClass.Denylist, c);
    }

    [Theory]
    [InlineData("guardrails/99-terminal.ps1")]  // plan-level terminal gate
    [InlineData("preflights/01-flight.ps1")]    // plan-level full-flight checks
    public void FileEdit_PlanLevelVerdictFolder_IsDenylist_RelativePath(string relative)
    {
        OverwatchAuthorityClass c = Classify(new OverwatchFixOp
        {
            Kind = OverwatchFixKind.FileEdit,
            TargetPath = relative
        });
        Assert.Equal(OverwatchAuthorityClass.Denylist, c);
    }

    [Fact]
    public void FileEdit_WaveLevelGuardrailBody_IsDenylist()
    {
        OverwatchAuthorityClass c = Classify(
            new OverwatchFixOp
            {
                Kind = OverwatchFixKind.FileEdit,
                TargetPath = Path.Combine(WaveDir, "guardrails", "01-wave-gate.ps1")
            },
            waved: true);
        Assert.Equal(OverwatchAuthorityClass.Denylist, c);
    }

    [Fact]
    public void FileEdit_NewNotYetExistingGuardrailFile_IsStillDenylist()
    {
        // The containment test catches a file a judge would CREATE under a guardrails folder — still verdict surface.
        OverwatchAuthorityClass c = Classify(new OverwatchFixOp
        {
            Kind = OverwatchFixKind.FileEdit,
            TargetPath = Path.Combine(TaskDir, "guardrails", "03-brand-new.ps1")
        });
        Assert.Equal(OverwatchAuthorityClass.Denylist, c);
    }

    [Fact]
    public void FileEdit_ActionPromptBody_IsDefault_NotAllowlistInV1()
    {
        // A persistent action.prompt.md edit is a v2 ALLOWLIST bet; in v1 it is unclassified → propose-only
        // (Default). It must NOT be silently applied (and it is NOT denylist — it is not the verdict surface).
        OverwatchAuthorityClass c = Classify(new OverwatchFixOp
        {
            Kind = OverwatchFixKind.FileEdit,
            TargetPath = Path.Combine(TaskDir, "action.prompt.md")
        });
        Assert.Equal(OverwatchAuthorityClass.Default, c);
    }

    [Fact]
    public void FileEdit_OrdinarySourceFile_IsDefault()
    {
        OverwatchAuthorityClass c = Classify(new OverwatchFixOp
        {
            Kind = OverwatchFixKind.FileEdit,
            TargetPath = Path.Combine(PlanDir, "..", "src", "Foo.cs")
        });
        Assert.Equal(OverwatchAuthorityClass.Default, c);
    }
}
