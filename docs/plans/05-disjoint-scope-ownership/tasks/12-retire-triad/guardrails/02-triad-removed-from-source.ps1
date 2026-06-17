# catches: M7's "clean removal" being only partial - the build can stay green while a stray
#          captureHashes/restoreOnRetry/tests-untouched reference lingers in the loader, model, or
#          retry path. Greps the SOURCE .cs tree only, EXCLUDING bin/obj shadow copies (the .NET
#          grep-scope trap, stacks/dotnet.md §5) which hold stale compiled copies until a rebuild.
$srcFiles = Get-ChildItem -Path "src/Guardrails.Core" -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
$pattern = 'captureHashes|CaptureHashes|restoreOnRetry|RestoreOnRetry|tests-untouched|IsTestsUntouched'
$offenders = $srcFiles | Where-Object { (Get-Content $_.FullName -Raw) -match $pattern }
if ($offenders) {
    $names = ($offenders | ForEach-Object { $_.FullName.Substring($_.FullName.IndexOf('src')) }) -join ', '
    Write-Output "Triad references still present in source after M7 removal: $names - remove every captureHashes/restoreOnRetry/tests-untouched reference."
    exit 1
}
exit 0
