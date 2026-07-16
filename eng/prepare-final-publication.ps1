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
    [string] $ProductionIntegrationEvidencePath,

    [switch] $ConfirmMitLicense,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $ArtifactsRoot = "artifacts/release",

    [string] $WorkRoot = "artifacts/release-work",

    [switch] $NoRestore,

    [switch] $SkipDesktopBuild,

    [string] $CodeSigningSignToolPath,

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
        else {
            $absoluteUri = $null
            if ([System.Uri]::TryCreate($part, [System.UriKind]::Absolute, [ref]$absoluteUri) `
                -and (-not [string]::IsNullOrEmpty($absoluteUri.UserInfo) `
                    -or $absoluteUri.Query -match '(?i)(password|passphrase|token|secret|credential|api[-_]?key)=')) {
                $value = "<redacted-uri>"
            }
        }
        if ($value -ceq $part `
            -and $part -match '^(?<name>[^=:\s]+)(?<separator>=|:)(?<value>.*)$' `
            -and $Matches.name -match '(?i)(password|passphrase|token|secret|credential|api[-_]?key)') {
            $value = "$($Matches.name)$($Matches.separator)<redacted>"
        }
        elseif ($value -ceq $part `
            -and ($part -match '(?i)(password|passphrase|token|secret|credential|api[-_]?key)' `
            -or $part -ceq "/p")) {
            $maskNext = $true
        }

        if ($value -match "\s") {
            $value = '"' + $value.Replace('"', '\"') + '"'
        }

        $parts += $value
    }

    return ($parts -join " ")
}

function Invoke-GitQuery {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    Push-Location $RepoRoot
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = @(& git @Arguments 2>&1)
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
    }
    finally {
        Pop-Location
    }

    if ($exitCode -ne 0) {
        throw "Final publication requires an accessible Git worktree."
    }

    return (($output | ForEach-Object { $_.ToString() }) -join "`n").Trim()
}

function Get-PublicationSourceCommit {
    $insideWorkTree = Invoke-GitQuery -Arguments @("rev-parse", "--is-inside-work-tree")
    if ($insideWorkTree -cne "true") {
        throw "Final publication must run from a Git worktree."
    }

    $commit = (Invoke-GitQuery -Arguments @("rev-parse", "HEAD")).ToLowerInvariant()
    if ($commit -cnotmatch '^[0-9a-f]{40,64}$') {
        throw "Final publication could not resolve a full Git HEAD object id."
    }

    return $commit
}

function Assert-CleanGitWorkTree {
    $status = Invoke-GitQuery -Arguments @(
        "status",
        "--porcelain=v1",
        "--untracked-files=all")
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        throw "Final publication requires a clean Git worktree. Commit or discard every tracked and untracked change first."
    }
}

function Test-ExactJsonProperties {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)][string] $Description,
        [Parameter(Mandatory = $true)][string[]] $ExpectedProperties
    )

    if ($null -eq $Value -or $Value -isnot [pscustomobject]) {
        throw "$Description must be a JSON object."
    }

    $actual = @($Value.PSObject.Properties.Name | Sort-Object)
    $expected = @($ExpectedProperties | Sort-Object)
    if (@(Compare-Object -ReferenceObject $expected -DifferenceObject $actual -CaseSensitive).Count -ne 0) {
        throw "$Description has missing, unexpected, or non-canonical properties."
    }
}

