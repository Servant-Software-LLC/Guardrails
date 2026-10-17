using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Where a model string that reaches a claude GATEWAY block came from (#782).
/// </summary>
public enum ClaudeGatewayModelSource
{
    /// <summary>The block's own <c>model</c>.</summary>
    BlockModel,

    /// <summary>The block's <c>guardrailOverrides.model</c>.</summary>
    OverrideModel,

    /// <summary>A task's <c>action.model</c> pin dispatched to the block.</summary>
    ActionPin,

    /// <summary><c>--model</c> / <c>--fallback-model</c> in the block's <c>extraArgs</c> or <c>guardrailOverrides.extraArgs</c> (GR2084).</summary>
    ExtraArgs
}

/// <summary>One (gateway block, model) pair a run can dispatch, and where the model string came from.</summary>
/// <param name="Block">The gateway block the model reaches.</param>
/// <param name="Model">The model string, verbatim.</param>
/// <param name="Source">The kind of source.</param>
/// <param name="Where">The human-readable location, e.g. <c>promptRunners.qwen.model</c> or <c>tasks/01/task.json action.model</c>.</param>
public sealed record ClaudeGatewayModelReach(
    PromptRunnerConfig Block, string Model, ClaudeGatewayModelSource Source, string Where);

/// <summary>
/// THE reach set (#782 §2/§3.2): every model string that can reach each claude gateway block. ONE definition, read
/// by the validator (GR2084 / GR2085 / GR2086) and by the pre-DAG preflight — which probes and resolves the backend
/// identity of EVERY pair here, so the #760 shared-identity halt sees a task's <c>action.model</c> pin as well as the
/// block's own model, and the runner's provenance never records an identity nobody checked.
/// </summary>
public static class ClaudeGatewayReach
{
    /// <summary>
    /// Every (gateway block, model) pair, in a stable order: blocks by name (model, override model, extraArgs flags),
    /// then task pins by task id. Duplicates are kept — each carries its own <see cref="ClaudeGatewayModelReach.Where"/>
    /// for diagnostics; a caller that probes de-duplicates on (gateway, model).
    /// </summary>
    public static IReadOnlyList<ClaudeGatewayModelReach> Of(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Dictionary<string, PromptRunnerConfig> gateways = plan.Config.PromptRunners.Values
            .Where(b => b.IsClaudeGateway)
            .ToDictionary(b => b.Name, StringComparer.Ordinal);
        var reach = new List<ClaudeGatewayModelReach>();
        if (gateways.Count == 0)
        {
            return reach;
        }

        void Add(PromptRunnerConfig block, string? model, ClaudeGatewayModelSource source, string where)
        {
            if (!string.IsNullOrWhiteSpace(model))
            {
                reach.Add(new ClaudeGatewayModelReach(block, model, source, where));
            }
        }

        foreach (PromptRunnerConfig block in gateways.Values.OrderBy(b => b.Name, StringComparer.Ordinal))
        {
            string where = $"promptRunners.{block.Name}";
            Add(block, block.Settings.Model, ClaudeGatewayModelSource.BlockModel, $"{where}.model");
            Add(block, block.GuardrailOverrides?.Model, ClaudeGatewayModelSource.OverrideModel, $"{where}.guardrailOverrides.model");

            foreach (string model in ModelFlagValues(block.Settings.ExtraArgs))
            {
                Add(block, model, ClaudeGatewayModelSource.ExtraArgs, $"{where}.extraArgs");
            }

            if (block.GuardrailOverrides?.ExtraArgs is { } overrideArgs)
            {
                foreach (string model in ModelFlagValues(overrideArgs))
                {
                    Add(block, model, ClaudeGatewayModelSource.ExtraArgs, $"{where}.guardrailOverrides.extraArgs");
                }
            }
        }

        // An action.model pin overrides the model STRING on the block the action dispatches to (TierResolver §6.1
        // item 1: action.runner ?? the default), so a pin whose block is a gateway sends that string there. A
        // routing tier or a judge's runner pin resolves to a block's OWN model, which the loop above already read.
        string? defaultName = PromptRunnerRegistry.DefaultNameFor(plan.Config);
        foreach (TaskNode task in plan.Tasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            if (task.Action.Kind != ActionKind.Prompt || task.Action.Model is not { } pinned)
            {
                continue;
            }

            string? dispatch = task.Action.Runner ?? defaultName;
            if (dispatch is not null && gateways.TryGetValue(dispatch, out PromptRunnerConfig? target))
            {
                Add(target, pinned, ClaudeGatewayModelSource.ActionPin, $"tasks/{task.Id}/task.json action.model");
            }
        }

        return reach;
    }

    /// <summary>The values of <c>--model</c> / <c>--fallback-model</c>, in either spelling (<c>--flag v</c>, <c>--flag=v</c>).</summary>
    public static IEnumerable<string> ModelFlagValues(IReadOnlyList<string> args)
    {
        string[] flags = ["--model", "--fallback-model"];
        for (int i = 0; i < args.Count; i++)
        {
            foreach (string flag in flags)
            {
                if (string.Equals(args[i], flag, StringComparison.Ordinal) && i + 1 < args.Count)
                {
                    yield return args[i + 1];
                }
                else if (args[i].StartsWith(flag + "=", StringComparison.Ordinal))
                {
                    yield return args[i][(flag.Length + 1)..];
                }
            }
        }
    }
}
