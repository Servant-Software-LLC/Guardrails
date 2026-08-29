using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Guardrails.Core.State;

namespace Guardrails.Core.Breakdown;

/// <summary>
/// Provenance captured from the source <c>plan.md</c> at breakdown time (issue #505, plan-of-record
/// <c>docs/plans/24-plan-source-provenance.md</c> §3). Written to <c>&lt;plan&gt;/state/plan-source.json</c>
/// — deliberately under <c>state/</c>, which every hash in <see cref="Journal.PlanHash"/> and
/// <see cref="Journal.PlanDefinitionHash"/> excludes, so recording this can never de-attest an
/// already-reviewed plan (GR2025).
/// </summary>
public sealed record PlanSourceRecord
{
    private const string Sha256Prefix = "sha256:";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Matches a <c>&lt;!-- charter: key=value --&gt;</c> stamp comment on a single line.</summary>
    private static readonly Regex StampPattern = new(
        @"<!--\s*charter:\s*(?<key>[^=]+?)\s*=\s*(?<value>.+?)\s*-->",
        RegexOptions.Compiled);

    /// <summary>Matches Charter's <c>DECISIONS DELEGATED TO YOU: (\d+)**</c> count line.</summary>
    private static readonly Regex DeclaredDecisionsPattern = new(
        @"DECISIONS DELEGATED TO YOU:\s*(?<count>\d+)\*\*",
        RegexOptions.Compiled);

    /// <summary>The plan-relative path of the source markdown that was captured.</summary>
    public required string SourcePath { get; init; }

    /// <summary>The byte length actually read from <see cref="SourcePath"/>.</summary>
    public required int SourceBytes { get; init; }

    /// <summary>
    /// <c>sha256:&lt;hex&gt;</c> over the file's raw bytes, exactly as read — never decoded text, so a
    /// UTF-8 BOM or any other encoding round-trip changes it.
    /// </summary>
    public required string SourceSha256 { get; init; }

    /// <summary>
    /// <c>sha256:&lt;hex&gt;</c> over the same bytes with CRLF and a lone CR normalized to LF, so a
    /// line-ending-only checkout difference (e.g. <c>core.autocrlf</c>) does not read as tampering.
    /// </summary>
    public required string SourceSha256Lf { get; init; }

    /// <summary>
    /// The integer from a <c>DECISIONS DELEGATED TO YOU: (\d+)**</c> line, or 0 when the line is absent
    /// — Charter emits the line whenever the count is &gt;= 1 and never when it is 0.
    /// </summary>
    public required int DeclaredDelegatedDecisions { get; init; }

    /// <summary>
    /// An OPEN map of every <c>&lt;!-- charter: key=value --&gt;</c> comment found, keyed by <c>key</c>.
    /// Empty (never null) when the plan carries none.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Stamps { get; init; }

    /// <summary>
    /// Keys for which more than one <c>charter:</c> comment was found. <see cref="Stamps"/> keeps the
    /// FIRST value seen for such a key; this reports that a duplicate existed at all.
    /// </summary>
    public required IReadOnlyList<string> DuplicateStampKeys { get; init; }

    /// <summary>Read <paramref name="planPath"/> and capture its provenance.</summary>
    public static PlanSourceRecord Capture(string planPath)
    {
        byte[] bytes = File.ReadAllBytes(planPath);
        byte[] lfBytes = NormalizeToLf(bytes);
        string text = Encoding.UTF8.GetString(bytes);

        var stamps = new Dictionary<string, string>();
        var duplicateStampKeys = new List<string>();
        foreach (Match match in StampPattern.Matches(text))
        {
            string key = match.Groups["key"].Value;
            string value = match.Groups["value"].Value;
            if (!stamps.TryAdd(key, value) && !duplicateStampKeys.Contains(key))
            {
                duplicateStampKeys.Add(key);
            }
        }

        Match decisionsMatch = DeclaredDecisionsPattern.Match(text);
        int declaredDelegatedDecisions = decisionsMatch.Success
            ? int.Parse(decisionsMatch.Groups["count"].Value)
            : 0;

        return new PlanSourceRecord
        {
            SourcePath = planPath,
            SourceBytes = bytes.Length,
            SourceSha256 = ComputeHash(bytes),
            SourceSha256Lf = ComputeHash(lfBytes),
            DeclaredDelegatedDecisions = declaredDelegatedDecisions,
            Stamps = stamps,
            DuplicateStampKeys = duplicateStampKeys
        };
    }

    /// <summary>
    /// Serialize this record as JSON and write it to <c>&lt;planDirectory&gt;/state/plan-source.json</c>.
    /// Lives under <c>state/</c> so no hash in <see cref="Journal.PlanHash"/> or
    /// <see cref="Journal.PlanDefinitionHash"/> ever walks it.
    /// </summary>
    public void WriteTo(string planDirectory)
    {
        string path = Path.Combine(planDirectory, "state", "plan-source.json");
        string json = JsonSerializer.Serialize(this, WriteOptions);
        AtomicFile.WriteAllText(path, json + "\n");
    }

    private static string ComputeHash(byte[] bytes) =>
        Sha256Prefix + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Collapse CRLF and lone CR to LF at the byte level (matching <c>BreakdownManifest</c>'s
    /// normalization) so a line-ending-only checkout difference does not read as tampering.
    /// </summary>
    private static byte[] NormalizeToLf(byte[] bytes)
    {
        const byte Cr = 0x0D;
        const byte Lf = 0x0A;

        var output = new byte[bytes.Length];
        int length = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == Cr)
            {
                output[length++] = Lf;
                if (i + 1 < bytes.Length && bytes[i + 1] == Lf)
                {
                    i++;
                }
            }
            else
            {
                output[length++] = bytes[i];
            }
        }

        return length == output.Length ? output : output[..length];
    }
}
