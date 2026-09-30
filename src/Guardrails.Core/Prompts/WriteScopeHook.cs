using Guardrails.Core.Execution;
using Guardrails.Core.State;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Issue #816: a Claude Code <c>PreToolUse</c> hook that refuses a file-editing tool call whose target lies
/// INSIDE the task's workspace but OUTSIDE its enforced <c>writeScope</c> — the WRITE-TIME half of write-scope
/// enforcement. The retrospective <see cref="WriteScopeCheck"/> remains the backstop: it runs at the end of
/// every attempt (success or not) and catches every route this hook cannot see.
///
/// <para><b>Why it exists.</b> An implement task edited its upstream task's TEST files across several attempts
/// that each ended in a timeout or a turn cap. The retrospective check never ran on those attempts, the retry
/// feedback told the agent to continue from its partial work, and the agent ended up grading itself against
/// tests it had rewritten. The agent never got a signal that the files were off-limits. A refusal at the
/// moment of the write IS that signal, and it names the scope and the needsHuman door.</para>
///
/// <para><b>What it polices.</b> <c>Write</c>/<c>Edit</c>/<c>MultiEdit</c>/<c>NotebookEdit</c> only — the
/// tools whose target is a single, exact path. <c>Bash</c> is deliberately NOT policed here: a build or a test
/// run legitimately writes outside the scope (<c>bin/</c>, <c>obj/</c>, caches), and the command-text heuristics
/// the containment hook uses would refuse those. A Bash write outside the scope is caught by the retrospective
/// check at the end of the attempt, exactly as before.</para>
///
/// <para><b>What it allows.</b> A target outside the workspace root (worktree containment is
/// <see cref="WorktreeContainmentHook"/>'s job, and serial mode never contained writes); the harness's own
/// provisioned folders <c>.guardrails-agent-io/</c> (the staged <c>GUARDRAILS_STATE_OUT</c>, SSOT §9.5) and
/// <c>.guardrails-staging/</c> (§3.5); and every path the enforced scope covers — the declared
/// <c>writeScope</c> plus the implicit <c>stagingOutputs</c> destinations, the same array the retrospective
/// check gates on.</para>
///
/// <para><b>One matcher.</b> The scope test is <see cref="WriteScope.IsInScope"/>'s rule, compiled ONCE in C#
/// to anchored regular expressions (<see cref="WriteScope.ToAnchoredPatterns"/>) that the scripts only apply —
/// the script templates carry data, never a second glob implementation. <c>WriteScopeTests</c> proves the
/// compiled patterns agree with <see cref="WriteScope.IsInScope"/> over a corpus, and
/// <c>WriteScopeHookTests</c> runs the REAL generated script standalone.</para>
/// </summary>
public static class WriteScopeHook
{
    /// <summary>The Claude Code tool-name matcher: the file-editing tools, never Bash (see the type remarks).</summary>
    internal const string Matcher = "Write|Edit|MultiEdit|NotebookEdit";

    internal const string ScriptFileNameWindows = "write-scope-hook.ps1";
    internal const string ScriptFileNameUnix = "write-scope-hook.sh";

    /// <summary>The settings file written when the scope hook runs ALONE (serial mode: no containment hook).</summary>
    internal const string SettingsFileName = "write-scope-settings.json";

    /// <summary>
    /// The workspace-relative prefixes the harness provisions for the agent to write into, allowed whatever the
    /// scope says: the staged state-out fragment (SSOT §9.5) and the stagingOutputs staging tree (§3.5). Both are
    /// excluded from every harness staging site too (<see cref="SegmentStaging.ReconstructableExclusions"/>), so
    /// nothing written there can ever reach a commit or the retrospective check.
    /// </summary>
    internal static readonly IReadOnlyList<string> HarnessProvisionedPrefixes =
    [
        ".guardrails-agent-io/",
        ".guardrails-staging/"
    ];

    /// <summary>What a hook script needs: the workspace root it polices and the ENFORCED scope.</summary>
    /// <param name="WorkspaceRoot">The effective workspace: the segment worktree, or the serial plan workspace.</param>
    /// <param name="Scope">The enforced scope — declared writeScope plus the implicit staging destinations.</param>
    public sealed record Spec(string WorkspaceRoot, IReadOnlyList<string> Scope);

