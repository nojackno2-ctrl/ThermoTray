<#
.SYNOPSIS
    ThermoTray unified build, test, package, manifest verification, and release packaging script.
.DESCRIPTION
    Canonical release-quality PowerShell packaging and verification path used locally and in CI
    (.github/workflows/build.yml and release.yml).
    Execution steps:
    1. Read and validate Directory.Build.props version, Inno Setup fallback version, and Git tag guard.
    2. dotnet restore for runtime (win-x64).
    3. dotnet build (Release).
    4. dotnet test (unit test suite).
    5. dotnet format --verify-no-changes.
    6. dotnet publish (self-contained single-file).
    7. mt.exe verification of embedded requireAdministrator manifest.
    8. Inno Setup (ISCC.exe) installer compilation.
    9. Portable ZIP packaging (PDB excluded).
    10. SHA256 checksum generation (SHA256SUMS.txt).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Tag,

    [Parameter(Mandatory = $false)]
    [string]$Configuration = 'Release',

    [Parameter(Mandatory = $false)]
    [string]$Runtime = 'win-x64',

    [Parameter(Mandatory = $false)]
    [string]$IsccPath,

    [Parameter(Mandatory = $false)]
    [string]$MtPath,

    [Parameter(Mandatory = $false)]
    [switch]$SkipManifestCheck,

    [Parameter(Mandatory = $false)]
    [switch]$SkipInstaller,

    [Parameter(Mandatory = $false)]
    [switch]$SkipTests,

    [Parameter(Mandatory = $false)]
    [switch]$SkipFormat,

    [Parameter(Mandatory = $false)]
    [switch]$SkipRestore,

    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild,

    [Parameter(Mandatory = $false)]
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Write-Host "==> Repository root: $repoRoot" -ForegroundColor Cyan

# 1. Resolve product version from Directory.Build.props
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $propsPath)) {
    throw "Directory.Build.props not found at: $propsPath"
}

[xml]$propsXml = Get-Content -LiteralPath $propsPath
$version = $propsXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Version is not defined in Directory.Build.props"
}
$version = $version.Trim()
Write-Host "==> Resolved product version: $version" -ForegroundColor Green

# 2. Verify Inno Setup script default version agreement
$issPath = Join-Path $repoRoot 'installer/ThermoTray.iss'
if (Test-Path -LiteralPath $issPath) {
    $issContent = Get-Content -LiteralPath $issPath -Raw
    if ($issContent -match '#define\s+AppVersion\s+"([^"]+)"') {
        $issVersion = $matches[1]
        if ($issVersion -ne $version) {
            throw "Inno Setup installer default AppVersion '$issVersion' does not match Directory.Build.props version '$version'"
        }
        Write-Host "==> Inno Setup default AppVersion matches Directory.Build.props ($version)" -ForegroundColor Green
    } else {
        throw "Could not find '#define AppVersion' in $issPath"
    }
} else {
    Write-Warning "installer/ThermoTray.iss not found at $issPath"
}

# 3. Tag-version guard
$resolvedTag = $Tag
if ([string]::IsNullOrWhiteSpace($resolvedTag)) {
    if ($env:GITHUB_REF_TYPE -eq 'tag' -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_REF_NAME)) {
        $resolvedTag = $env:GITHUB_REF_NAME
    } elseif ($env:GITHUB_REF -and $env:GITHUB_REF.StartsWith('refs/tags/')) {
        $resolvedTag = $env:GITHUB_REF.Substring('refs/tags/'.Length)
    }
}

if (-not [string]::IsNullOrWhiteSpace($resolvedTag)) {
    $expectedTag = "v$version"
    if ($resolvedTag -ne $expectedTag) {
        throw "Git tag '$resolvedTag' does not match Directory.Build.props version '$version' (expected '$expectedTag')"
    }
    Write-Host "==> Tag-version guard verified: $resolvedTag == $expectedTag" -ForegroundColor Green
}

# 4. Export environment variables if running in GitHub Actions
if ($env:GITHUB_ENV -and (Test-Path -LiteralPath $env:GITHUB_ENV)) {
    Add-Content -LiteralPath $env:GITHUB_ENV -Value "AppVersion=$version" -Encoding utf8
    Add-Content -LiteralPath $env:GITHUB_ENV -Value "ReleaseTag=v$version" -Encoding utf8
    Write-Host "==> Exported AppVersion and ReleaseTag to GITHUB_ENV" -ForegroundColor DarkGray
}

