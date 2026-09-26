using System.Globalization;

namespace Guardrails.Core.Journal;

/// <summary>
/// How spend RENDERS when some of it went through a claude gateway (#782 §4, the maintainer's <c>gateway-cost</c>
/// answer): a gateway dispatch has no honest dollar figure, so its TOKEN usage stands in for cost on every surface
/// that shows cost — never a blank, never <c>$0.00</c> (which would read as "free"). Rendering only: <c>run.json</c>
/// and telemetry keep <c>CostUsd: null</c> plus the raw token counts, and the <c>k</c>/<c>M</c> abbreviation is never
/// stored.
/// </summary>
public static class SpendFormat
{
    /// <summary>
    /// A token count as usage: the exact count below a thousand (<c>"640 tok"</c>), one decimal of thousands up to
    /// 999.9k (<c>"48.2k tok"</c>), and one decimal of millions above that (<c>"1.3M tok"</c>). Invariant culture.
    /// </summary>
    public static string Tokens(long tokens)
    {
        if (tokens < 1_000)
        {
            return tokens.ToString(CultureInfo.InvariantCulture) + " tok";
        }

        decimal thousands = Math.Round(tokens / 1_000m, 1, MidpointRounding.AwayFromZero);
        if (thousands < 1_000m)
        {
            return thousands.ToString("0.0", CultureInfo.InvariantCulture) + "k tok";
        }

        decimal millions = Math.Round(tokens / 1_000_000m, 1, MidpointRounding.AwayFromZero);
        return millions.ToString("0.0", CultureInfo.InvariantCulture) + "M tok";
    }

    /// <summary>
    /// A total that keeps the two units APART: <c>"$1.8400 + 310.5k tok (gateway)"</c> for a mixed run, the dollar
    /// figure alone when nothing went through a gateway, <c>"310.5k tok (gateway)"</c> when everything did, and null
    /// when there is nothing to report at all. Never a dollar figure that silently omits the gateway spend.
    /// </summary>
    /// <param name="costUsd">The summed reported cost, or null when none was reported.</param>
    /// <param name="gatewayTokens">The summed gateway token usage, or null when no gateway dispatch reported any.</param>
    /// <param name="costFormat">The dollar format of the surface (<c>F4</c> for the run and status totals).</param>
    public static string? Total(decimal? costUsd, long? gatewayTokens, string costFormat = "F4")
    {
        string? money = costUsd is { } cost ? "$" + cost.ToString(costFormat, CultureInfo.InvariantCulture) : null;
        string? tokens = gatewayTokens is { } count ? Tokens(count) + " (gateway)" : null;

        return (money, tokens) switch
        {
            ({ } m, { } t) => $"{m} + {t}",
            ({ } m, null) => m,
            (null, { } t) => t,
            _ => null
        };
    }
}
