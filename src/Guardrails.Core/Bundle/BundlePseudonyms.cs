namespace Guardrails.Core.Bundle;

/// <summary>
/// The identifier pseudonyms of one bundle (#812, SSOT §17.6.2): each distinct identifier value gets the next token,
/// <c>[id-1]</c>, <c>[id-2]</c>, …, in the order the bundle first meets it. The builder redacts files in a fixed order, so
/// the numbering is a function of the inputs alone (§17.10). The table lives in memory only and is never written.
/// Identifiers are not secrets: the same value may still appear raw where it is not keyed (a session file name, a
/// command line), so a token is a readability aid, not an irreversibility guarantee.
/// </summary>
public sealed class BundlePseudonyms
{
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);

    /// <summary>How many distinct identifiers have been pseudonymized.</summary>
    public int Count => _tokens.Count;

    /// <summary>The token for <paramref name="value"/>, assigning the next one on first sight.</summary>
    public string TokenFor(string value)
    {
        if (!_tokens.TryGetValue(value, out string? token))
        {
            token = $"[id-{_tokens.Count + 1}]";
            _tokens[value] = token;
        }

        return token;
    }
}
