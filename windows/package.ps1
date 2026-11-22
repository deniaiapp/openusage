[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw "Choose a new output directory: $output"
}

function Publish-OpenUsage([string]$Project, [string]$Folder) {
    $projectFile = Join-Path $PSScriptRoot "$Project/$Project.csproj"
    $destination = Join-Path $output $Folder
    dotnet publish $projectFile --configuration Release --runtime win-x64 --self-contained true --output $destination
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project (exit $LASTEXITCODE)"
    }
}

Publish-OpenUsage OpenUsage.App app
Publish-OpenUsage OpenUsage.Cli cli
