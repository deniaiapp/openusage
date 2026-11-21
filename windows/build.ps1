$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
dotnet test .\OpenUsage.sln
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build .\OpenUsage.sln -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built: $(Join-Path $PSScriptRoot 'OpenUsage.App\bin\Release\net8.0-windows\OpenUsage.exe')"
Write-Host "CLI:   $(Join-Path $PSScriptRoot 'OpenUsage.Cli\bin\Release\net8.0-windows\openusage.exe')"
