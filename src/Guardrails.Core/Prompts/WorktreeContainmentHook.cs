using System.Text;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
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
/// process Claude Code spawns directly, not a .NET callback). Both scripts are pure string
/// normalization with NO symlink resolution and no external <c>realpath</c>/<c>readlink</c>
/// dependency — identical behavior on every OS, deliberately consistent rather than the bash
/// side attempting (and, on macOS's BSD coreutils, silently failing at) a "stronger" symlink-aware
/// check. What the scripts DO get is a LIST of accepted root spellings, canonicalised once in C#
/// at generation time (<see cref="AcceptedRoots"/>) — the symlink knowledge lives on the .NET side
/// of the boundary, where it is portable, and the scripts stay pure string comparison against N
/// literals instead of one. It ALSO blocks the <c>git stash</c>
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
    /// The root spellings the generated script ACCEPTS a candidate path under (issue #464): the root
    /// exactly as the harness spelled it (lexically normalised) first — the PRIMARY, used for joining
    /// relative candidates and for naming the worktree in the block message — followed by its fully
    /// symlink-resolved form (<see cref="RealPath.Resolve"/>) when that differs. Deduped with
    /// <see cref="RealPath.Comparison"/>, so on a filesystem with no links in play there is exactly one
    /// entry and the script behaves precisely as it did before this existed.
    ///
    /// <para><b>Why a LIST.</b> One directory can have more than one absolute spelling. On macOS
    /// <c>/var</c> is a symlink to <c>/private/var</c> and <see cref="Path.GetTempPath"/> lives under
    /// it, so the harness derives a worktree root spelled <c>/var/folders/…/wt</c> while the very same
    /// directory is spelled <c>/private/var/folders/…/wt</c> by anything that resolves it — git, the
    /// OS's own idea of the agent's working directory. Baking ONE literal and comparing by pure string
    /// normalisation then refuses a perfectly legitimate write inside the agent's own worktree, exit 2,
    /// on every write of every task — and because the hook's job is to block, it reads as the hook
    /// working correctly. Resolving symlinks in the SCRIPTS is not an option: that is exactly the
    /// <c>realpath -m</c> regression documented on <see cref="BashScript"/> (GNU-only flag, 13
    /// macOS-only CI failures). So the resolution happens ONCE, here, in portable .NET, and the scripts
    /// receive data rather than a new rule.
    /// </para>
    ///
    /// <para><b>This can only ever turn a WRONG block into an allow.</b> Every added spelling names the
    /// SAME directory as the primary — a path accepted under the resolved root IS a path inside the
    /// worktree, reached by another name. Nothing is removed: the rooted-path rejection, the
    /// <c>.</c>/<c>..</c> collapse and the directory-boundary comparison run against EACH entry
    /// unchanged, so a genuine escape is still blocked no matter how many entries the list has (and a
    /// sibling such as <c>…/wt-evil</c> is still not under <c>…/wt</c> in any spelling).
    /// </para>
    ///
    /// <para><b>Bounded gap — what this does NOT cover.</b> The set is
    /// <c>{as-given, resolved(as-given)}</c>, which covers the direction where the as-given root is the
    /// ALIAS (both spellings are then enumerable, and both are accepted). It does NOT cover the inverse
    /// — a root baked in its CANONICAL spelling while a candidate arrives through an alias — because
    /// canonical→alias is not enumerable in general: a directory can be reachable through arbitrarily
    /// many links, and nothing can list them from the target end. That inverse is unreachable HERE by
    /// construction rather than by luck, and the mechanism is worth naming because it is the whole
    /// reason a two-element set suffices: the root baked in is always the harness's OWN spelling, never
    /// git's. Both call sites (<c>ActionRunner</c>, <c>GuardrailRunner</c>) receive one value —
    /// <c>WorktreeHandle.WorktreePath</c>, via <c>TaskExecutor</c> — and every producer of that
    /// property in <c>GitWorktreeProvider</c> builds it with
    /// <see cref="Path.Combine(string, string)"/> under the run's worktree root (fresh segment, fork)
    /// or copies another handle's already-built string (reuse). Nothing reads a segment path back out
    /// of <c>git worktree list</c>; the one path in that provider that DOES come from git is the
    /// resume-adopted INTEGRATION worktree, a different type that never reaches this method (its
    /// re-verification runs script guardrails only, so no hook is generated for it). And the baked
    /// string is byte-for-byte the child process's working directory, so the agent's own
    /// relative→absolute resolution starts from the spelling that is baked. A CANONICAL candidate can
    /// still arrive — the OS resolves a cwd, <c>git rev-parse --show-toplevel</c> and <c>pwd -P</c>
    /// print resolved paths, an agent may echo one back — and that is precisely the direction the
    /// resolved entry covers.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> AcceptedRoots(string worktreeRoot)
    {
        var accepted = new List<string>(2);
        AddDistinct(accepted, Lexical(worktreeRoot));
        AddDistinct(accepted, RealPath.Resolve(worktreeRoot));
        if (accepted.Count == 0)
        {
            accepted.Add(worktreeRoot); // degenerate input — keep the caller's own spelling rather than nothing
        }

        return accepted;
    }

    private static void AddDistinct(List<string> accepted, string spelling)
    {
        if (spelling.Length == 0)
        {
            return;
        }

        foreach (string existing in accepted)
        {
            if (string.Equals(existing, spelling, RealPath.Comparison))
            {
                return;
            }
        }

        accepted.Add(spelling);
    }

    /// <summary>
    /// <see cref="Path.GetFullPath(string)"/> normalisation (separators, <c>.</c>/<c>..</c>, no trailing
    /// separator) that never throws — the literal spelling stands for anything <see cref="Path"/> refuses.
    /// </summary>
    private static string Lexical(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return path;
        }
    }

    /// <summary>
    /// The accepted roots as one newline-separated block of SINGLE-quoted shell literals, ready to drop
    /// inside a bash <c>ARRAY=( … )</c>.
    /// <para>
    /// Single quotes, not the double quotes the root used to be interpolated into raw: inside <c>'…'</c>
    /// bash performs no expansion at all, so a path containing <c>$</c>, a backtick, a backslash or a
    /// double quote is inert rather than executed or mangled. The one character that must be broken out
    /// is <c>'</c> itself, via the standard <c>'\''</c> idiom. (A path with any of these is unusual, not
    /// impossible — and this file emits N literals now instead of one.)
    /// </para>
    /// </summary>
    private static string BashRootLiterals(string worktreeRoot) =>
        string.Join("\n", AcceptedRoots(worktreeRoot).Select(BashLiteral));

    private static string BashLiteral(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// The accepted roots as one newline-separated block of SINGLE-quoted PowerShell literals, ready to
    /// drop inside <c>@( … )</c> (newlines separate elements there).
    /// <para>
    /// Single quotes for the same reason as <see cref="BashRootLiterals"/>, and it fixes a real hole: the
    /// root used to be emitted into a DOUBLE-quoted PowerShell string with only the backtick and the
    /// double quote escaped, so a perfectly legal Windows path containing <c>$</c> (say
    /// <c>C:\build\$tmp\wt</c>) was interpolated as a variable and silently became something else. A
    /// single-quoted string interpolates nothing; only <c>'</c> needs doubling.
    /// </para>
    /// </summary>
    private static string PowerShellRootLiterals(string worktreeRoot) =>
        string.Join("\n", AcceptedRoots(worktreeRoot).Select(PowerShellLiteral));

    private static string PowerShellLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// The bash hook script body (issue #199/#192), the accepted worktree-root spellings
    /// (<see cref="AcceptedRoots"/>) baked in as single-quoted literals. Reads the
    /// PreToolUse tool-call JSON from stdin via a small dependency-free field extractor (no <c>jq</c>
    /// assumed on the agent's PATH); exit 2 + stderr is Claude Code's documented block contract.
    ///
    /// <para>Path canonicalization is a pure, dependency-free string-based <c>.</c>/<c>..</c> segment
    /// collapse — the LITERAL bash mirror of <see cref="WorkspaceContainment.Escapes"/> (rooted-path
    /// rejection, normalize, directory-boundary comparison), exactly like the PowerShell script below.
    /// It does NOT call an external <c>realpath</c>/<c>readlink</c>: an earlier version shelled out to
    /// <c>realpath -m</c>, but <c>-m</c> is GNU-coreutils-only — macOS ships BSD <c>realpath</c>, which
    /// does not support it, so on macOS the call silently misbehaved and escape detection went dark
    /// (13 macOS-only CI failures, all "expected block, got allow"). Neither platform's script resolves
    /// symlinks now — that is a known, accepted, CONSISTENT gap (see <see cref="PowerShellScript"/>'s
    /// doc comment), not an asymmetry between them, and it trades away zero portability for a rule that
    /// cannot silently diverge by core-utils flavor again.</para>
    ///
    /// <para>What the script gets INSTEAD of symlink resolution is a baked ARRAY of accepted root
    /// spellings (issue #464): the same equality/directory-boundary test, run once per entry, allowing
    /// on the first hit and falling through to <c>block</c> only when NONE match. The symlink knowledge
    /// is computed in C# by <see cref="AcceptedRoots"/>, where it is portable; the script's own rule is
    /// unchanged and still pure string comparison. The PowerShell twin's <c>Test-Escapes</c> loops over
    /// the identical list in the identical order — whatever happens to this <c>case</c> happens
    /// there.</para>
    /// </summary>
    internal static string BashScript(string worktreeRoot) => $$"""
        #!/usr/bin/env bash
        # Guardrails worktree-containment PreToolUse hook (issue #199 / #192). Generated per attempt;
        # the accepted worktree-root spellings below are literals baked in at generation time, not read
        # from the environment. There is a LIST rather than one root because a single directory can have
        # more than one absolute spelling (macOS: /var/folders/... and /private/var/folders/... name the
        # same place), and this script deliberately does NOT resolve symlinks -- see the C# AcceptedRoots
        # helper (issue #464). Adding a spelling is a DATA change here, never a control-flow one.
        set -u

        ACCEPTED_ROOTS=(
        {{BashRootLiterals(worktreeRoot)}}
        )

        # The PRIMARY spelling: what a relative candidate is joined to, and what a block message names.
        WORKTREE_ROOT="${ACCEPTED_ROOTS[0]}"

        input="$(cat)"

        extract() {
          # $1 = field name; extracts the first "<name>":"<value>" match (top-level or nested), then
          # unescapes \" and \\ — the two escapes a path can realistically contain.
          #
          # NOTE: this uses `sed -E` (POSIX extended regex) deliberately, NOT the default basic
          # regex (BRE) mode. An earlier version used BRE with `\|` for alternation inside a `\(...\)`
          # group -- `\|` as alternation in BRE is a GNU sed extension, unsupported by BSD sed (the
          # stock /usr/bin/sed on macOS), where the substitution silently failed to match and this
          # function returned empty for EVERY field. `-E` is POSIX and behaves identically on GNU and
          # BSD sed. Validated with `sed --posix` (GNU's BSD-sed emulation) to confirm no other
          # GNU-only extension is in play.
          printf '%s' "$input" | sed -En 's/.*"'"$1"'"[[:space:]]*:[[:space:]]*"(([^"\\]|\\.)*)".*/\1/p' | head -n1 \
            | sed 's/\\"/"/g; s/\\\\/\\/g'
        }

        tool_name="$(extract tool_name)"

        block() {
          echo "BLOCKED by Guardrails worktree-containment hook: $1" >&2
          exit 2
        }

        # Pure string-based '.'/'..' segment collapse over an ABSOLUTE path -- no realpath/readlink,
        # no symlink resolution, no GNU-vs-BSD flag dependency. Mirrors Path.GetFullPath's
        # normalization: walk segments left-to-right, push non-'.'/'..' segments, a '..' pops the
        # last pushed segment (dropped if the stack is already empty -- never pops above the root),
        # a bare '.' is dropped. Input MUST already be absolute (callers join with WORKTREE_ROOT
        # first); output is always absolute, "/" at minimum.
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

        # Normalize every accepted root ONCE, up front (bash 3.2-safe: the array is never empty, so the
        # `set -u` expansion of "${ACCEPTED_ROOTS[@]}" is always defined).
        ROOT_NORMS=()
        for accepted_root in "${ACCEPTED_ROOTS[@]}"; do
          accepted_norm="$(normalize_path "$accepted_root")"
          ROOT_NORMS+=("${accepted_norm%/}")
        done
        ROOT_PRIMARY="${ROOT_NORMS[0]}"

        resolve_and_check() {
          local candidate="$1"
          [ -z "$candidate" ] && return 0

          local absolute
          if [[ "$candidate" = /* ]]; then
            absolute="$candidate"
          else
            absolute="$WORKTREE_ROOT/$candidate"
          fi

          local resolved
          resolved="$(normalize_path "$absolute")"

          # Same rule as before, once per accepted spelling: equality, or nesting on a DIRECTORY
          # boundary (so a sibling '<root>-evil' is never under '<root>', in any spelling).
          local root_norm
          for root_norm in "${ROOT_NORMS[@]}"; do
            case "$resolved" in
              "$root_norm"|"$root_norm"/*) return 0 ;;
            esac
          done

          block "path '$candidate' resolves to '$resolved', outside the task worktree '$ROOT_PRIMARY'"
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
            #
            # The MESSAGE must name only a route the agent can actually take (#382). The scratch-patch
            # recipe this used to hand back was refused three ways over: its redirect target sat outside
            # the worktree, which the redirect check further down THIS script blocks (the PowerShell
            # twin's relative target landed INSIDE the worktree instead, where it then failed the
            # write-scope check as an out-of-scope path), and both git verbs it leaned on are ungranted
            # on a clean box. Bash(git show*) is the one git verb the harness injects unconditionally
            # (ClaudePromptRunner.SalvageInspectionGrant), so that plus the agent's own file-editing
            # tools is the whole recipe -- the same route RetryPolicy's salvage section prescribes.
            if printf '%s' "$cmd" | grep -Eq '(^|[;&|]|[[:space:]])git[[:space:]]+stash([[:space:]]|$)'; then
              block "'git stash' is repo-wide, not worktree-scoped -- a concurrent task's stash can silently cross-contaminate this worktree. You do not need it: your edits are uncommitted, so the committed tree is already the clean baseline. To read a file as it was committed, run: git show 'HEAD:<path>' -- git show is the one git verb this harness grants unconditionally -- and write that content over the file with your own file-editing tool; put your own version back the same way afterwards. If you must park text on disk in between, keep the scratch file INSIDE this worktree AND inside your task's writeScope: a path outside the worktree is blocked by this same hook, and an in-worktree path outside your writeScope fails the write-scope check. Do not reach for git diff or git apply, and do not try to undo your edits with a git write verb -- none of those are granted unless this task's allowedTools declares them, so do not spend turns on them."
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

            # 'git checkout' with a pathspec after the '--' separator (restoring a path from another
            # commit/branch into place). Matcher unchanged -- only the wording of this note.
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
    /// The PowerShell hook script body (issue #199/#192), the accepted worktree-root spellings
    /// (<see cref="AcceptedRoots"/>) baked in as single-quoted literals. This
    /// is the LITERAL mirror of <see cref="WorkspaceContainment.Escapes"/> (rooted-path rejection,
    /// <c>GetFullPath</c> normalization, directory-boundary comparison) — no symlink resolution,
    /// exactly matching the reused C# decision function's semantics.
    ///
    /// <para><c>Test-Escapes</c> loops over the SAME accepted-root array, in the same order, with the
    /// same "any match wins" semantics as the bash <c>case</c> (issue #464). The two scripts are kept
    /// behaviourally identical on purpose: the rule must not be able to diverge by platform, which is
    /// exactly how the <c>realpath -m</c> regression documented on <see cref="BashScript"/> happened.
    /// </para>
    /// </summary>
    internal static string PowerShellScript(string worktreeRoot)
    {
        return $$"""
        # Guardrails worktree-containment PreToolUse hook (issue #199 / #192). Generated per attempt;
        # the accepted worktree-root spellings below are literals baked in at generation time, not read
        # from the environment. There is a LIST rather than one root because a single directory can have
        # more than one absolute spelling (a junctioned or symlinked temp dir), and this script
        # deliberately does NOT resolve symlinks -- see the C# AcceptedRoots helper (issue #464).
        $ErrorActionPreference = 'Stop'

        $AcceptedRoots = @(
        {{PowerShellRootLiterals(worktreeRoot)}}
        )

        $stdin = [Console]::In.ReadToEnd()

        function Block([string]$reason) {
            [Console]::Error.WriteLine("BLOCKED by Guardrails worktree-containment hook: $reason")
            exit 2
        }

        $acceptedFull = @()
        foreach ($acceptedRoot in $AcceptedRoots) {
            $acceptedFull += [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($acceptedRoot))
        }

        # The PRIMARY spelling: what a relative candidate is joined to, and what a block message names.
        $rootFull = $acceptedFull[0]

        function Test-Escapes([string]$candidate) {
            if ([string]::IsNullOrWhiteSpace($candidate)) { return $false }

            if (-not [System.IO.Path]::IsPathRooted($candidate)) {
                $candidate = Join-Path $rootFull $candidate
            }

            $resolved = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($candidate))

            # Same rule as before, once per accepted spelling: equality, or nesting on a DIRECTORY
            # boundary (so a sibling '<root>-evil' is never under '<root>', in any spelling).
            foreach ($root in $acceptedFull) {
                if ($resolved -ieq $root) { return $false }
                if ($resolved.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { return $false }
            }

            return $true
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

                # git stash family (#192): refs/stash is repo-wide, not worktree-scoped. The message is
                # kept WORD-FOR-WORD identical to the bash twin's (see its comment for why the old
                # scratch-patch recipe was self-defeating on both platforms) -- one rule, one wording.
                if ($cmd -match '(^|[;&|]|\s)git\s+stash(\s|$)') {
                    Block "'git stash' is repo-wide, not worktree-scoped -- a concurrent task's stash can silently cross-contaminate this worktree. You do not need it: your edits are uncommitted, so the committed tree is already the clean baseline. To read a file as it was committed, run: git show 'HEAD:<path>' -- git show is the one git verb this harness grants unconditionally -- and write that content over the file with your own file-editing tool; put your own version back the same way afterwards. If you must park text on disk in between, keep the scratch file INSIDE this worktree AND inside your task's writeScope: a path outside the worktree is blocked by this same hook, and an in-worktree path outside your writeScope fails the write-scope check. Do not reach for git diff or git apply, and do not try to undo your edits with a git write verb -- none of those are granted unless this task's allowedTools declares them, so do not spend turns on them."
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
