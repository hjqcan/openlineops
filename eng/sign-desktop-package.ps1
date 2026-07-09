param(
    [string] $PackageRoot = "apps/desktop/release/desktop/win-unpacked",

    [string] $SignToolPath,

    [string] $CertificatePath,

    [string] $CertificatePassword,

    [string] $CertificateThumbprint,

    [switch] $AutoSelectCertificate,

    [switch] $StoreMachine,

    [string] $TimestampUrl = "http://timestamp.digicert.com",

    [ValidateSet("SHA256", "SHA384", "SHA512")]
    [string] $FileDigestAlgorithm = "SHA256",

    [ValidateSet("SHA256", "SHA384", "SHA512")]
    [string] $TimestampDigestAlgorithm = "SHA256",

    [switch] $SkipVerify,

    [switch] $PlanOnly
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$SignableExtensions = @(".exe", ".dll", ".node")

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Resolve-SignTool {
    if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
        $resolved = Resolve-RepoPath $SignToolPath
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "SignToolPath does not exist: $resolved"
        }

        return $resolved
    }

    $command = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
    if ($command -ne $null) {
        return $command.Source
    }

    $windowsKitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits/10/bin"
    if (Test-Path -LiteralPath $windowsKitsRoot -PathType Container) {
        $candidate = Get-ChildItem `
            -LiteralPath $windowsKitsRoot `
            -Filter "signtool.exe" `
            -Recurse `
            -File `
            -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "[\\/]x64[\\/]signtool\.exe$" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1

        if ($candidate -ne $null) {
            return $candidate.FullName
        }
    }

    throw "signtool.exe was not found. Install the Windows SDK or pass -SignToolPath."
}

function Get-CertificateSelectorArguments {
    $selectorCount = 0
    $arguments = @()

    if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
        $selectorCount++
        $resolvedCertificatePath = Resolve-RepoPath $CertificatePath
        if (-not (Test-Path -LiteralPath $resolvedCertificatePath -PathType Leaf)) {
            throw "CertificatePath does not exist: $resolvedCertificatePath"
        }

        $arguments += @("/f", $resolvedCertificatePath)
        if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
            $arguments += @("/p", $CertificatePassword)
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        $selectorCount++
        $arguments += @("/sha1", $CertificateThumbprint)
        if ($StoreMachine) {
            $arguments += "/sm"
        }
    }

    if ($AutoSelectCertificate) {
        $selectorCount++
        $arguments += "/a"
        if ($StoreMachine) {
            $arguments += "/sm"
        }
    }

    if ($selectorCount -ne 1) {
        throw "Provide exactly one certificate selector: -CertificatePath, -CertificateThumbprint, or -AutoSelectCertificate."
    }

    return $arguments
}

function Get-RelativePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $normalizedRoot = $ResolvedPackageRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar

    if ($fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $fullPath.Substring($rootPrefix.Length).Replace([char]92, [char]47)
    }

    return $fullPath.Replace([char]92, [char]47)
}

function Invoke-SignTool {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    & $ResolvedSignTool @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "signtool.exe failed with exit code $LASTEXITCODE."
    }
}

$ResolvedPackageRoot = Resolve-RepoPath $PackageRoot
if (-not (Test-Path -LiteralPath $ResolvedPackageRoot -PathType Container)) {
    throw "PackageRoot does not exist: $ResolvedPackageRoot"
}

if (-not [string]::IsNullOrWhiteSpace($CertificatePath) -and
    [string]::IsNullOrWhiteSpace($CertificatePassword) -and
    -not [string]::IsNullOrWhiteSpace($env:OPENLINEOPS_CODESIGN_PASSWORD)) {
    $CertificatePassword = $env:OPENLINEOPS_CODESIGN_PASSWORD
}

$certificateArguments = Get-CertificateSelectorArguments
$signableFiles = @(
    Get-ChildItem -LiteralPath $ResolvedPackageRoot -Recurse -File |
        Where-Object { $SignableExtensions -contains $_.Extension.ToLowerInvariant() } |
        Sort-Object FullName
)

if ($signableFiles.Count -eq 0) {
    throw "No signable desktop package files were found under $ResolvedPackageRoot."
}

if ($PlanOnly) {
    Write-Host "Desktop signing plan only."
    Write-Host "PackageRoot: $ResolvedPackageRoot"
    Write-Host "TimestampUrl: $TimestampUrl"
    Write-Host "Files: $($signableFiles.Count)"
    foreach ($file in $signableFiles) {
        Write-Host " - $(Get-RelativePath $file.FullName)"
    }

    return
}

$ResolvedSignTool = Resolve-SignTool

foreach ($file in $signableFiles) {
    $signArguments = @(
        "sign",
        "/fd",
        $FileDigestAlgorithm,
        "/tr",
        $TimestampUrl,
        "/td",
        $TimestampDigestAlgorithm
    ) + $certificateArguments + @($file.FullName)

    Write-Host "Signing $(Get-RelativePath $file.FullName)"
    Invoke-SignTool -Arguments $signArguments
}

if (-not $SkipVerify) {
    foreach ($file in $signableFiles) {
        $verifyArguments = @("verify", "/pa", "/all", $file.FullName)
        Write-Host "Verifying signature $(Get-RelativePath $file.FullName)"
        Invoke-SignTool -Arguments $verifyArguments
    }
}

Write-Host "Desktop package signing completed."
Write-Host "Signed files: $($signableFiles.Count)"
