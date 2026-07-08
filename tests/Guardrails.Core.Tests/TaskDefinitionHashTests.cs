using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="TaskDefinitionHash"/> (SSOT §7.2, issue #274 Part A): the per-task
/// definition hash is <c>sha256:</c>-prefixed, deterministic, newline-normalized (CRLF/LF hash the
/// same), and changes when ANY of the task's definition files (task.json, action, guardrails) change.
/// </summary>
public sealed class TaskDefinitionHashTests : IDisposable
{
    private readonly string _taskDir;

    public TaskDefinitionHashTests()
    {
        _taskDir = Path.Combine(Path.GetTempPath(), "gr-tdh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(_taskDir, "task.json"), "{ \"description\": \"t\", \"dependsOn\": [] }\n");
        File.WriteAllText(Path.Combine(_taskDir, "action.sh"), "#!/usr/bin/env bash\nexit 0\n");
        File.WriteAllText(Path.Combine(_taskDir, "guardrails", "01-check.sh"), "#!/usr/bin/env bash\nexit 0\n");
    }

    private TaskNode BuildTask() => new()
    {
        Id = "01-task",
        Directory = _taskDir,
        Description = "t",
        DependsOn = [],
        Action = new ActionDefinition { Path = Path.Combine(_taskDir, "action.sh"), Kind = ActionKind.Script },
        Guardrails =
        [
            new GuardrailDefinition
            {
                Name = "01-check",
                Path = Path.Combine(_taskDir, "guardrails", "01-check.sh"),
                Kind = ActionKind.Script
            }
        ]
    };

    [Fact]
    public void Compute_IsSha256Prefixed_AndDeterministic()
    {
        string a = TaskDefinitionHash.Compute(BuildTask());
        string b = TaskDefinitionHash.Compute(BuildTask());

        Assert.StartsWith("sha256:", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_ChangesWhenActionFileChanges()
    {
        string before = TaskDefinitionHash.Compute(BuildTask());
        File.WriteAllText(Path.Combine(_taskDir, "action.sh"), "#!/usr/bin/env bash\n# edited\nexit 0\n");
        string after = TaskDefinitionHash.Compute(BuildTask());

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Compute_ChangesWhenGuardrailFileChanges()
    {
        string before = TaskDefinitionHash.Compute(BuildTask());
        File.WriteAllText(Path.Combine(_taskDir, "guardrails", "01-check.sh"),
            "#!/usr/bin/env bash\necho changed\nexit 0\n");
        string after = TaskDefinitionHash.Compute(BuildTask());

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Compute_ChangesWhenTaskJsonChanges()
    {
        string before = TaskDefinitionHash.Compute(BuildTask());
        File.WriteAllText(Path.Combine(_taskDir, "task.json"), "{ \"description\": \"edited\", \"dependsOn\": [] }\n");
        string after = TaskDefinitionHash.Compute(BuildTask());

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Compute_NewlineNormalized_CrlfAndLfHashIdentically()
    {
        File.WriteAllText(Path.Combine(_taskDir, "action.sh"), "line1\nline2\n");
        string lf = TaskDefinitionHash.Compute(BuildTask());

        File.WriteAllText(Path.Combine(_taskDir, "action.sh"), "line1\r\nline2\r\n");
        string crlf = TaskDefinitionHash.Compute(BuildTask());

        Assert.Equal(lf, crlf);
    }

    public void Dispose()
    {
        try { Directory.Delete(_taskDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }
}
