using System.CommandLine;
using Guardrails.Cli;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #460 — <c>validate</c> printed its full check-set banner over a report the semantic half never
/// contributed to.
///
/// <para>
/// <c>PlanProbe.LoadAndValidate</c> runs <c>PlanValidator</c> only when loading produced no errors, so a
/// config with a loader-level defect AND a validator-level one reports the loader half alone. The author
/// fixes it, re-runs, and is told about the next problem — a serial reveal where one report was possible.
/// The banner underneath said which checks EXIST, and the natural reading of "here are the problems, and
/// here is the check set that found them" is that the set ran.
/// </para>
///
/// <para>
/// <b>What this fixes, and what it deliberately does not.</b> Merging both diagnostic sets across the
/// boundary is what the filing asks for eventually, and it is not free: a block whose <c>routing</c>
/// failed to parse comes back with an empty <c>tiers</c> list, and a validator run over it emits a
/// spurious "unservable tier" the author cannot act on. Cascading false diagnostics is the exact failure
/// mode this cluster of issues is about, so the boundary stays and the REPORT stops implying it is not
/// there — the filing's own "honest alternative".
/// </para>
///
/// <para>
/// Both tests drive the REAL <c>validate</c> verb through the production command factory over a real plan
/// folder on disk. Asserting on <c>PlanProbe</c> alone would pass while the operator-visible output — the
/// entire subject of the issue — stayed silent.
/// </para>
/// </summary>
[Trait("Category", "ScanSoundness")]
public sealed class ValidateSemanticHalfDisclosureTests
{
    private const string Marker = "semantic validation did NOT run";

    /// <summary>A loading error suppresses the semantic half, so the report must say so.</summary>
    [Fact]
    public async Task WhenLoadingErrorsSuppressTheSemanticHalf_TheReportSaysSo()
    {
        using var plan = new ProbePlan(wellFormedRunnerKind: false);

        string output = await ValidateAsync(plan.Dir);

        Assert.Contains(Marker, output, StringComparison.Ordinal);
        Assert.Contains("there may be more problems this pass could not see", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The polarity control, and the one that keeps the fix honest: on a plan that loads cleanly the
    /// semantic half DID run, so the note must be absent. Without this a fix could print the caveat
    /// unconditionally and "pass" — trading a false clean bill of health for a permanent false alarm,
    /// which is the same defect wearing the other sign.
    /// </summary>
    [Fact]
    public async Task WhenThePlanLoadsCleanly_NoSuchNoteIsPrinted()
    {
        using var plan = new ProbePlan(wellFormedRunnerKind: true);

        string output = await ValidateAsync(plan.Dir);

        Assert.DoesNotContain(Marker, output, StringComparison.Ordinal);
    }

    private static async Task<string> ValidateAsync(string folder)
    {
        var io = new StringConsoleIo();
        RootCommand root = CommandFactory.BuildRootCommand(io);
        await root.Parse(["validate", folder]).InvokeAsync();
        return io.OutText;
    }

    /// <summary>
    /// A minimal real plan. Its ONE variable is the prompt-runner <c>kind</c>: an unrecognised value is a
    /// LOADER-level error (GR2044), which is precisely what suppresses the semantic pass.
    /// </summary>
    private sealed class ProbePlan : IDisposable
    {
        private static readonly bool Ps = OperatingSystem.IsWindows();
        private static readonly string Ext = Ps ? ".ps1" : ".sh";

        public string Dir { get; }

        public ProbePlan(bool wellFormedRunnerKind)
        {
            Dir = Path.Combine(Path.GetTempPath(), "gr460-" + Guid.NewGuid().ToString("N"));
            string taskDir = Path.Combine(Dir, "tasks", "01-only");
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            string kind = wellFormedRunnerKind ? "claude" : "NOT-A-REAL-KIND";
            File.WriteAllText(Path.Combine(Dir, "guardrails.json"),
                $$"""
                {
                  "version": 1,
                  "workspace": ".",
                  "promptRunners": {
                    "default": "x",
                    "x": { "command": "claude", "kind": "{{kind}}" }
                  }
                }
                """);

            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                """
                {
                  "description": "the only task",
                  "dependsOn": [],
                  "writeScope": []
                }
                """);

            WriteScript(Path.Combine(taskDir, "action" + Ext), Ps ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");
            WriteScript(Path.Combine(taskDir, "guardrails", "01-check" + Ext),
                Ps ? "# catches: nothing\nexit 0\n" : "#!/usr/bin/env bash\n# catches: nothing\nexit 0\n");
        }

        private static void WriteScript(string path, string content)
        {
            File.WriteAllText(path, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); }
            catch (IOException) { }
        }
    }
}
