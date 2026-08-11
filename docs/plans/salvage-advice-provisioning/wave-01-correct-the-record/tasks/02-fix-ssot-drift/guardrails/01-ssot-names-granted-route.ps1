# catches: the SSOT still specifying the ungranted 'git checkout <ref> -- <path>' as the SOME-recovery
#          route, so the contract and the harness's actual feedback disagree.
$f = 'docs/plans/02-schemas-and-contracts.md'
if (-not (Test-Path $f)) { Write-Output "$f not found"; exit 1 }
$c = Get-Content -Raw -Path $f
if ($c -match 'git checkout <ref> -- <path>') {
    Write-Output "$f still names 'git checkout <ref> -- <path>' as the SOME-recovery route - it is not granted by the read-only default; it must read 'git show <ref>:<path>'"
    exit 1
}
if ($c -notmatch 'git show <ref>:<path>') {
    Write-Output "$f does not name the corrected SOME-recovery route 'git show <ref>:<path>'"
    exit 1
}
exit 0
