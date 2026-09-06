$script = $args[0]
$rest   = @(if ($args.Count -gt 1) { $args[1..($args.Count - 1)] } else { @() })
try {
    & $script @rest
} catch {
    [Console]::Error.WriteLine("GUARDRAILS-ABORT: the guardrail did not run to completion -- " + $_.Exception.Message)
    exit 97
}
if ($null -eq $LASTEXITCODE) { exit 0 }
exit $LASTEXITCODE
