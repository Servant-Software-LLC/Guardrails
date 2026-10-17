using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #835: a delivery refused because an operator link shadows a merge path is a STRUCTURED outcome. Its journal
/// token is what <c>run.json</c> (<c>delivery.outcome</c>) and a wave's <c>delivered.outcome</c> carry, so a consumer
/// keys on the token, never on the refusal's prose (the #742 lesson).
/// </summary>
public sealed class LinkShadowedOutcomeTests
{
    [Fact]
    public void TheOutcome_HasItsOwnKebabToken_AndRoundTrips()
    {
        Assert.Equal("link-shadowed", JournalJson.DeliveryOutcomeToken(DeliveryOutcome.LinkShadowed));

        string json = JsonSerializer.Serialize(DeliveryOutcome.LinkShadowed, JournalJson.Options);
        Assert.Equal("\"link-shadowed\"", json);
        Assert.Equal(DeliveryOutcome.LinkShadowed, JsonSerializer.Deserialize<DeliveryOutcome>(json, JournalJson.Options));
    }

    [Fact]
    public void EveryDeliveryOutcome_HasADistinctToken_ThatRoundTrips()
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (DeliveryOutcome outcome in Enum.GetValues<DeliveryOutcome>())
        {
            string token = JournalJson.DeliveryOutcomeToken(outcome);
            Assert.True(tokens.Add(token), $"duplicate token {token}");
            Assert.Equal(outcome, JsonSerializer.Deserialize<DeliveryOutcome>($"\"{token}\"", JournalJson.Options));
        }
    }

    [Fact]
    public void TheMergeResult_IsAppendedToTheEnum_SoNoExistingValueIsRenumbered() =>
        Assert.Equal(
            MergeOnSuccessResult.LinkShadowed,
            Enum.GetValues<MergeOnSuccessResult>().Max());
}
