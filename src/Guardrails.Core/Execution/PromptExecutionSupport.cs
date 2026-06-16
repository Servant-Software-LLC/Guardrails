using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// Shared prompt-execution helpers used by both the prompt ACTION path
/// (<see cref="ActionRunner"/>) and the prompt GUARDRAIL path (<see cref="GuardrailRunner"/>):
/// resolving the runner registry, loading/parsing a <c>*.prompt.md</c> file, and applying a
/// task/frontmatter <c>maxTurns</c> override over the runner-config settings. Extracted so the
/// two prompt callers do not each carry their own copy.
/// </summary>
internal sealed class PromptExecutionSupport
{
    private readonly PromptRunnerRegistry? _promptRunners;

    public PromptExecutionSupport(PromptRunnerRegistry? promptRunners) => _promptRunners = promptRunners;

    public PromptRunnerRegistry RequireRegistry() =>
        _promptRunners ?? throw new InvalidOperationException(
            "This plan has prompt actions/guardrails but no prompt-runner registry was provided to the executor.");

    /// <summary>
    /// Load and parse a <c>*.prompt.md</c> file. Loading-time validation (GR10xx) should have
    /// caught malformed frontmatter, but if parsing fails here we fall back to the raw text as
    /// the body so the run surfaces a real prompt result rather than crashing.
    /// </summary>
    public static PromptFile LoadPromptFile(string path)
    {
        string content = File.ReadAllText(path);
        PromptParseResult parsed = PromptFileParser.Parse(content);
        return parsed.File ?? new PromptFile { Frontmatter = PromptFrontmatter.Empty, Body = content };
    }

    /// <summary>Apply a task/frontmatter <c>maxTurns</c> override over the runner-config settings.</summary>
    public static PromptRunnerSettings ApplyPromptOverrides(PromptRunnerSettings settings, int? maxTurns) =>
        maxTurns is { } turns ? settings with { MaxTurns = turns } : settings;
}
