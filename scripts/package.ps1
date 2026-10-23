<#
.SYNOPSIS
    ThermoTray unified build, test, package, manifest verification, and release packaging script.
.DESCRIPTION
    Canonical release-quality PowerShell packaging and verification path used locally and in CI
    (.github/workflows/build.yml and release.yml).
    Execution steps:
    1. Read the product version from Cargo.toml and validate the Inno Setup default and Git tag guard.
    2. cargo fmt --check, cargo clippy (warnings are errors), and cargo test.
    3. cargo build --release for the Rust application (ThermoTray.exe).
    4. dotnet format --verify-no-changes for the .NET sensor sidecar.
    5. dotnet publish of the sidecar (self-contained single file, ThermoTray.Sensors.exe) next to ThermoTray.exe.
    6. mt.exe verification of the embedded requireAdministrator manifest.
    7. Inno Setup (ISCC.exe) installer compilation.
    8. Portable ZIP packaging (PDB excluded).
    9. SHA256 checksum generation (SHA256SUMS.txt).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Tag,

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
    [switch]$SkipBuild,

    [Parameter(Mandatory = $false)]
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Write-Host "==> Repository root: $repoRoot" -ForegroundColor Cyan

# 1. Resolve product version from Cargo.toml ([package] version)
$cargoPath = Join-Path $repoRoot 'Cargo.toml'
if (-not (Test-Path -LiteralPath $cargoPath)) {
    throw "Cargo.toml not found at: $cargoPath"
}

$inPackage = $false
$version = $null
foreach ($line in Get-Content -LiteralPath $cargoPath) {
    if ($line -match '^\s*\[(.+)\]\s*$') {
        $inPackage = ($matches[1] -eq 'package')
        continue
    }
    if ($inPackage -and $line -match '^\s*version\s*=\s*"([^"]+)"') {
        $version = $matches[1].Trim()
        break
    }
}
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Version is not defined in the [package] section of Cargo.toml"
}
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Cargo.toml version '$version' must be MAJOR.MINOR.PATCH for Windows file versions"
}
Write-Host "==> Resolved product version: $version" -ForegroundColor Green

# 2. Verify Inno Setup script default version agreement
$issPath = Join-Path $repoRoot 'installer/ThermoTray.iss'
if (Test-Path -LiteralPath $issPath) {
    $issContent = Get-Content -LiteralPath $issPath -Raw
    if ($issContent -match '#define\s+AppVersion\s+"([^"]+)"') {
        $issVersion = $matches[1]
        if ($issVersion -ne $version) {
            throw "Inno Setup installer default AppVersion '$issVersion' does not match Cargo.toml version '$version'"
        }
        Write-Host "==> Inno Setup default AppVersion matches Cargo.toml ($version)" -ForegroundColor Green
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
        throw "Git tag '$resolvedTag' does not match Cargo.toml version '$version' (expected '$expectedTag')"
    }
    Write-Host "==> Tag-version guard verified: $resolvedTag == $expectedTag" -ForegroundColor Green
}

# 4. Export environment variables if running in GitHub Actions
if ($env:GITHUB_ENV -and (Test-Path -LiteralPath $env:GITHUB_ENV)) {
    Add-Content -LiteralPath $env:GITHUB_ENV -Value "AppVersion=$version" -Encoding utf8
    Add-Content -LiteralPath $env:GITHUB_ENV -Value "ReleaseTag=v$version" -Encoding utf8
    Write-Host "==> Exported AppVersion and ReleaseTag to GITHUB_ENV" -ForegroundColor DarkGray
}

$sidecarProject = Join-Path $repoRoot 'sensors/ThermoTray.Sensors/ThermoTray.Sensors.csproj'
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

# 5. Rust formatting, lints, and unit/integration tests
Push-Location $repoRoot
try {
    if (-not $SkipFormat) {
        Write-Host "==> Verifying Rust formatting..." -ForegroundColor Cyan
        & cargo fmt --all -- --check
        if ($LASTEXITCODE -ne 0) { throw "cargo fmt check failed with exit code $LASTEXITCODE" }

        Write-Host "==> Running Clippy (warnings are errors)..." -ForegroundColor Cyan
        & cargo clippy --all-targets --locked -- -D warnings
        if ($LASTEXITCODE -ne 0) { throw "cargo clippy failed with exit code $LASTEXITCODE" }

        Write-Host "==> Verifying sensor sidecar formatting..." -ForegroundColor Cyan
        & dotnet format $sidecarProject --verify-no-changes --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw "dotnet format check failed with exit code $LASTEXITCODE" }
    }

    if (-not $SkipTests) {
        Write-Host "==> Running Rust tests..." -ForegroundColor Cyan
        & cargo test --locked
        if ($LASTEXITCODE -ne 0) { throw "cargo test failed with exit code $LASTEXITCODE" }
    }

    # 6. Build the Rust application
    if (-not $SkipBuild) {
        Write-Host "==> Building ThermoTray.exe (release)..." -ForegroundColor Cyan
        & cargo build --release --locked
        if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE" }
    }
} finally {
    Pop-Location
}

# 7. Publish the sensor sidecar and stage both executables
if (-not $SkipPublish) {
    Write-Host "==> Publishing sensor sidecar ($Runtime)..." -ForegroundColor Cyan
    Remove-ValidatedDirectory -Path $publishDir -ExpectedParent (Join-Path $repoRoot 'publish')
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
    & dotnet publish $sidecarProject --configuration Release --runtime $Runtime --self-contained true -p:PublishSingleFile=true "-p:Version=$version" --output $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $builtExe = Join-Path $repoRoot 'target/release/ThermoTray.exe'
    if (-not (Test-Path -LiteralPath $builtExe)) {
        throw "Rust executable not found at: $builtExe"
    }
    Copy-Item -LiteralPath $builtExe -Destination $publishDir -Force
}

$exePath = Join-Path $publishDir 'ThermoTray.exe'
foreach ($required in @($exePath, (Join-Path $publishDir 'ThermoTray.Sensors.exe'))) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Published executable not found at: $required"
    }
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

    & $resolvedMt -nologo "-inputresource:$exePath;#1" "-out:$manifestXmlPath"
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
