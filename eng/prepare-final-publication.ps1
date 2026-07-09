param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $RepositoryUrl,

    [Parameter(Mandatory = $true)]
    [string] $SecurityContact,

    [Parameter(Mandatory = $true)]
    [string] $ConductContact,

    [Parameter(Mandatory = $true)]
    [string] $GitHubActionsRunUrl,

    [switch] $ConfirmMitLicense,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $ArtifactsRoot = "artifacts/release",

    [string] $WorkRoot = "artifacts/release-work",

    [switch] $NoRestore,

    [switch] $SkipDesktopBuild,

    [string] $CodeSigningSignToolPath,

    [string] $CodeSigningCertificatePath,

    [string] $CodeSigningCertificatePassword,

    [string] $CodeSigningCertificateThumbprint,

    [switch] $CodeSigningAutoSelectCertificate,

    [switch] $CodeSigningStoreMachine,

    [string] $CodeSigningTimestampUrl = "http://timestamp.digicert.com",

    [switch] $PlanOnly
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Assert-RequiredScript {
    param([Parameter(Mandatory = $true)][string] $Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Required script is missing: $Path"
    }

    return $resolved
}

function Assert-GitHubRepositoryUrl {
    param([Parameter(Mandatory = $true)][string] $Value)

    if ($Value -notmatch "^https://github\.com/[^/]+/[^/]+/?$") {
        throw "RepositoryUrl must have the form https://github.com/<owner>/<repository>."
    }
}

function Assert-GitHubActionsRunUrl {
    param([Parameter(Mandatory = $true)][string] $Value)

    if ($Value -notmatch "^https://github\.com/[^/]+/[^/]+/actions/runs/\d+(/.*)?$") {
        throw "GitHubActionsRunUrl must point to a GitHub Actions run URL."
    }
}

function Assert-FinalContact {
    param(
        [Parameter(Mandatory = $true)][string] $Value,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name is required."
    }

    if ($Value -match "(?i)\b(todo|tbd|placeholder|maintainers?)\b") {
        throw "$Name must be a final private contact, not '$Value'."
    }
}

function Get-CodeSigningSelectorCount {
    $selectorCount = 0
    if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificatePath)) {
        $selectorCount++
    }

    if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint)) {
        $selectorCount++
    }

    if ($CodeSigningAutoSelectCertificate) {
        $selectorCount++
    }

    return $selectorCount
}

function Format-CommandLine {
    param([Parameter(Mandatory = $true)][string[]] $Command)

    $parts = @()
    $maskNext = $false
    foreach ($part in $Command) {
        $value = $part
        if ($maskNext) {
            $value = "<redacted>"
            $maskNext = $false
        }
        elseif ($part -eq "-CodeSigningCertificatePassword") {
            $maskNext = $true
        }

        if ($value -match "\s") {
            $value = '"' + $value.Replace('"', '\"') + '"'
        }

        $parts += $value
    }

    return ($parts -join " ")
}

function Invoke-FinalPublicationCommand {
    param([Parameter(Mandatory = $true)][string[]] $Command)

    Write-Host "> $(Format-CommandLine $Command)"
    $executable = $Command[0]
    $arguments = @()
    if ($Command.Count -gt 1) {
        $arguments = $Command[1..($Command.Count - 1)]
    }

    & $executable @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $(Format-CommandLine $Command)"
    }
}

if (-not $ConfirmMitLicense) {
    throw "ConfirmMitLicense is required for final publication."
}

Assert-GitHubRepositoryUrl $RepositoryUrl
Assert-GitHubActionsRunUrl $GitHubActionsRunUrl
Assert-FinalContact -Value $SecurityContact -Name "SecurityContact"
Assert-FinalContact -Value $ConductContact -Name "ConductContact"

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Version is required."
}

$selectorCount = Get-CodeSigningSelectorCount
if ($selectorCount -ne 1) {
    throw "Provide exactly one code-signing certificate selector: -CodeSigningCertificatePath, -CodeSigningCertificateThumbprint, or -CodeSigningAutoSelectCertificate."
}

$finalizeScript = Assert-RequiredScript "eng/finalize-publication-metadata.ps1"
$stageScript = Assert-RequiredScript "eng/stage-release-artifacts.ps1"
$inspectScript = Assert-RequiredScript "eng/inspect-release-candidate.ps1"
$readinessScript = Assert-RequiredScript "eng/verify-publication-readiness.ps1"
$evidenceScript = Assert-RequiredScript "eng/write-publication-evidence.ps1"

$resolvedArtifactsRoot = Resolve-RepoPath $ArtifactsRoot
$resolvedWorkRoot = Resolve-RepoPath $WorkRoot

$commands = @()
$commands += ,@(
    "powershell",
    "-NoProfile",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    $finalizeScript,
    "-RepositoryUrl",
    $RepositoryUrl,
    "-SecurityContact",
    $SecurityContact,
    "-ConductContact",
    $ConductContact,
    "-SkipReadinessGate"
)

$stageCommand = @(
    "powershell",
    "-NoProfile",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    $stageScript,
    "-Configuration",
    $Configuration,
    "-Version",
    $Version,
    "-ArtifactsRoot",
    $resolvedArtifactsRoot,
    "-WorkRoot",
    $resolvedWorkRoot,
    "-SignDesktopPackage",
    "-CodeSigningTimestampUrl",
    $CodeSigningTimestampUrl
)

if ($NoRestore) {
    $stageCommand += "-NoRestore"
}

if ($SkipDesktopBuild) {
    $stageCommand += "-SkipDesktopBuild"
}

if (-not [string]::IsNullOrWhiteSpace($CodeSigningSignToolPath)) {
    $stageCommand += @("-CodeSigningSignToolPath", $CodeSigningSignToolPath)
}

if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificatePath)) {
    $stageCommand += @("-CodeSigningCertificatePath", $CodeSigningCertificatePath)
}

if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificatePassword)) {
    $stageCommand += @("-CodeSigningCertificatePassword", $CodeSigningCertificatePassword)
}

if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint)) {
    $stageCommand += @("-CodeSigningCertificateThumbprint", $CodeSigningCertificateThumbprint)
}

if ($CodeSigningAutoSelectCertificate) {
    $stageCommand += "-CodeSigningAutoSelectCertificate"
}

if ($CodeSigningStoreMachine) {
    $stageCommand += "-CodeSigningStoreMachine"
}

$commands += ,$stageCommand
$commands += ,@(
    "powershell",
    "-NoProfile",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    $inspectScript,
    "-ArtifactsRoot",
    $resolvedArtifactsRoot,
    "-RequireSignedDesktop"
)
$commands += ,@(
    "powershell",
    "-NoProfile",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    $readinessScript,
    "-ArtifactsRoot",
    $resolvedArtifactsRoot
)
$commands += ,@(
    "powershell",
    "-NoProfile",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    $evidenceScript,
    "-ArtifactsRoot",
    $resolvedArtifactsRoot,
    "-ConfirmMitLicense",
    "-GitHubActionsRunUrl",
    $GitHubActionsRunUrl,
    "-RequirePublishable"
)

if ($PlanOnly) {
    Write-Host "Final publication plan:"
    foreach ($command in $commands) {
        Write-Host "> $(Format-CommandLine $command)"
    }

    Write-Host "Plan only; no publication changes were made."
    return
}

foreach ($command in $commands) {
    Invoke-FinalPublicationCommand $command
}

Write-Host "Final publication preparation passed."
