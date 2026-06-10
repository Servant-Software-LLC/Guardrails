using System.Text.Json;

namespace Guardrails.Core.Journal;

/// <summary>
/// Read-only access to a <c>run.json</c> on disk, without the resume normalization that
/// <see cref="RunJournal.LoadOrCreate"/> applies. Used by <c>guardrails status</c>, which
/// must report the journal exactly as it stands (e.g. a crashed task still shows
/// <c>running</c> until the next <c>run</c> normalizes it).
/// </summary>
public static class JournalReader
{
    /// <summary>Deserialize the journal document at <paramref name="journalPath"/>.</summary>
    public static JournalDocument Read(string journalPath)
    {
        string text = File.ReadAllText(journalPath);
        return JsonSerializer.Deserialize<JournalDocument>(text, JournalJson.Options)
            ?? throw new JsonException($"run.json at '{journalPath}' deserialized to null.");
    }
}
