using Guardrails.Core.Prompts;

namespace Guardrails.Core.Telemetry;

/// <summary>
/// The model identity the telemetry report stratifies on (charter §4, SSOT §15.5) — one definition, read by
/// <c>guardrails telemetry report</c>.
/// </summary>
public static class TelemetryFingerprint
{
    /// <summary>What a row with no route at all (a script attempt) fingerprints as.</summary>
    public const string NoRouteRecorded = "(no route recorded)";

    /// <summary>
    /// The strongest model identity the row carries: the resolved route <c>kind/runner/model</c>, plus
    /// <c>@</c><see cref="TelemetryRow.ModelDigest"/> when the row carries one, plus — for a claude GATEWAY row (#782)
    /// — <c> via gateway &lt;endpoint&gt; backend &lt;backendModel&gt;</c>. The gateway suffix is what keeps a
    /// local-model row out of a real Claude stratum even when the gateway deliberately maps a Claude model name: the
    /// route alone cannot tell them apart. The endpoint is <see cref="ClaudeGatewayConfig.EndpointKey"/> (loopback
    /// spellings folded, trailing <c>/</c> dropped), and a missing backend reads <c>unverified</c>. A row with no
    /// gateway fingerprints exactly as before, so no existing stratum moves. A component the row left null is spelled
    /// <c>?</c> rather than silently collapsed.
    /// </summary>
    public static string Of(TelemetryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Kind is null && row.Runner is null && row.Model is null && row.Gateway is null)
        {
            return NoRouteRecorded;
        }

        string route = $"{row.Kind ?? "?"}/{row.Runner ?? "?"}/{row.Model ?? "?"}";
        if (row.ModelDigest is { } digest)
        {
            route = $"{route}@{digest}";
        }

        return row.Gateway is { } gateway
            ? $"{route} via gateway {ClaudeGatewayConfig.EndpointKey(gateway)} backend {row.BackendModel ?? ClaudeGatewayConfig.UnverifiedBackend}"
            : route;
    }
}