$solutionPath = Join-Path $repoRoot 'ThermoTray.sln'
$projectPath = Join-Path $repoRoot 'src/ThermoTray/ThermoTray.csproj'
$publishDir = Join-Path $repoRoot "publish/$Runtime"
$artifactsDir = Join-Path $repoRoot 'artifacts'
$installerOutputDir = Join-Path $artifactsDir 'installer'
$verificationDir = Join-Path $artifactsDir 'verification'
$releaseDir = Join-Path $artifactsDir 'release'

function Remove-ValidatedDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedParent
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedParent = [IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean path outside '$resolvedParent': '$resolvedPath'"
    }
    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
}

# 5. Restore win-x64 asset graph
if (-not $SkipRestore) {
    Write-Host "==> Restoring solution for runtime: $Runtime..." -ForegroundColor Cyan
    & dotnet restore $solutionPath --runtime $Runtime
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
}

# 6. Build Release configuration
if (-not $SkipBuild) {
    Write-Host "==> Building solution ($Configuration)..." -ForegroundColor Cyan
    & dotnet build $solutionPath --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
}

# 7. Execute unit tests
if (-not $SkipTests) {
    Write-Host "==> Running unit tests ($Configuration)..." -ForegroundColor Cyan
    & dotnet test $solutionPath --configuration $Configuration --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }
}

# 8. Verify code formatting
if (-not $SkipFormat) {
    Write-Host "==> Verifying code formatting..." -ForegroundColor Cyan
    & dotnet format $solutionPath --verify-no-changes --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet format check failed with exit code $LASTEXITCODE" }
}

# 9. Publish self-contained single-file application
if (-not $SkipPublish) {
    Write-Host "==> Publishing self-contained application ($Runtime)..." -ForegroundColor Cyan
    Remove-ValidatedDirectory -Path $publishDir -ExpectedParent (Join-Path $repoRoot 'publish')
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
    & dotnet publish $projectPath --configuration $Configuration --runtime $Runtime --self-contained true -p:PublishSingleFile=true --no-restore --output $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

$exePath = Join-Path $publishDir 'ThermoTray.exe'
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Published executable not found at: $exePath"
}

# 10. Verify embedded requireAdministrator manifest
if (-not $SkipManifestCheck) {
    Write-Host "==> Verifying embedded administrator manifest..." -ForegroundColor Cyan
    $resolvedMt = $null
    if (-not [string]::IsNullOrWhiteSpace($MtPath) -and (Test-Path -LiteralPath $MtPath)) {
        $resolvedMt = $MtPath
    } else {
        $cmdMt = Get-Command 'mt.exe' -ErrorAction SilentlyContinue
        if ($cmdMt) {
            $resolvedMt = $cmdMt.Source
        } else {
            $sdkMt = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\mt.exe", "${env:ProgramFiles}\Windows Kits\10\bin\*\x64\mt.exe" -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending |
                Select-Object -First 1
            if ($sdkMt) {
                $resolvedMt = $sdkMt.FullName
            }
        }
    }

    if (-not $resolvedMt) {
        throw "Windows SDK Manifest Tool (mt.exe) was not found. Install Windows SDK or use -SkipManifestCheck."
    }

    Write-Host "    Using mt.exe: $resolvedMt" -ForegroundColor DarkGray
    New-Item -ItemType Directory -Force -Path $verificationDir | Out-Null
    $manifestXmlPath = Join-Path $verificationDir 'manifest.xml'

    & $resolvedMt "-inputresource:$exePath;#1" "-out:$manifestXmlPath"
    if ($LASTEXITCODE -ne 0) { throw "mt.exe extraction failed with exit code $LASTEXITCODE" }

    $manifest = Get-Content -LiteralPath $manifestXmlPath -Raw
    if ($manifest -notmatch 'requestedExecutionLevel\s+level="requireAdministrator"') {
        throw "The published executable does not require administrator elevation in its embedded manifest"
    }
    Write-Host "==> requireAdministrator manifest verified successfully" -ForegroundColor Green
}

