param(
    [string]$RepoRoot = "D:\Project\FixMyDeviceMonorepo",
    [string]$ShareRoot = "D:\Project\Backups"
)

$ErrorActionPreference = "Stop"

$agentInstallerSource = Join-Path $RepoRoot "FixMyDeviceAgent\FixMyDeviceSetup.exe"
$websiteSource = Join-Path $RepoRoot "flutter_app\build\web"
$windowsSource = Join-Path $RepoRoot "flutter_app\build\windows\x64\runner\Release"

$agentTarget = Join-Path $ShareRoot "Agent Exe"
$websiteTarget = Join-Path $ShareRoot "Website"
$windowsTarget = Join-Path $ShareRoot "Windows Exe"

function Reset-Directory {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Copy-DirectoryContents {
    param(
        [string]$Source,
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Missing source directory: $Source"
    }

    Reset-Directory -Path $Destination
    Copy-Item -LiteralPath (Join-Path $Source '*') -Destination $Destination -Recurse -Force
}

if (-not (Test-Path -LiteralPath $agentInstallerSource)) {
    throw "Missing installer file: $agentInstallerSource"
}

Reset-Directory -Path $agentTarget
Copy-Item -LiteralPath $agentInstallerSource -Destination (Join-Path $agentTarget "FixMyDeviceSetup.exe") -Force

Copy-DirectoryContents -Source $websiteSource -Destination $websiteTarget
Copy-DirectoryContents -Source $windowsSource -Destination $windowsTarget

Write-Host "Copied share artifacts successfully:"
Write-Host "  Agent installer -> $agentTarget"
Write-Host "  Website build   -> $websiteTarget"
Write-Host "  Windows build   -> $windowsTarget"
