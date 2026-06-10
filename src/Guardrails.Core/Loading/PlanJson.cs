using System.Text.Json;

namespace Guardrails.Core.Loading;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for every manifest read. Comments and
/// trailing commas are allowed because humans hand-edit these files (SSOT §0 intro;
/// the committed example uses <c>//</c> comments).
/// </summary>
public static class PlanJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };
}