function Assert-PathDoesNotTraverseReparsePoint {
    param([Parameter(Mandatory = $true)][string] $Path)

    $relativePath = $Path.Substring(
        $RepoRoot.TrimEnd('\', '/').Length).TrimStart('\', '/')
    $cursor = $RepoRoot
    foreach ($segment in $relativePath.Split(
            @([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar),
            [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $segment
        if (-not (Test-Path -LiteralPath $cursor)) {
            break
        }

        if ((Get-Item -LiteralPath $cursor -Force).Attributes.HasFlag(
                [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Production integration evidence paths cannot traverse a reparse point: $Path"
        }
    }
}

function Resolve-ProductionIntegrationEvidence {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $ExpectedCommit,
        [Parameter(Mandatory = $true)][string] $ExpectedRepository
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "ProductionIntegrationEvidencePath is required."
    }

    $resolvedPath = Resolve-RepoPath $Path
    $normalizedRoot = $RepoRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "ProductionIntegrationEvidencePath must remain inside the repository root."
    }
    Assert-PathDoesNotTraverseReparsePoint $resolvedPath
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Production integration evidence does not exist: $resolvedPath"
    }

    $evidenceFile = Get-Item -LiteralPath $resolvedPath
    if ($evidenceFile.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
        throw "Production integration evidence cannot be a reparse point."
    }

    try {
        $evidence = Get-Content -LiteralPath $resolvedPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Production integration evidence is not valid JSON: $($_.Exception.Message)"
    }

    Test-ExactJsonProperties `
        -Value $evidence `
        -Description "Production integration evidence" `
        -ExpectedProperties @(
            "schemaVersion", "generatedAtUtc", "product", "repository", "commitSha",
            "runId", "runUrl", "jobName", "testName", "conclusion", "counters", "trx")
    Test-ExactJsonProperties `
        -Value $evidence.counters `
        -Description "Production integration evidence counters" `
        -ExpectedProperties @("total", "executed", "passed", "failed", "skipped")
    Test-ExactJsonProperties `
        -Value $evidence.trx `
        -Description "Production integration evidence TRX" `
        -ExpectedProperties @("relativePath", "sizeBytes", "sha256")

    $requiredTest = "OpenLineOps.PostgresIntegration.Tests.PostgresRabbitMqProductionCoordinationIntegrationTests.DurableOutboxAndResultInboxSurviveCoordinatorRestartAcrossRealBroker"
    $expectedRunUrl = "https://github.com/$($evidence.repository)/actions/runs/$($evidence.runId)"
    if ($evidence.schemaVersion -ne 1 `
        -or $evidence.product -cne "OpenLineOps" `
        -or $evidence.repository -cne $ExpectedRepository `
        -or $evidence.commitSha -cne $ExpectedCommit `
        -or $evidence.runId -cnotmatch '^[1-9][0-9]*$' `
        -or $evidence.runUrl -cne $expectedRunUrl `
        -or $evidence.jobName -cne "production-integration" `
        -or $evidence.testName -cne $requiredTest `
        -or $evidence.conclusion -cne "success" `
        -or $evidence.counters.total -le 0 `
        -or $evidence.counters.executed -ne $evidence.counters.total `
        -or $evidence.counters.passed -ne $evidence.counters.total `
        -or $evidence.counters.failed -ne 0 `
        -or $evidence.counters.skipped -ne 0 `
        -or $evidence.trx.relativePath -cnotmatch '^(?!/)(?!.*\\)(?!.*(?:^|/)\.\.(?:/|$)).+/production-integration\.trx$' `
        -or $evidence.trx.sizeBytes -le 0 `
        -or $evidence.trx.sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "Production integration evidence does not satisfy the strict successful same-run contract."
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY) `
        -or -not [string]::IsNullOrWhiteSpace($env:GITHUB_SHA) `
        -or -not [string]::IsNullOrWhiteSpace($env:GITHUB_RUN_ID) `
        -or -not [string]::IsNullOrWhiteSpace($env:GITHUB_SERVER_URL)) {
        if ($env:GITHUB_REPOSITORY -cne $evidence.repository `
            -or $env:GITHUB_SHA -cne $evidence.commitSha `
            -or $env:GITHUB_RUN_ID -cne $evidence.runId `
            -or $env:GITHUB_SERVER_URL -cne "https://github.com") {
            throw "Production integration evidence does not match the current GitHub Actions repository, commit, and run."
        }
    }

    $resolvedTrxPath = Resolve-RepoPath $evidence.trx.relativePath
    if (-not $resolvedTrxPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Production integration TRX must remain inside the repository."
    }
    Assert-PathDoesNotTraverseReparsePoint $resolvedTrxPath
    if ([System.IO.Path]::GetDirectoryName($resolvedTrxPath) -cne $evidenceFile.DirectoryName `
        -or -not (Test-Path -LiteralPath $resolvedTrxPath -PathType Leaf)) {
        throw "Production integration TRX must be an existing sibling file inside the repository."
    }

    $trxFile = Get-Item -LiteralPath $resolvedTrxPath
    if ($trxFile.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
        throw "Production integration TRX cannot be a reparse point."
    }
    $trxHash = (Get-FileHash -LiteralPath $resolvedTrxPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($trxFile.Length -ne [long] $evidence.trx.sizeBytes `
        -or $trxHash -cne $evidence.trx.sha256) {
        throw "Production integration TRX size or SHA-256 does not match its evidence."
    }

    return $resolvedPath
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
Assert-FinalContact -Value $SecurityContact -Name "SecurityContact"
Assert-FinalContact -Value $ConductContact -Name "ConductContact"

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Version is required."
}

$timestampUri = $null
if (-not [System.Uri]::TryCreate(
        $CodeSigningTimestampUrl,
        [System.UriKind]::Absolute,
        [ref]$timestampUri) `
    -or $timestampUri.Scheme -notin @("http", "https") `
    -or -not [string]::IsNullOrEmpty($timestampUri.UserInfo) `
    -or $timestampUri.Query -match '(?i)(password|passphrase|token|secret|credential|api[-_]?key)=') {
    throw "CodeSigningTimestampUrl must be an absolute HTTP(S) URL without embedded credentials or secret-bearing query parameters."
}

$selectorCount = Get-CodeSigningSelectorCount
if ($selectorCount -ne 1) {
    throw "Provide exactly one certificate-store selector: -CodeSigningCertificateThumbprint or -CodeSigningAutoSelectCertificate."
}

$publicationSourceCommit = Get-PublicationSourceCommit
$repositoryIdentity = $RepositoryUrl.TrimEnd('/').Substring("https://github.com/".Length)
$resolvedProductionIntegrationEvidencePath = Resolve-ProductionIntegrationEvidence `
    -Path $ProductionIntegrationEvidencePath `
    -ExpectedCommit $publicationSourceCommit `
    -ExpectedRepository $repositoryIdentity
if (-not $PlanOnly) {
    Assert-CleanGitWorkTree
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
    "-SignWindowsPackages",
    "-RequireCleanGitWorkTree",
    "-ExpectedGitCommit",
    $publicationSourceCommit,
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
    "-RequireSignedWindowsArtifacts"
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
    "-ProductionIntegrationEvidencePath",
    $resolvedProductionIntegrationEvidencePath,
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
