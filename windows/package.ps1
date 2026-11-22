[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw "Choose a new output directory: $output"
}
if ($Zip) {
    $archive = $output.TrimEnd([char[]]'\/') + ".zip"
    if (Test-Path -LiteralPath $archive) {
        throw "Choose an output directory without an existing ZIP: $archive"
    }
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

if ($Zip) {
    if (-not ('System.IO.Compression.ZipFile' -as [type])) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
    }
    $tempArchive = "$archive.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.Compression.ZipFile]::CreateFromDirectory($output, $tempArchive)
        [IO.File]::Move($tempArchive, $archive)
    }
    finally {
        [IO.File]::Delete($tempArchive)
    }
    Write-Output "ZIP: $archive"
}