    /// <summary>
    /// Write the scope hook script into <paramref name="logDir"/> (harness-owned, outside any segment) and return
    /// its path. <paramref name="filePrefix"/> disambiguates several invocations sharing one log dir.
    /// </summary>
    internal static string WriteScript(string logDir, Spec spec, string? filePrefix = null)
    {
        Directory.CreateDirectory(logDir);

        bool windows = OperatingSystem.IsWindows();
        string prefix = string.IsNullOrEmpty(filePrefix) ? string.Empty : filePrefix + ".";
        string scriptPath = Path.Combine(logDir, prefix + (windows ? ScriptFileNameWindows : ScriptFileNameUnix));
        AtomicFile.WriteAllText(scriptPath, windows ? PowerShellScript(spec) : BashScript(spec));
        if (!windows)
        {
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return scriptPath;
    }

    /// <summary>
    /// Serial mode: write the scope hook ALONE (there is no segment worktree to contain) plus its own settings
    /// file, and return the settings path to hand to <c>claude --settings</c>. Worktree mode composes this hook
    /// into the containment hook's settings instead (<see cref="WorktreeContainmentHook.WriteHookFiles"/>), so a
    /// runner always receives exactly ONE settings file.
    /// </summary>
    public static string WriteHookFiles(string logDir, Spec spec)
    {
        string scriptPath = WriteScript(logDir, spec);
        string settingsPath = Path.Combine(logDir, SettingsFileName);
        AtomicFile.WriteAllText(
            settingsPath,
            WorktreeContainmentHook.HookSettingsJson([(Matcher, scriptPath)], OperatingSystem.IsWindows()));
        return settingsPath;
    }

    /// <summary>
    /// Every pattern the script accepts a workspace-relative path by: the harness-provisioned prefixes first,
    /// then the scope compiled by <see cref="WriteScope.ToAnchoredPatterns"/>. Never empty, which the bash
    /// template relies on (an empty array under <c>set -u</c> is an error on bash 3.2, macOS's default).
    /// </summary>
    internal static IReadOnlyList<string> AllowedPatterns(IReadOnlyList<string> scope)
    {
        var patterns = new List<string>();
        foreach (string prefix in HarnessProvisionedPrefixes)
        {
            patterns.Add("^" + WriteScope.EscapeForPattern(prefix));
        }

        patterns.AddRange(WriteScope.ToAnchoredPatterns(scope));
        return patterns;
    }

    /// <summary>
    /// The refusal the agent reads (exit 2 + stderr is Claude Code's block contract). It names the scope and the
    /// needsHuman door, because the moment a write is refused is the one moment the agent is certain to read it.
    /// The per-path part is appended by the script.
    /// </summary>
    internal static string DenialTail(IReadOnlyList<string> scope)
    {
        string scopeText = scope.Count == 0
            ? "EMPTY (this task may not change any file)"
            : string.Join(", ", scope);
        return
            $"is outside this task's writeScope: {scopeText}. " +
            "Write only paths the writeScope covers; anything else is reverted at the end of the attempt and fails it. " +
            "If this task cannot be done without changing that path, do not write it: write " +
            "{ \"needsHuman\": { \"question\": \"<the path, and why this task must change it>\", \"kind\": \"blocked-work\" } } " +
            "to GUARDRAILS_STATE_OUT instead, so a human can widen the writeScope in task.json.";
    }

    private static string BashLiteral(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string PowerShellLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// The bash hook body. Path handling mirrors <see cref="WorktreeContainmentHook.BashScript"/> exactly (the same
    /// dependency-free JSON field extractor and <c>.</c>/<c>..</c> collapse, the same accepted-root list, no
    /// symlink resolution); the scope test is a case-insensitive <c>[[ =~ ]]</c> over the baked patterns.
    /// </summary>
    internal static string BashScript(Spec spec)
    {
        string roots = string.Join("\n", WorktreeContainmentHook.AcceptedRoots(spec.WorkspaceRoot).Select(BashLiteral));
        string patterns = string.Join("\n", AllowedPatterns(spec.Scope).Select(BashLiteral));
        string tail = BashLiteral(DenialTail(spec.Scope));

        return $$"""
            #!/usr/bin/env bash
            # Guardrails write-scope PreToolUse hook (issue #816). Generated per attempt; the workspace-root
            # spellings and the allowed-path patterns below are literals baked in at generation time. The
            # patterns are WriteScope.IsInScope's rule compiled in C# (WriteScope.ToAnchoredPatterns) -- this
            # script only applies them, it never interprets a glob itself.
            set -u
            shopt -s nocasematch

            ACCEPTED_ROOTS=(
            {{roots}}
            )

            ALLOWED_PATTERNS=(
            {{patterns}}
            )

            DENIAL_TAIL={{tail}}

            input="$(cat)"

            extract() {
              # Same POSIX-ERE extractor as the containment hook (sed -E, never GNU-only BRE alternation).
              printf '%s' "$input" | sed -En 's/.*"'"$1"'"[[:space:]]*:[[:space:]]*"(([^"\\]|\\.)*)".*/\1/p' | head -n1 \
                | sed 's/\\"/"/g; s/\\\\/\\/g'
            }

            normalize_path() {
              local input="$1"
              local -a parts=()
              local seg
              local old_ifs="$IFS"
              IFS='/'
              read -ra segs <<< "$input"
              IFS="$old_ifs"
              for seg in "${segs[@]}"; do
                case "$seg" in
                  ""|".") continue ;;
                  "..")
                    if [ "${#parts[@]}" -gt 0 ]; then
                      unset 'parts[${#parts[@]}-1]'
                    fi
                    ;;
                  *) parts+=("$seg") ;;
                esac
              done
              if [ "${#parts[@]}" -eq 0 ]; then
                printf '/'
                return
              fi
              local result=""
              for seg in "${parts[@]}"; do
                result="$result/$seg"
              done
              printf '%s' "$result"
            }

            ROOT_NORMS=()
            for accepted_root in "${ACCEPTED_ROOTS[@]}"; do
              accepted_norm="$(normalize_path "$accepted_root")"
              ROOT_NORMS+=("${accepted_norm%/}")
            done
            WORKSPACE_ROOT="${ACCEPTED_ROOTS[0]}"

            check_scope() {
              local candidate="$1"
              [ -z "$candidate" ] && return 0

              local absolute
              if [[ "$candidate" = /* ]]; then
                absolute="$candidate"
              else
                absolute="$WORKSPACE_ROOT/$candidate"
              fi

              local resolved
              resolved="$(normalize_path "$absolute")"

              local rel=""
              local inside=0
              local root_norm
              for root_norm in "${ROOT_NORMS[@]}"; do
                case "$resolved" in
                  "$root_norm") return 0 ;;
                  "$root_norm"/*) rel="${resolved#"$root_norm"/}"; inside=1; break ;;
                esac
              done

              # Outside the workspace: not this hook's question (containment is the containment hook's).
              [ "$inside" -eq 0 ] && return 0

              local pattern
              for pattern in "${ALLOWED_PATTERNS[@]}"; do
                if [[ "$rel" =~ $pattern ]]; then
                  return 0
                fi
              done

              echo "BLOCKED by Guardrails write-scope hook: '$rel' $DENIAL_TAIL" >&2
              exit 2
            }

            tool_name="$(extract tool_name)"
            case "$tool_name" in
              Write|Edit|MultiEdit)
                check_scope "$(extract file_path)"
                ;;
              NotebookEdit)
                fp="$(extract notebook_path)"
                [ -z "$fp" ] && fp="$(extract file_path)"
                check_scope "$fp"
                ;;
            esac

            exit 0

            """;
    }

    /// <summary>
    /// The PowerShell hook body: the <see cref="WorktreeContainmentHook.PowerShellScript"/> path rules
    /// (<c>GetFullPath</c> normalisation, the accepted-root list, directory-boundary comparison, no symlink
    /// resolution) plus <c>-match</c> — case-insensitive by default, as <see cref="WriteScope.IsInScope"/> is —
    /// over the baked patterns.
    /// </summary>
    internal static string PowerShellScript(Spec spec)
    {
        string roots = string.Join("\n", WorktreeContainmentHook.AcceptedRoots(spec.WorkspaceRoot).Select(PowerShellLiteral));
        string patterns = string.Join("\n", AllowedPatterns(spec.Scope).Select(PowerShellLiteral));
        string tail = PowerShellLiteral(DenialTail(spec.Scope));

        return $$"""
            # Guardrails write-scope PreToolUse hook (issue #816). Generated per attempt; the workspace-root
            # spellings and the allowed-path patterns below are literals baked in at generation time. The
            # patterns are WriteScope.IsInScope's rule compiled in C# (WriteScope.ToAnchoredPatterns) -- this
            # script only applies them, it never interprets a glob itself.
            $ErrorActionPreference = 'Stop'

            $AcceptedRoots = @(
            {{roots}}
            )

            $AllowedPatterns = @(
            {{patterns}}
            )

            $DenialTail = {{tail}}

            $stdin = [Console]::In.ReadToEnd()

            $acceptedFull = @()
            foreach ($acceptedRoot in $AcceptedRoots) {
                $acceptedFull += [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($acceptedRoot))
            }
            $rootFull = $acceptedFull[0]

            function Test-Scope([string]$candidate) {
                if ([string]::IsNullOrWhiteSpace($candidate)) { return }

                if (-not [System.IO.Path]::IsPathRooted($candidate)) {
                    $candidate = Join-Path $rootFull $candidate
                }

                $resolved = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($candidate))

                $rel = $null
                foreach ($root in $acceptedFull) {
                    if ($resolved -ieq $root) { return }
                    $prefix = $root + [System.IO.Path]::DirectorySeparatorChar
                    if ($resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                        $rel = $resolved.Substring($prefix.Length)
                        break
                    }
                }

                # Outside the workspace: not this hook's question (containment is the containment hook's).
                if ($null -eq $rel) { return }

                $rel = $rel.Replace('\', '/')
                foreach ($pattern in $AllowedPatterns) {
                    if ($rel -match $pattern) { return }
                }

                [Console]::Error.WriteLine("BLOCKED by Guardrails write-scope hook: '$rel' $DenialTail")
                exit 2
            }

            try {
                $payload = $stdin | ConvertFrom-Json
            } catch {
                exit 0  # unparseable input -- fail open on the hook itself, never crash the tool call
            }

            $toolName = $payload.tool_name
            $toolInput = $payload.tool_input

            switch ($toolName) {
                { $_ -in @('Write', 'Edit', 'MultiEdit') } {
                    Test-Scope $toolInput.file_path
                }
                'NotebookEdit' {
                    $fp = $toolInput.notebook_path
                    if ([string]::IsNullOrWhiteSpace($fp)) { $fp = $toolInput.file_path }
                    Test-Scope $fp
                }
            }

            exit 0

            """;
    }
}
