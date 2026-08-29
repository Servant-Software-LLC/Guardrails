using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Samples;

/// <summary>
/// Why a pair is unsound (plan of record 26, §2/§7). A pair asserts exactly two facts — the
/// <c>.valid</c> half's guardrail exits 0, the <c>.invalid</c> half's exits non-zero — and this enum
/// names every way that assertion can be false or unverifiable.
/// </summary>
public enum SampleFindingKind
{
    /// <summary>Only one of the two halves (<c>.valid</c>/<c>.invalid</c>) is committed.</summary>
    MissingHalf,

    /// <summary>A sample's base name matches no guardrail in its task — a stale, orphaned pair.</summary>
    OrphanSample,

    /// <summary>The <c>.valid</c> half exited non-zero — the guardrail rejects a correct artifact.</summary>
    ValidHalfFailed,

    /// <summary>The <c>.invalid</c> half exited 0 — the guardrail can never fail.</summary>
    InvalidHalfPassed,

    /// <summary>Both halves are wrong at once (<c>.valid</c> non-zero AND <c>.invalid</c> zero) — one finding, not two.</summary>
    ReversedPolarity,

    /// <summary>The matched guardrail cannot be executed deterministically (e.g. a prompt judge).</summary>
    Unverifiable
}

/// <summary>One problem found while verifying a sample pair against its guardrail (SSOT/plan 26).</summary>
public sealed record SampleFinding
{
    public required SampleFindingKind Kind { get; init; }

    /// <summary>Absolute path to the matched guardrail file; null only for <see cref="SampleFindingKind.OrphanSample"/>.</summary>
    public string? GuardrailPath { get; init; }

    /// <summary>Absolute path to the sample half this finding is about.</summary>
    public required string SamplePath { get; init; }

    /// <summary>The guardrail's observed exit code for <see cref="SamplePath"/>, when a process actually ran.</summary>
    public int? ObservedExitCode { get; init; }

    /// <summary>Human-actionable message naming the guardrail path, the sample path, and the observed exit code.</summary>
    public required string Message { get; init; }
}

/// <summary>The outcome of verifying every committed sample pair in a plan (SSOT/plan 26).</summary>
public sealed record SampleVerifyResult
{
    public required IReadOnlyList<SampleFinding> Findings { get; init; }

    /// <summary>The number of pairs actually run through their guardrail.</summary>
    public required int PairsVerified { get; init; }

    public bool Passed => Findings.Count == 0;
}

/// <summary>
/// Verifies every committed <c>tasks/&lt;id&gt;/samples/</c> pair against its matching
/// <c>tasks/&lt;id&gt;/guardrails/</c> script (plan of record 26). For each task, samples are grouped by
/// base name (their filename with the last two extensions stripped) and matched ordinally to a
/// <see cref="GuardrailDefinition.Name"/>. A matched, complete, executable pair is run through the same
/// <see cref="ScriptUnitRunner"/> path a real guardrail invocation takes — cwd <see cref="PlanDefinition.Workspace"/>,
/// the sample supplied both as the first positional argument and as <c>GR_SUBJECT</c> — and classified by
/// the two halves' exit codes. Every problem becomes a <see cref="SampleFinding"/>; this type never
/// throws for a malformed sample or a crashing guardrail; a task with no <c>samples/</c> folder costs one
/// directory probe and nothing else.
/// </summary>
public static class SampleVerifier
{
    private const string ValidExtension = ".valid";
    private const string InvalidExtension = ".invalid";
    private const string SubjectEnvironmentVariable = "GR_SUBJECT";

