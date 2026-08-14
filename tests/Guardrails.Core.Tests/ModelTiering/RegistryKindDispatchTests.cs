using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// #224 / charter §A item 2 — <see cref="PromptRunnerRegistry.FromConfig"/> dispatches on the runner
/// block's <c>kind</c>. Only <c>claude</c> is real in this stage; <c>codex</c>, <c>openrouter</c> and
/// <c>local</c> are #223 and must FAIL registry construction with a message naming the kind, and must
/// NEVER silently fall back to Claude — a fallback spends a real run against a model the config did
/// not ask for, which is the failure the plan calls out by name.
///
/// Three deliberate choices about HOW this is asserted:
///
/// <list type="bullet">
/// <item>The kind travels as a <c>guardrails.json</c> field, not a hand-built
/// <see cref="PromptRunnerConfig"/>, so the test exercises the same path a user's config takes.</item>
/// <item>Every runner block is keyed <c>primary</c> with a neutral <c>command</c> — never
/// <c>claude</c>/<c>codex</c>. Dispatch must follow the <c>kind</c> FIELD; a registry that keyed off
/// the map name or the command would pass a fixture that spelled the provider there.</item>
/// <item>The failure contract is <see cref="InvalidOperationException"/> (or a subclass) — the
/// registry's existing convention for "this config cannot be served", and deliberately NOT the
/// <see cref="NotImplementedException"/> a stub throws, so a stub cannot make these tests green.</item>
/// </list>
/// </summary>
[Trait("Category", "ModelTieringStage1")]
public sealed class RegistryKindDispatchTests : IDisposable
{
    /// <summary>The runner block's NAME — deliberately not a provider name (see the class remarks).</summary>
    private const string RunnerName = "primary";

    private readonly string _root;

    public RegistryKindDispatchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-kind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void KindClaude_BuildsAClaudePromptRunner()
    {
        RunConfig config = ConfigWithKind("claude");

        PromptRunnerRegistry registry = PromptRunnerRegistry.FromConfig(config, new ProcessRunner());

        // The CONCRETE type, not merely "a runner came back": once #223 makes more kinds real, an
        // inverted kind→class pairing is exactly what a weaker assertion would wave through.
        Assert.IsType<ClaudePromptRunner>(registry.Resolve(RunnerName));
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("openrouter")]
    [InlineData("local")]
    public void UnimplementedKind_FailsRegistryConstruction_NamingTheKind(string kind)
    {
        RunConfig config = ConfigWithKind(kind);

        InvalidOperationException failure = Assert.ThrowsAny<InvalidOperationException>(
            () => PromptRunnerRegistry.FromConfig(config, new ProcessRunner()));

        // "Actionable" = the message names the kind that has no implementation, so the human edits the
        // offending block instead of reading a stack trace to find out which provider was refused.
        Assert.Contains(kind, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnimplementedKind_NeverSilentlyFallsBackToClaude()
    {
        RunConfig config = ConfigWithKind("codex");

        PromptRunnerRegistry? registry = null;
        Exception? failure = Record.Exception(
            () => registry = PromptRunnerRegistry.FromConfig(config, new ProcessRunner()));

        // Check the WRONG answer first. If construction ever stops throwing, the one outcome the plan
        // forbids by name is a Claude runner standing in for the kind that was asked for — asserting it
        // here makes the failure read "fell back to Claude" rather than the far less useful
        // "expected an exception".
        if (failure is null)
        {
            Assert.NotNull(registry);
            Assert.IsNotType<ClaudePromptRunner>(registry!.Resolve(RunnerName));
        }

        Assert.NotNull(failure);
    }

    [Fact]
    public void NoKind_StillBuildsClaude_Unchanged()
    {
        RunConfig config = ConfigWithKind(kind: null);

        PromptRunnerRegistry registry = PromptRunnerRegistry.FromConfig(config, new ProcessRunner());

        // The additive guarantee: every config written before `kind` existed keeps building Claude, and
        // keeps resolving the same default.
        Assert.IsType<ClaudePromptRunner>(registry.Resolve(RunnerName));
        Assert.Equal(RunnerName, registry.DefaultRunnerName);
    }

    /// <summary>
    /// A one-prompt-task plan whose single runner block carries <paramref name="kind"/> (the field is
    /// omitted entirely when null), loaded into the <see cref="RunConfig"/> the registry is built from.
    /// </summary>
    private RunConfig ConfigWithKind(string? kind)
    {
        string kindField = kind is null ? string.Empty : $", \"kind\": \"{kind}\"";
        string json =
            $$"""
            {
              "version": 1,
              "promptRunners": {
                "default": "{{RunnerName}}",
                "{{RunnerName}}": { "command": "cli-under-test"{{kindField}} }
              }
            }
            """;

        File.WriteAllText(Path.Combine(_root, "guardrails.json"), json);

        string taskDir = Path.Combine(_root, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "writeScope": [], "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "exit 0\n");

        PlanLoadResult result = new PlanLoader().Load(_root);

        // A recognized-but-unimplemented kind is a REGISTRY-construction failure, not a load failure —
        // the plan's "backstop, not gate" wording. If loading ever starts rejecting it, this fixture
        // premise is what tells us, instead of a confusing null-plan crash below.
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.NotNull(result.Plan);

        return result.Plan!.Config;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}
