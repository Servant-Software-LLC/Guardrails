using System.Diagnostics;
using System.Text;

namespace Guardrails.Cli;

/// <summary>
/// One documented Claude Code managed-settings source (#782 review, sec W4) the gateway preflight reads. <see cref="Read"/>
/// returns the JSON documents the source currently holds (empty when the source is absent — an absent source is never a
/// failure), and THROWS when the source exists but cannot be read, which the preflight turns into a halt.
/// </summary>
public sealed class ManagedSettingsSource
{
    private readonly Func<IReadOnlyList<(string Label, string Text)>> _read;

    private ManagedSettingsSource(string label, Func<IReadOnlyList<(string Label, string Text)>> read)
    {
        Label = label;
        _read = read;
    }

    /// <summary>Where the source lives, as the halt names it.</summary>
    public string Label { get; }

    /// <summary>The source's current JSON documents, each with its own label.</summary>
    public IReadOnlyList<(string Label, string Text)> Read() => _read();

    /// <summary>A source defined by a delegate — for tests, and for a source the harness learns to read later.</summary>
    public static ManagedSettingsSource Custom(string label, Func<IReadOnlyList<(string Label, string Text)>> read) =>
        new(label, read);

    /// <summary>A <c>managed-settings.json</c>-shaped file.</summary>
    public static ManagedSettingsSource File(string path) => new(path, () =>
        System.IO.File.Exists(path) ? [(path, System.IO.File.ReadAllText(path))] : []);

    /// <summary>A <c>managed-settings.d/</c> drop-in directory: each <c>*.json</c> in it, in ordinal order.</summary>
    public static ManagedSettingsSource DropInDirectory(string directory) => new(directory, () =>
        Directory.Exists(directory)
            ? [.. Directory.GetFiles(directory, "*.json")
                .Order(StringComparer.Ordinal)
                .Select(file => (file, System.IO.File.ReadAllText(file)))]
            : []);

    /// <summary>
    /// A macOS managed-preferences plist (the <c>com.anthropic.claudecode</c> domain), converted to JSON with
    /// <c>plutil -convert json -o - &lt;path&gt;</c> — exactly how Claude Code documents reading it. A plist <c>plutil</c>
    /// cannot convert is returned as unparseable text so the preflight halts on it, as Claude Code refuses to start on it.
    /// </summary>
    public static ManagedSettingsSource Plist(string path) => new(path, () =>
    {
        if (!System.IO.File.Exists(path))
        {
            return [];
        }

        var start = new ProcessStartInfo("plutil")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        foreach (string argument in new[] { "-convert", "json", "-o", "-", path })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("plutil could not be started");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("plutil did not finish within 15s");
        }

        return process.ExitCode == 0
            ? [(path, stdout.GetAwaiter().GetResult())]
            : [(path, "plutil could not convert it: " + stderr.GetAwaiter().GetResult().Trim())];
    });

    /// <summary>
    /// The Windows <c>Settings</c> value (a JSON <c>REG_SZ</c>/<c>REG_EXPAND_SZ</c>) under
    /// <c>&lt;hive&gt;\SOFTWARE\Policies\ClaudeCode</c>, for <paramref name="hive"/> <c>HKLM</c> or <c>HKCU</c>. A value
    /// that is not a string is returned as unparseable text so the preflight halts on it.
    /// </summary>
    public static ManagedSettingsSource Registry(string hive) => new($@"{hive}\SOFTWARE\Policies\ClaudeCode\Settings", () =>
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        using Microsoft.Win32.RegistryKey? root = hive == "HKLM"
            ? Microsoft.Win32.Registry.LocalMachine
            : Microsoft.Win32.Registry.CurrentUser;
        using Microsoft.Win32.RegistryKey? key = root.OpenSubKey(@"SOFTWARE\Policies\ClaudeCode");
        object? value = key?.GetValue("Settings");
        string label = $@"{hive}\SOFTWARE\Policies\ClaudeCode\Settings";
        return value switch
        {
            null => [],
            string text => [(label, text)],
            _ => [(label, $"the value is a {value.GetType().Name}, not a JSON string")]
        };
    });
}
