namespace Guardrails.Core.Prompts;

/// <summary>
/// The pure half of the #782 §3.2 backend-identity check (D2), kept out of the CLI's preflight so every rule is
/// unit-testable: normalizing LiteLLM's <c>api_base</c>, the declared-<c>backendModel</c> match rule, and the
/// identity string provenance records.
/// </summary>
public static class ClaudeGatewayBackendIdentity
{
    /// <summary>
    /// LiteLLM's <c>api_base</c> for a <c>llama-server</c> is typically <c>http://127.0.0.1:8080/v1</c>: strip a
    /// trailing <c>/</c>, then a trailing <c>/v1</c>, so the backend's own <c>/props</c> and <c>/v1/models</c> are
    /// reachable (without this both would 404 and the identity would always read "unverified").
    /// </summary>
    public static string NormalizeApiBase(string apiBase)
    {
        string normalized = apiBase.Trim().TrimEnd('/');
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^3].TrimEnd('/');
        }

        return normalized;
    }

    /// <summary>
    /// True when a reported "alias" is really a file path — <c>llama-server</c> started without <c>--alias</c> reports
    /// its model path as the model id. Such a value is matched under rule (b), as a <c>model_path</c>, never (a).
    /// </summary>
    public static bool LooksLikePath(string value) =>
        value.Contains('/', StringComparison.Ordinal)
        || value.Contains('\\', StringComparison.Ordinal)
        || value.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file name of <paramref name="modelPath"/> without its directory, whichever separator it uses.</summary>
    public static string Basename(string modelPath)
    {
        string unified = modelPath.Trim().Replace('\\', '/').TrimEnd('/');
        int slash = unified.LastIndexOf('/');
        return slash < 0 ? unified : unified[(slash + 1)..];
    }

    /// <summary>
    /// The §3.2 match rule. The declared value matches when (a) it equals the backend's model ALIAS,
    /// case-insensitively; or (b) there is no alias and it is a case-insensitive SUBSTRING of the basename of
    /// <c>model_path</c>. With neither reported, nothing matches — a claim needs evidence.
    /// </summary>
    public static bool Matches(string declared, string? alias, string? modelPath)
    {
        string wanted = declared.Trim();
        if (wanted.Length == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(alias))
        {
            return string.Equals(wanted, alias.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(modelPath)
               && Basename(modelPath).Contains(wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The identity provenance records (§3.2 step 4): the normalized backend base and the loaded model id — the
    /// alias when there is one, else the <c>model_path</c> basename.
    /// </summary>
    public static string Describe(string normalizedBase, string? alias, string? modelPath) =>
        $"{normalizedBase} {(string.IsNullOrWhiteSpace(alias) ? Basename(modelPath ?? "?") : alias.Trim())}";
}
