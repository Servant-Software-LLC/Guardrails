using System.Text;
using Guardrails.Core.Execution;
using Guardrails.Core.State;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Generates a Claude Code <c>PreToolUse</c> hook (issue #199) that hard-enforces worktree
/// containment at RUNTIME — the OUTER boundary. <see cref="WorkspaceContainment.Escapes"/>
/// (used by <c>PlanValidator</c>) only ever polices the plan AUTHOR's declared <c>writeScope</c>
/// at validation time; nothing previously stopped a task AGENT from writing to an absolute path
/// outside its segment worktree at runtime — a write there never appears in the post-hoc
/// <c>git diff</c> the write-scope CHECK inspects (<see cref="Execution.WriteScopeCheck"/>,
/// SSOT §3.4), so it went completely undetected. That post-hoc diff check remains the INNER
/// boundary, unaffected by this hook.
///
/// <para>The hook is injected ONLY for worktree-mode prompt invocations (a real segment, SSOT
/// §1) via <c>claude -p --settings &lt;path&gt;</c> — session-scoped, additive: it never touches
/// the user's own <c>~/.claude/settings.json</c> or the repo's <c>.claude/settings.json</c>.
/// It intercepts <c>Write</c>/<c>Edit</c>/<c>MultiEdit</c>/<c>NotebookEdit</c> and write-ish
/// <c>Bash</c> commands (redirects, <c>tee</c>/<c>cp</c>/<c>mv</c>, <c>git checkout --</c>,
/// <c>git worktree add</c>) and blocks any target path that resolves outside the segment
/// worktree root, reusing the SAME escape decision as <see cref="WorkspaceContainment.Escapes"/>
/// (rooted-path rejection + normalized-path directory-boundary comparison — never reimplemented
/// as a different rule, only re-expressed in shell/PowerShell since the hook runs as an OS
/// process Claude Code spawns directly, not a .NET callback). It ALSO blocks the <c>git stash</c>
/// family (issue #192): <c>refs/stash</c> is repo-wide, not worktree-scoped, so a concurrent
/// task's <c>stash pop</c> can silently apply into the WRONG worktree. Both rules live in the
/// SAME hook script and settings file — one mechanism, two additive checks.</para>
///
/// <para><b>Boundary/honesty note:</b> this is defense at the TOOL-CALL layer Claude Code exposes
/// (Write/Edit/MultiEdit/NotebookEdit/Bash). It cannot stop an agent from asking Claude Code to
/// spawn an arbitrary un-parseable process that itself writes outside the worktree via some
/// mechanism the Bash-command heuristic fails to recognize (e.g. a compiled helper, an obscure
/// redirection form, a script interpreter's own file-write primitive) — the Bash matcher is a
/// heuristic over the command TEXT, not a sandboxed OS-level filesystem ACL. It raises the bar
/// sharply for the classes of accidental/careless escape #199 was written against; it is not a
/// security sandbox against a deliberately adversarial agent.</para>
/// </summary>
public static class WorktreeContainmentHook
{
    /// <summary>The Claude Code tool-name matcher: every tool this hook must inspect.</summary>
    internal const string Matcher = "Write|Edit|MultiEdit|NotebookEdit|Bash";

    internal const string ScriptFileNameWindows = "containment-hook.ps1";
    internal const string ScriptFileNameUnix = "containment-hook.sh";
    internal const string SettingsFileName = "containment-settings.json";

    /// <summary>
    /// Write the hook script + Claude Code settings JSON into <paramref name="logDir"/> (a
    /// harness-owned directory OUTSIDE the segment worktree, so the generated files never pollute
    /// <c>git status</c> / the write-scope diff). Returns the absolute path to the settings file —
    /// pass it to <c>claude -p --settings &lt;path&gt;</c>. <paramref name="worktreeRoot"/> is baked
    /// into the script body as a literal (one script per attempt — no extra env/arg plumbing).
    /// <paramref name="filePrefix"/> disambiguates multiple invocations sharing one <paramref
    /// name="logDir"/> (an action AND each of its guardrails all write into the same attempt log
    /// directory) — defaults to the action's plain file names.
    /// </summary>
    public static string WriteHookFiles(string logDir, string worktreeRoot, string? filePrefix = null)
    {
        Directory.CreateDirectory(logDir);

        bool windows = OperatingSystem.IsWindows();
        string prefix = string.IsNullOrEmpty(filePrefix) ? string.Empty : filePrefix + ".";
        string scriptFileName = prefix + (windows ? ScriptFileNameWindows : ScriptFileNameUnix);
        string scriptPath = Path.Combine(logDir, scriptFileName);
        string scriptBody = windows ? PowerShellScript(worktreeRoot) : BashScript(worktreeRoot);

        AtomicFile.WriteAllText(scriptPath, scriptBody);
        if (!windows)
        {
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        string settingsPath = Path.Combine(logDir, prefix + SettingsFileName);
        AtomicFile.WriteAllText(settingsPath, SettingsJson(scriptPath, windows));
        return settingsPath;
    }

    /// <summary>
    /// The settings JSON handed to <c>claude --settings</c> (SSOT §9): one <c>PreToolUse</c> matcher
    /// group covering <see cref="Matcher"/>, one command hook whose <c>command</c> directly spawns
    /// the OS-appropriate script — <c>pwsh -File</c> on Windows (matches the interpreter convention
    /// used elsewhere in the harness), the executable <c>.sh</c> directly on Unix.
    /// </summary>
    internal static string SettingsJson(string scriptPath, bool windows)
    {
        string command = windows
            ? $"pwsh -NoProfile -ExecutionPolicy Bypass -File {ShellQuoteForJson(scriptPath)}"
            : ShellQuoteForJson(scriptPath).Trim('"');

        return $$"""
        {
          "hooks": {
            "PreToolUse": [
              {
                "matcher": "{{Matcher}}",
                "hooks": [
                  {
                    "type": "command",
                    "command": {{JsonQuote(command)}}
                  }
                ]
              }
            ]
          }
        }
        """;
    }

    // On Windows the script PATH itself may need quoting inside the shell command line (spaces);
    // `pwsh -File "<path>"` needs the path quoted for the shell, and then the WHOLE command string
    // needs JSON-quoting for the settings file. This wraps the path in shell double-quotes first.
    private static string ShellQuoteForJson(string path) => $"\"{path}\"";

    private static string JsonQuote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                default: sb.Append(c); break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// The bash hook script body (issue #199/#192), worktree root baked in as a literal. Reads the
    /// PreToolUse tool-call JSON from stdin via a small dependency-free field extractor (no <c>jq</c>
    /// assumed on the agent's PATH); exit 2 + stderr is Claude Code's documented block contract.
    /// Uses <c>realpath -m</c> (resolves symlinks AND non-existent path segments) — a strictly
    /// MORE conservative escape check than the plain-normalize <see cref="WorkspaceContainment.Escapes"/>
    /// it otherwise mirrors (rooted-path rejection, directory-boundary comparison): a symlink whose
    /// target itself escapes the worktree is caught here even though pure path-string normalization
    /// would not catch it.
    /// </summary>
    internal static string BashScript(string worktreeRoot) => $$"""
        #!/usr/bin/env bash
        # Guardrails worktree-containment PreToolUse hook (issue #199 / #192). Generated per attempt;
        # the worktree root below is a literal baked in at generation time, not read from the environment.
        set -u

        WORKTREE_ROOT="{{worktreeRoot}}"

        input="$(cat)"

        extract() {
          # $1 = field name; extracts the first "<name>":"<value>" match (top-level or nested), then
          # unescapes \" and \\ — the two escapes a path can realistically contain.
          printf '%s' "$input" | sed -n 's/.*"'"$1"'"[[:space:]]*:[[:space:]]*"\(\([^"\\]\|\\.\)*\)".*/\1/p' | head -n1 \
            | sed 's/\\"/"/g; s/\\\\/\\/g'
        }

        tool_name="$(extract tool_name)"

        block() {
          echo "BLOCKED by Guardrails worktree-containment hook: $1" >&2
          exit 2
        }

        resolve_and_check() {
          local candidate="$1"
          [ -z "$candidate" ] && return 0

          local resolved
          if command -v realpath >/dev/null 2>&1; then
            if [[ "$candidate" = /* ]]; then
              resolved="$(realpath -m -- "$candidate" 2>/dev/null || printf '%s' "$candidate")"
            else
              resolved="$(realpath -m -- "$WORKTREE_ROOT/$candidate" 2>/dev/null || printf '%s' "$WORKTREE_ROOT/$candidate")"
            fi
          else
            if [[ "$candidate" = /* ]]; then
              resolved="$candidate"
            else
              resolved="$WORKTREE_ROOT/$candidate"
            fi
          fi

          local root_norm="${WORKTREE_ROOT%/}"
          case "$resolved" in
            "$root_norm"|"$root_norm"/*) return 0 ;;
            *) block "path '$candidate' resolves to '$resolved', outside the task worktree '$root_norm'" ;;
          esac
        }

        case "$tool_name" in
          Write|Edit|MultiEdit)
            resolve_and_check "$(extract file_path)"
            ;;
          NotebookEdit)
            fp="$(extract notebook_path)"
            [ -z "$fp" ] && fp="$(extract file_path)"
            resolve_and_check "$fp"
            ;;
          Bash)
            cmd="$(extract command)"

            # git stash family (#192): refs/stash is repo-wide, not worktree-scoped -- a concurrent
            # task's stash can silently apply into the WRONG worktree. Always block, any subcommand.
            if printf '%s' "$cmd" | grep -Eq '(^|[;&|]|[[:space:]])git[[:space:]]+stash([[:space:]]|$)'; then
              block "'git stash' is repo-wide, not worktree-scoped -- a concurrent task's stash can silently cross-contaminate this worktree. Use: git diff > /tmp/mine.patch && git checkout -- <files> to test baseline, then git apply /tmp/mine.patch to restore."
            fi

            # git worktree add <path> -- a new worktree rooted outside this segment is exactly the
            # escape class #199 targets (a sibling task's tree, or the user's main checkout).
            if printf '%s' "$cmd" | grep -Eq '(^|[;&|]|[[:space:]])git[[:space:]]+worktree[[:space:]]+add[[:space:]]'; then
              wt_path="$(printf '%s' "$cmd" | sed -E 's/.*git[[:space:]]+worktree[[:space:]]+add[[:space:]]+//' | awk '{
                for (i = 1; i <= NF; i++) {
                  if ($i ~ /^-/) { if ($i == "-b" || $i == "-B") { i++ } ; continue }
                  print $i; exit
                }
              }')"
              resolve_and_check "$wt_path"
            fi

            # git checkout -- <path> (restoring a path from another commit/branch into place).
            if printf '%s' "$cmd" | grep -Eq '(^|[;&|]|[[:space:]])git[[:space:]]+checkout[[:space:]].*--[[:space:]]+[^[:space:]]'; then
              co_path="$(printf '%s' "$cmd" | sed -E 's/.*--[[:space:]]+//')"
              for p in $co_path; do resolve_and_check "$p"; done
            fi

            # Output redirection (> / >>) -- the LAST redirect target on the line.
            if printf '%s' "$cmd" | grep -Eq '>>?[[:space:]]*[^[:space:]&|;]+'; then
              redir_path="$(printf '%s' "$cmd" | grep -Eo '>>?[[:space:]]*[^[:space:]&|;]+' | tail -n1 | sed -E 's/^>>?[[:space:]]*//')"
              resolve_and_check "$redir_path"
            fi

            if printf '%s' "$cmd" | grep -Eq '(^|[;&|]|[[:space:]])tee[[:space:]]'; then
              tee_path="$(printf '%s' "$cmd" | sed -E 's/.*tee[[:space:]]+(-a[[:space:]]+)?//' | awk '{print $1}')"
              resolve_and_check "$tee_path"
            fi

            if printf '%s' "$cmd" | grep -Eq '(^|[;&|]|[[:space:]])(cp|mv)[[:space:]]'; then
              dest="$(printf '%s' "$cmd" | sed -E 's/.*(^|[;&|[:space:]])(cp|mv)[[:space:]]+//' | awk '{print $NF}')"
              resolve_and_check "$dest"
            fi
            ;;
        esac

        exit 0

        """;

    /// <summary>
    /// The PowerShell hook script body (issue #199/#192), worktree root baked in as a literal. This
    /// is the LITERAL mirror of <see cref="WorkspaceContainment.Escapes"/> (rooted-path rejection,
    /// <c>GetFullPath</c> normalization, directory-boundary comparison) — no symlink resolution,
    /// exactly matching the reused C# decision function's semantics.
    /// </summary>
    internal static string PowerShellScript(string worktreeRoot)
    {
        string escapedRoot = worktreeRoot.Replace("`", "``").Replace("\"", "`\"");
        return $$"""
        # Guardrails worktree-containment PreToolUse hook (issue #199 / #192). Generated per attempt;
        # the worktree root below is a literal baked in at generation time, not read from the environment.
        $ErrorActionPreference = 'Stop'

        $WorktreeRoot = "{{escapedRoot}}"

        $stdin = [Console]::In.ReadToEnd()

        function Block([string]$reason) {
            [Console]::Error.WriteLine("BLOCKED by Guardrails worktree-containment hook: $reason")
            exit 2
        }

        $rootFull = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($WorktreeRoot))

        function Test-Escapes([string]$candidate) {
            if ([string]::IsNullOrWhiteSpace($candidate)) { return $false }

            if (-not [System.IO.Path]::IsPathRooted($candidate)) {
                $candidate = Join-Path $rootFull $candidate
            }

            $resolved = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($candidate))

            if ($resolved -ieq $rootFull) { return $false }
            return -not $resolved.StartsWith($rootFull + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
        }

        function Resolve-AndCheck([string]$candidate) {
            if ([string]::IsNullOrWhiteSpace($candidate)) { return }
            if (Test-Escapes $candidate) {
                Block "path '$candidate' resolves outside the task worktree '$rootFull'"
            }
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
                Resolve-AndCheck $toolInput.file_path
            }
            'NotebookEdit' {
                $fp = $toolInput.notebook_path
                if ([string]::IsNullOrWhiteSpace($fp)) { $fp = $toolInput.file_path }
                Resolve-AndCheck $fp
            }
            'Bash' {
                $cmd = [string]$toolInput.command
                if ($null -eq $cmd) { $cmd = '' }

                # git stash family (#192): refs/stash is repo-wide, not worktree-scoped.
                if ($cmd -match '(^|[;&|]|\s)git\s+stash(\s|$)') {
                    Block "'git stash' is repo-wide, not worktree-scoped -- a concurrent task's stash can silently cross-contaminate this worktree. Use: git diff > TEMP/mine.patch then git checkout -- <files> to test baseline, then git apply TEMP/mine.patch to restore."
                }

                if ($cmd -match '(^|[;&|]|\s)git\s+worktree\s+add\s') {
                    $rest = ($cmd -replace '.*git\s+worktree\s+add\s+', '')
                    $tokens = $rest -split '\s+' | Where-Object { $_ -ne '' }
                    $i = 0
                    $wtPath = $null
                    while ($i -lt $tokens.Count) {
                        $t = $tokens[$i]
                        if ($t.StartsWith('-')) {
                            if ($t -eq '-b' -or $t -eq '-B') { $i++ }
                            $i++
                            continue
                        }
                        $wtPath = $t
                        break
                    }
                    Resolve-AndCheck $wtPath
                }

                if ($cmd -match '(^|[;&|]|\s)git\s+checkout\s.*--\s+(?<rest>.+)$') {
                    $rest = $Matches['rest']
                    foreach ($p in ($rest -split '\s+' | Where-Object { $_ -ne '' })) {
                        Resolve-AndCheck $p
                    }
                }

                $redirMatches = [regex]::Matches($cmd, '>>?\s*([^\s&|;]+)')
                if ($redirMatches.Count -gt 0) {
                    Resolve-AndCheck $redirMatches[$redirMatches.Count - 1].Groups[1].Value
                }

                if ($cmd -match '(^|[;&|]|\s)tee\s+(-a\s+)?(?<p>[^\s&|;]+)') {
                    Resolve-AndCheck $Matches['p']
                }

                if ($cmd -match '(^|[;&|]|\s)(cp|mv)\s+(?<args>.+)$') {
                    $cpArgs = $Matches['args'] -split '\s+' | Where-Object { $_ -ne '' }
                    if ($cpArgs.Count -gt 0) {
                        Resolve-AndCheck $cpArgs[$cpArgs.Count - 1]
                    }
                }
            }
        }

        exit 0

        """;
    }
}
