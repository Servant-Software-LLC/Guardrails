# catches: the one change that satisfies 01-build-passes while doing none of the work — giving Role a
#          default (`= PromptRole.Action`), a nullable type, or dropping `required`. Any of those makes
#          the whole solution compile without a single construction site being touched, so the build
#          gate goes green over an empty change. §3.4 is explicit that this is the failure: "A default
#          would let a new call site silently acquire the permissive value. The compiler is the gate."
#          With `required` and no default asserted HERE, 01-build-passes then proves every site was
#          found — the two guardrails only mean something together.
$ErrorActionPreference = 'Continue'

$invocation = Join-Path $env:GUARDRAILS_WORKSPACE 'src/Guardrails.Core/Prompts/PromptInvocation.cs'
if (-not (Test-Path $invocation)) {
    Write-Output "MISSING: src/Guardrails.Core/Prompts/PromptInvocation.cs"
    exit 1
}

$text = Get-Content $invocation -Raw
$failures = @()

# 1. The property is declared `required`, of the bare enum type. Matched with `required` BEFORE the type
#    so `public PromptRole Role` (no required) does not satisfy it.
if ($text -notmatch 'public\s+required\s+PromptRole\s+Role\s*\{\s*get;\s*init;\s*\}') {
    $failures += 'PromptInvocation does not declare: public required PromptRole Role { get; init; }' 
}

# 2. No default/initializer on it. This is the gaming vector, so it is checked as its own clause rather
#    than folded into the pattern above — an initializer would still leave clause 1 matching.
if ($text -match 'PromptRole\s+Role\s*\{\s*get;\s*init;\s*\}\s*=') {
    $failures += 'Role carries a default initializer (= ...). Section 3.4 forbids it: a default lets a new call site silently acquire the permissive value.' 
}

# 3. Not nullable — `PromptRole?` would make every un-set site compile, which is a default by another name.
if ($text -match 'PromptRole\?\s+Role') {
    $failures += "Role is nullable (PromptRole?). That is a default by another name — every un-set construction site would compile."
}

# 4. The enum exists with exactly the three §3.4 members, wherever it was declared.
$enumFile = Get-ChildItem -Path (Join-Path $env:GUARDRAILS_WORKSPACE 'src/Guardrails.Core/Prompts') -Filter '*.cs' |
    Where-Object { (Get-Content $_.FullName -Raw) -match 'enum\s+PromptRole' } |
    Select-Object -First 1

if ($null -eq $enumFile) {
    $failures += 'No "enum PromptRole" found under src/Guardrails.Core/Prompts/.' 
}
else {
    $enumText = Get-Content $enumFile.FullName -Raw
    foreach ($member in @('Action', 'Guardrail', 'Advisory')) {
        if ($enumText -notmatch "(?m)^\s*$member\s*,?\s*$") {
            $failures += "enum PromptRole is missing the '$member' member (§3.4 names exactly Action, Guardrail, Advisory)."
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== The role seam is not shaped as §3.4 requires ==="
    foreach ($f in $failures) { Write-Output "  - $f" }
    Write-Output ""
    Write-Output 'Fix PromptInvocation.cs so Role is: public required PromptRole Role { get; init; } -- with NO default and NO trailing ? -- then set Role = PromptRole.Action at every construction site the compiler names.' 
    exit 1
}

Write-Output "Role is required, non-nullable and un-defaulted; enum PromptRole carries Action/Guardrail/Advisory."
exit 0
