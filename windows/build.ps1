$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
dotnet test .\OpenUsage.sln
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build .\OpenUsage.App\OpenUsage.App.csproj -c Release
Write-Host "Built: $(Join-Path $PSScriptRoot 'OpenUsage.App\bin\Release\net8.0-windows\OpenUsage.exe')"