    public static async Task<SampleVerifyResult> VerifyAsync(
        PlanDefinition plan,
        ProcessRunner processRunner,
        TimeSpan perSampleTimeout,
        CancellationToken cancellationToken)
    {
        var findings = new List<SampleFinding>();
        int pairsVerified = 0;

        var interpreterMap = InterpreterMap.CreateDefault(plan.Config);
        var scriptRunner = new ScriptUnitRunner(processRunner, interpreterMap);

        foreach (TaskNode task in plan.Tasks)
        {
            string samplesDir = Path.Combine(task.Directory, "samples");
            if (!Directory.Exists(samplesDir))
            {
                continue;
            }

            foreach (SamplePairFiles pair in DiscoverPairs(samplesDir))
            {
                pairsVerified += await VerifyPairAsync(
                        task, pair.BaseName, pair.ValidPath, pair.InvalidPath,
                        scriptRunner, interpreterMap, plan.Workspace, perSampleTimeout, findings, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new SampleVerifyResult { Findings = findings, PairsVerified = pairsVerified };
    }

    /// <summary>One base name's committed halves, discovered under one task's <c>samples/</c> folder.</summary>
    private sealed record SamplePairFiles(string BaseName, string? ValidPath, string? InvalidPath);

    /// <summary>
    /// Groups every <c>&lt;base&gt;.valid.&lt;ext&gt;</c> / <c>&lt;base&gt;.invalid.&lt;ext&gt;</c> file by
    /// <c>&lt;base&gt;</c> (the filename with its last two extensions stripped). Every other file in the
    /// folder — a README, a <c>*.probe.ps1</c> helper — does not carry a <c>.valid</c>/<c>.invalid</c>
    /// second extension and is silently ignored, per the committed corpus convention.
    /// </summary>
    private static IEnumerable<SamplePairFiles> DiscoverPairs(string samplesDir)
    {
        var validByBase = new Dictionary<string, string>(StringComparer.Ordinal);
        var invalidByBase = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (string file in Directory.EnumerateFiles(samplesDir))
        {
            string withoutLastExtension = Path.GetFileNameWithoutExtension(file);
            string secondExtension = Path.GetExtension(withoutLastExtension);

            bool isValid = secondExtension == ValidExtension;
            bool isInvalid = secondExtension == InvalidExtension;
            if (!isValid && !isInvalid)
            {
                continue;
            }

            string baseName = Path.GetFileNameWithoutExtension(withoutLastExtension);
            if (!validByBase.ContainsKey(baseName) && !invalidByBase.ContainsKey(baseName))
            {
                order.Add(baseName);
            }

            if (isValid)
            {
                validByBase[baseName] = file;
            }
            else
            {
                invalidByBase[baseName] = file;
            }
        }

        foreach (string baseName in order)
        {
            validByBase.TryGetValue(baseName, out string? validPath);
            invalidByBase.TryGetValue(baseName, out string? invalidPath);
            yield return new SamplePairFiles(baseName, validPath, invalidPath);
        }
    }

    /// <summary>Verifies one base name's pair; returns 1 if a guardrail was actually run for it, else 0.</summary>
    private static async Task<int> VerifyPairAsync(
        TaskNode task,
        string baseName,
        string? validPath,
        string? invalidPath,
        ScriptUnitRunner scriptRunner,
        InterpreterMap interpreterMap,
        string workspace,
        TimeSpan perSampleTimeout,
        List<SampleFinding> findings,
        CancellationToken cancellationToken)
    {
        GuardrailDefinition? guardrail = task.Guardrails.FirstOrDefault(
            g => string.Equals(g.Name, baseName, StringComparison.Ordinal));

        if (guardrail is null)
        {
            // Stale pair: the script was renamed or deleted and the sample(s) were left behind. One
            // finding per committed half — each names a real file on disk.
            if (validPath is not null)
            {
                findings.Add(OrphanFinding(validPath));
            }

            if (invalidPath is not null)
            {
                findings.Add(OrphanFinding(invalidPath));
            }

            return 0;
        }

        if (guardrail.Kind == ActionKind.Prompt)
        {
            string samplePath = validPath ?? invalidPath!;
            findings.Add(new SampleFinding
            {
                Kind = SampleFindingKind.Unverifiable,
                GuardrailPath = guardrail.Path,
                SamplePath = samplePath,
                Message = $"Guardrail '{guardrail.Path}' is a prompt judge, not a script — its sample pair " +
                          $"(matched to '{samplePath}') cannot be executed deterministically and is recorded " +
                          "but never run, the same 'recorded but never run' failure this feature exists to end."
            });
            return 0;
        }

        if (validPath is null || invalidPath is null)
        {
            string presentPath = validPath ?? invalidPath!;
            string missingHalf = validPath is null ? "valid" : "invalid";
            findings.Add(new SampleFinding
            {
                Kind = SampleFindingKind.MissingHalf,
                GuardrailPath = guardrail.Path,
                SamplePath = presentPath,
                Message = $"Sample pair '{baseName}' for guardrail '{guardrail.Path}' is missing its " +
                          $"'.{missingHalf}' half — only '{presentPath}' is committed; a one-sided pair " +
                          "certifies nothing."
            });
            return 0;
        }

        InterpreterMap.Resolution resolution = interpreterMap.Resolve(guardrail.Path, guardrail.Args);
        if (resolution.Status != InterpreterMap.Status.Resolved)
        {
            findings.Add(new SampleFinding
            {
                Kind = SampleFindingKind.Unverifiable,
                GuardrailPath = guardrail.Path,
                SamplePath = validPath,
                Message = $"Guardrail '{guardrail.Path}' interpreter did not resolve ({resolution.Status}) — " +
                          "its sample pair cannot be executed deterministically and is recorded but never run."
            });
            return 0;
        }

        ProcessResult validResult = await RunSampleAsync(
                scriptRunner, guardrail, validPath, workspace, perSampleTimeout, cancellationToken)
            .ConfigureAwait(false);
        ProcessResult invalidResult = await RunSampleAsync(
                scriptRunner, guardrail, invalidPath, workspace, perSampleTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (validResult.TimedOut || invalidResult.TimedOut)
        {
            string timedOutPath = validResult.TimedOut ? validPath : invalidPath;
            findings.Add(new SampleFinding
            {
                Kind = SampleFindingKind.Unverifiable,
                GuardrailPath = guardrail.Path,
                SamplePath = timedOutPath,
                Message = $"Guardrail '{guardrail.Path}' timed out (> {perSampleTimeout}) running sample " +
                          $"'{timedOutPath}' — a timed-out run produces no usable exit code and is never " +
                          "treated as a silent pass."
            });
            return 0;
        }

        bool validPassed = validResult.ExitCode == 0;
        bool invalidPassed = invalidResult.ExitCode == 0;

        if (validPassed && !invalidPassed)
        {
            return 1; // sound pair: no finding.
        }

        if (!validPassed && invalidPassed)
        {
            findings.Add(new SampleFinding
            {
                Kind = SampleFindingKind.ReversedPolarity,
                GuardrailPath = guardrail.Path,
                SamplePath = validPath,
                ObservedExitCode = validResult.ExitCode,
                Message = $"Guardrail '{guardrail.Path}': valid sample '{validPath}' exited non-zero " +
                          $"(exit {validResult.ExitCode}) while invalid sample '{invalidPath}' exited 0 " +
                          $"(exit {invalidResult.ExitCode}) — the two halves are swapped, or the guardrail's " +
                          "sense is inverted."
            });
            return 1;
        }

        if (!validPassed) // && !invalidPassed
        {
            findings.Add(new SampleFinding
            {
                Kind = SampleFindingKind.ValidHalfFailed,
                GuardrailPath = guardrail.Path,
                SamplePath = validPath,
                ObservedExitCode = validResult.ExitCode,
                Message = $"Guardrail '{guardrail.Path}' rejects its own valid sample '{validPath}' " +
                          $"(exit {validResult.ExitCode}) — a false-red on a representative correct artifact. " +
                          $"Its invalid sample '{invalidPath}' also exited non-zero " +
                          $"(exit {invalidResult.ExitCode}); the guardrail may not be reading the sample at all."
            });
            return 1;
        }

        // validPassed && invalidPassed: the guardrail can never fail.
        findings.Add(new SampleFinding
        {
            Kind = SampleFindingKind.InvalidHalfPassed,
            GuardrailPath = guardrail.Path,
            SamplePath = invalidPath,
            ObservedExitCode = invalidResult.ExitCode,
            Message = $"Guardrail '{guardrail.Path}' exits 0 for its own invalid sample '{invalidPath}' " +
                      $"(exit {invalidResult.ExitCode}) — this guardrail can never fail. Its valid sample " +
                      $"'{validPath}' also exited 0 (exit {validResult.ExitCode}); the guardrail may not be " +
                      "reading the sample at all."
        });
        return 1;
    }

    /// <summary>
    /// Runs one guardrail against one sample half, cwd <paramref name="workspace"/> (a guardrail's
    /// built-in default subject is repo-relative; anywhere else turns "ignored the sample" into a crash
    /// that reads as a correct rejection). The sample is bound both ways the committed corpus uses: the
    /// absolute path as the guardrail's first positional argument, and the same path in
    /// <c>GR_SUBJECT</c> — a verifier that supplies only one silently mis-verifies half the corpus.
    /// </summary>
    private static Task<ProcessResult> RunSampleAsync(
        ScriptUnitRunner scriptRunner,
        GuardrailDefinition guardrail,
        string samplePath,
        string workspace,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var args = new List<string>(guardrail.Args.Count + 1) { samplePath };
        args.AddRange(guardrail.Args);

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SubjectEnvironmentVariable] = samplePath
        };

        return scriptRunner.RunAsync(guardrail.Path, args, workspace, env, timeout, cancellationToken);
    }

    private static SampleFinding OrphanFinding(string samplePath) => new()
    {
        Kind = SampleFindingKind.OrphanSample,
        GuardrailPath = null,
        SamplePath = samplePath,
        Message = $"Sample '{samplePath}' matches no guardrail in this task — the guardrail script may " +
                  "have been renamed or deleted, leaving a stale pair that certifies nothing."
    };
}