# 11. Compile Inno Setup installer
$installerExePath = $null
if (-not $SkipInstaller) {
    Write-Host "==> Compiling Inno Setup installer..." -ForegroundColor Cyan
    $resolvedIscc = $null
    if (-not [string]::IsNullOrWhiteSpace($IsccPath) -and (Test-Path -LiteralPath $IsccPath)) {
        $resolvedIscc = $IsccPath
    } else {
        $cmdIscc = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
        if ($cmdIscc) {
            $resolvedIscc = $cmdIscc.Source
        } else {
            $knownIsccPaths = @(
                "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe",
                "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
                "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
            )
            foreach ($p in $knownIsccPaths) {
                if (Test-Path -LiteralPath $p) {
                    $resolvedIscc = $p
                    break
                }
            }
        }
    }

    if (-not $resolvedIscc) {
        throw "Inno Setup Compiler (ISCC.exe) was not found. Install Inno Setup 6 or use -SkipInstaller."
    }

    Write-Host "    Using ISCC.exe: $resolvedIscc" -ForegroundColor DarkGray
    New-Item -ItemType Directory -Force -Path $installerOutputDir | Out-Null

    & $resolvedIscc "/DAppVersion=$version" $issPath
    if ($LASTEXITCODE -ne 0) { throw "ISCC.exe compilation failed with exit code $LASTEXITCODE" }

    $expectedInstaller = Join-Path $installerOutputDir "ThermoTray-Setup-$version.exe"
    if (-not (Test-Path -LiteralPath $expectedInstaller)) {
        throw "Installer output was not created at expected location: $expectedInstaller"
    }
    $installerExePath = $expectedInstaller
    Write-Host "==> Installer compiled successfully: $expectedInstaller" -ForegroundColor Green
}

# 12. Create release directory, stage installer, and package PDB-free portable ZIP
Write-Host "==> Packaging release artifacts..." -ForegroundColor Cyan
Remove-ValidatedDirectory -Path $releaseDir -ExpectedParent $artifactsDir
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

if ($installerExePath -and (Test-Path -LiteralPath $installerExePath)) {
    Copy-Item -LiteralPath $installerExePath -Destination $releaseDir -Force
}

$portableZipPath = Join-Path $releaseDir "ThermoTray-$version-$Runtime-portable.zip"
if (Test-Path -LiteralPath $portableZipPath) {
    Remove-Item -LiteralPath $portableZipPath -Force
}

$publishFiles = Get-ChildItem -LiteralPath $publishDir -File | Where-Object {
    $_.Extension -ne '.pdb' -and
    $_.Name -notmatch '(?i)(?:^|[._-])(running|backup|old|temp|tmp)(?:[._-]|$)'
}
if ($publishFiles.Count -eq 0) {
    throw "No publish files found to package into portable archive"
}

Write-Host "    Archiving $($publishFiles.Count) files into portable ZIP (PDB excluded)..." -ForegroundColor DarkGray
Compress-Archive -Path ($publishFiles.FullName) -DestinationPath $portableZipPath -CompressionLevel Optimal -Force
Write-Host "==> Portable ZIP created: $portableZipPath" -ForegroundColor Green

# 13. Compute SHA256 checksums and write SHA256SUMS.txt
Write-Host "==> Generating SHA256 checksums..." -ForegroundColor Cyan
$releaseAssets = @($portableZipPath)
if ($installerExePath) { $releaseAssets += (Join-Path $releaseDir (Split-Path -Leaf $installerExePath)) }
$checksumLines = $releaseAssets |
    ForEach-Object { Get-Item -LiteralPath $_ } |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$hash  $($_.Name)"
    }

$sha256SumsPath = Join-Path $releaseDir 'SHA256SUMS.txt'
[System.IO.File]::WriteAllLines($sha256SumsPath, $checksumLines, [System.Text.Encoding]::UTF8)

Write-Host ""
Write-Host "================ Packaging Complete ================" -ForegroundColor Green
Write-Host "Version:      $version"
Write-Host "Release Dir:  $releaseDir"
Write-Host "Artifacts:"
foreach ($line in $checksumLines) {
    Write-Host "  $line" -ForegroundColor Yellow
}
Write-Host "=====================================================" -ForegroundColor Green

return [PSCustomObject]@{
    Version         = $version
    ReleaseDir      = $releaseDir
    InstallerPath   = $installerExePath
    PortableZipPath = $portableZipPath
    ChecksumsPath   = $sha256SumsPath
}
