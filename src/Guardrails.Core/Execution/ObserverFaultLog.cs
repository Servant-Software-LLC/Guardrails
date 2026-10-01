using System.Globalization;
using System.Text;

namespace Guardrails.Core.Execution;

/// <summary>
/// The run-level record of observer faults (#803): <c>logs/&lt;runId&gt;/observer-faults.log</c>. Each
/// (observer, callback) pair is written in full — exception type, message and stack — the FIRST time it throws,
/// and the fault that disables an observer is always written, so a rendering bug that throws on every event
/// leaves a few lines here rather than thousands.
///
/// <para>Best-effort by design: this records a display fault, and a failure to record it (the same full disk that
/// broke the observer, say) must not become a run fault either. Nothing is written until the first fault, so a
/// clean run has no file.</para>
/// </summary>
public sealed class ObserverFaultLog
{
    /// <summary>The file name under the run's log directory.</summary>
    public const string FileName = "observer-faults.log";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly HashSet<(string Observer, string Callback)> _reported = [];

    /// <param name="runLogDirectory">The run's log directory, <c>logs/&lt;runId&gt;/</c>.</param>
    public ObserverFaultLog(string runLogDirectory)
    {
        _path = Path.Combine(runLogDirectory, FileName);
    }

    /// <summary>The log file's full path.</summary>
    public string FilePath => _path;

    /// <summary>Record <paramref name="fault"/>. Never throws.</summary>
    public void Record(ObserverFault fault)
    {
        try
        {
            string? text = Format(fault);
            if (text is null)
            {
                return;
            }

            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, text, Utf8NoBom);
            }
        }
        catch (Exception)
        {
            // Best-effort: see the type's remarks.
        }
    }

    private string? Format(ObserverFault fault)
    {
        bool first;
        lock (_gate)
        {
            first = _reported.Add((fault.Observer, fault.Callback));
        }

        if (!first && !fault.Disabled)
        {
            return null;
        }

        string at = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{at} observer '{fault.Observer}' threw from {fault.Callback} (fault {fault.FaultCount} of {FaultIsolatingObserver.MaxFaults}); the run continues.");
        text.AppendLine();
        if (first)
        {
            text.AppendLine(fault.Error.ToString());
        }

        if (fault.Disabled)
        {
            text.AppendLine(
                $"{at} observer '{fault.Observer}' is disabled for the rest of this run after {fault.FaultCount} faults.");
        }

        return text.ToString();
    }
}
