# catches: source changes that do not compile (TreatWarningsAsErrors → a warning also fails)
dotnet build Guardrails.sln -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) {
  Write-Output "Solution does not build clean after the cost-cap implementation (build/warning failure)."
  exit 1
}
exit 0
