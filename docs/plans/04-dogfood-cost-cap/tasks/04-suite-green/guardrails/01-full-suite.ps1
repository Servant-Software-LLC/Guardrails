# catches: the cost-cap work regressing anything elsewhere in the suite (this is the ONE
#          place a whole-suite green check belongs - a terminal integration gate)
dotnet build Guardrails.sln -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Output "Solution does not build at the terminal gate."; exit 1 }
dotnet test Guardrails.sln -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { Write-Output "The full test suite has failures after the cost-cap work."; exit 1 }
exit 0
