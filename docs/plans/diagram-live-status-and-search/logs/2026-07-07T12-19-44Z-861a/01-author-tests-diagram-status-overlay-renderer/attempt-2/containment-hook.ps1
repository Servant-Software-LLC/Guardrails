# Guardrails worktree-containment PreToolUse hook (issue #199 / #192). Generated per attempt;
# the worktree root below is a literal baked in at generation time, not read from the environment.
$ErrorActionPreference = 'Stop'

$WorktreeRoot = "C:\Users\David\AppData\Local\Temp\guardrails-worktrees\diagram-live-status-and-search-80c8913d\71e1ab5c\01-author-tests-diagram-status-overlay-renderer\attempt-1"

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
