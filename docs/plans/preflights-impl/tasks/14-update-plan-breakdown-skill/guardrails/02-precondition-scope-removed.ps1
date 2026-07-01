# catches: the migration leaving the retired `scope:"precondition"` marker in the skill's guidance - no
#          third scope value exists under this model (scopes are integration | local). Fail-on-present
#          (negative assertion, #176) for the quoted scope value, scoped to the one skill directory this
#          task owns. A prose note that simply names the removal without quoting the value does not trip it.
$dir = ".claude/skills/plan-breakdown"
$hits = Get-ChildItem $dir -Recurse -File -Include *.md |
    Where-Object { (Get-Content $_.FullName -Raw) -match '["'']precondition["'']' }
if ($hits) {
    $names = ($hits | ForEach-Object { $_.Name }) -join ', '
    Write-Output "plan-breakdown skill still uses the retired scope value ""precondition"" in [$names] - remove it (scopes are integration | local under this model)"
    exit 1
}
exit 0
