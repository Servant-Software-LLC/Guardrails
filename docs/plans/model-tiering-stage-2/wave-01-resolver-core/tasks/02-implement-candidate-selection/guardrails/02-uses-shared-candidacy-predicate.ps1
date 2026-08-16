# catches: the SHAPE failures a grep can actually prove. Everything about whether the floor HOLDS at
#          runtime is behavioural and belongs to this task's tests (task 01 requires the costly floor
#          asserted at BOTH the own rung and the climbed-to rung) - not to a regex.
#
#   (1) RE-IMPLEMENTING the candidacy predicate instead of calling PromptRunnerConfig.ServesTier.
#       D22a makes the single predicate a CORRECTNESS requirement: if validate's GR2048 check and the
#       runtime resolver disagree about which blocks serve a rung, validation passes and every task
#       at that rung dies at runtime on no-route.
#   (2) A boolean BYPASS parameter. The costly floor is the one rule with no override, so a bool
#       parameter on a method in these files is an opt-in switch by construction, whatever it is
#       named - which is why this bans the SHAPE and not a list of guessed identifiers.
#
# NOT banned, deliberately: `DeclaresTier` and `Costly` comparisons. An earlier version banned both
# and thereby made a REQUIRED deliverable uncomputable - the D28 binding-ceiling datum is
# definitionally `DeclaresTier AND NOT ServesTier`, and the harness itself computes exactly that in
# GR2048 (`r.Costly is true && atOrAbove.Any(r.DeclaresTier)`, PlanValidator.cs). Banning them left
# only obfuscated spellings (`Costly.GetValueOrDefault()`, `Array.IndexOf(Tiers.ToArray(), rung)`)
# that evade the regex AND ship the duplication D22a exists to prevent. Strictly worse than allowing
# the honest formulation.
$paths = @(
    "src/Guardrails.Core/Prompts/TierResolver.cs",
    "src/Guardrails.Core/Prompts/TierResolution.cs"    # same writeScope - a ban scoped to one file is moved-one-file-over
)
$failed = $false
foreach ($path in $paths) {
    if (-not (Test-Path $path)) {
        if ($path -like "*TierResolver.cs") { Write-Output "$path does not exist"; exit 1 }
        continue    # TierResolution.cs may legitimately be folded into TierResolver.cs
    }
    $raw    = Get-Content -Raw -Path $path
    $noRaw  = [regex]::Replace($raw, '"""[\s\S]*?"""', '""')      # C# 11 raw string literals
    $noVerb = [regex]::Replace($noRaw, '@"(?:[^"]|"")*"', '""')
    $noStr  = [regex]::Replace($noVerb, '"(\\.|[^"\\])*"', '""')
    $code   = [regex]::Replace($noStr, '/\*[\s\S]*?\*/', '')
    $code   = [regex]::Replace($code, '(?m)//.*$', '')

    # (1) NEGATIVE: the membership test IS the predicate's body. Case-sensitive - these are C#
    # identifiers, and PowerShell's -match is not case-sensitive by default.
    if ($code -cmatch '\.Tiers\s*\.\s*(?:Contains|Any|Exists|IndexOf)\s*\(') {
        Write-Output "$path tests routing.Tiers membership directly - that IS the candidacy predicate. Call ServesTier(tier) instead, so validate's GR2048 check and the runtime resolver can never disagree about which blocks serve a rung (D22a). If you need the D28 binding-ceiling datum, compute it from DeclaresTier (that is what DeclaresTier is for) - do not re-derive membership."
        $failed = $true
    }

    # (2) NEGATIVE: any bool parameter. No `= true|false` default is required - an earlier version
    # demanded one and a plain `bool allowCostly` walked straight through.
    if ($code -cmatch '(?m)^\s*(?:public|internal|private|protected|static|\s)*[\w<>,\[\]\?]+\s+\w+\s*\([^)]*\bbool\s+\w+\s*[,)]') {
        Write-Output "$path declares a method with a boolean parameter - the costly floor admits NO override, no flag and no dial (D22). Remove it; a human reaches a costly model by PINNING it per task (6.1 item 1), which never enters this resolver."
        $failed = $true
    }
}

# POSITIVE: the shared predicate must actually be called. Kept deliberately simple - an earlier
# version demanded a LINQ shape and false-RED a semantically identical `foreach` + `if (!ServesTier)`
# implementation. Whether ServesTier is what actually SELECTS is proven by this task's tests, which
# assert the costly floor at both the own rung and the climbed-to rung.
$resolver = Get-Content -Raw -Path "src/Guardrails.Core/Prompts/TierResolver.cs"
if ($resolver -cnotmatch '\.ServesTier\s*\(') {
    Write-Output "src/Guardrails.Core/Prompts/TierResolver.cs never calls PromptRunnerConfig.ServesTier - the resolver MUST consume the ONE shared candidacy predicate (D22a), not its own copy."
    $failed = $true
}

if ($failed) { exit 1 }
exit 0
