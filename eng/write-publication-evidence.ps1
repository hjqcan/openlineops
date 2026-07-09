param(
    [string] $ArtifactsRoot = "artifacts/release",

    [string] $OutputRoot = "output/publication-evidence",

    [string] $GitHubActionsRunUrl,

    [switch] $ConfirmMitLicense,

    [switch] $RequirePublishable,

    [switch] $SkipClean
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$InternalFailures = New-Object System.Collections.Generic.List[string]

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Assert-UnderRepoRoot {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $normalizedRoot = $RepoRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside the repository root: $fullPath"
    }
}

function New-CleanDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    Assert-UnderRepoRoot $Path
    if ((Test-Path -LiteralPath $Path) -and -not $SkipClean) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Content
    )

    $directory = [System.IO.Path]::GetDirectoryName($Path)
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        [System.Text.UTF8Encoding]::new($false))
}

function ConvertTo-CommandLine {
    param([Parameter(Mandatory = $true)][string[]] $Command)

    $parts = @($Command | ForEach-Object {
        if ($_ -match "\s") {
            '"' + $_.Replace('"', '\"') + '"'
        }
        else {
            $_
        }
    })

    return ($parts -join " ")
}

function Invoke-EvidenceCommand {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string[]] $Command,
        [switch] $Required,
        [switch] $PendingAllowed
    )

    $executable = $Command[0]
    $arguments = @()
    if ($Command.Count -gt 1) {
        $arguments = $Command[1..($Command.Count - 1)]
    }

    $output = & $executable @arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($null -eq $exitCode) {
        $exitCode = 0
    }

    $text = (($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()

    if ($Required -and $exitCode -ne 0) {
        $InternalFailures.Add("$Name failed with exit code $exitCode.") | Out-Null
    }

    return [pscustomobject]@{
        name = $Name
        command = ConvertTo-CommandLine $Command
        exitCode = $exitCode
        pendingAllowed = [bool] $PendingAllowed
        output = $text
    }
}

function Get-PendingExternalMessages {
    param([Parameter(Mandatory = $true)]$Gates)

    $messages = New-Object System.Collections.Generic.List[string]
    foreach ($gate in $Gates) {
        if ([string]::IsNullOrWhiteSpace($gate.output)) {
            continue
        }

        $pendingContinuation = $null
        foreach ($line in ($gate.output -split "\r?\n")) {
            if ($null -ne $pendingContinuation) {
                $trimmedContinuation = $line.Trim()
                if (-not [string]::IsNullOrWhiteSpace($trimmedContinuation) -and
                    $trimmedContinuation -notmatch "^(WARNING:|Publication readiness|Release manifest|Manifest:|Checksums:)") {
                    $messages.Add(($pendingContinuation + " " + $trimmedContinuation).Trim()) | Out-Null
                    $pendingContinuation = $null
                    continue
                }

                $messages.Add($pendingContinuation.Trim()) | Out-Null
                $pendingContinuation = $null
            }

            if ($line -match "PENDING EXTERNAL:\s*(.+)$") {
                $message = $Matches[1].Trim()
                if ($message -match "Status:\s*$") {
                    $pendingContinuation = $message
                }
                else {
                    $messages.Add($message) | Out-Null
                }
            }
        }

        if ($null -ne $pendingContinuation) {
            $messages.Add($pendingContinuation.Trim()) | Out-Null
        }
    }

    $normalized = @($messages | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $hasSignedStatus = @($normalized | Where-Object {
        $_ -match "Desktop release package is not signed with a valid Authenticode signature\. Status:\s*NotSigned\.?"
    }).Count -gt 0

    if ($hasSignedStatus) {
        $normalized = @($normalized | Where-Object {
            $_ -notmatch "Desktop release package is not signed with a valid Authenticode signature\. Status:\s*$"
        })
    }

    return @($normalized | Select-Object -Unique)
}

function Get-GateStatus {
    param([Parameter(Mandatory = $true)]$Gate)

    if ($Gate.exitCode -eq 0) {
        return "pass"
    }

    if ($Gate.pendingAllowed) {
        return "pending external"
    }

    return "fail"
}

function Test-StrictReadinessFailure {
    param([Parameter(Mandatory = $true)]$Gate)

    if ($Gate.exitCode -eq 0) {
        return
    }

    $failureLines = @($Gate.output -split "\r?\n" | Where-Object { $_ -match "^\s*-\s+" })
    $nonPending = @($failureLines | Where-Object { $_ -notmatch "PENDING EXTERNAL:" })
    if ($failureLines.Count -eq 0 -or $nonPending.Count -gt 0) {
        $InternalFailures.Add("Strict publication readiness failed for a non-external reason.") | Out-Null
    }
}

function Test-SignedInspectionFailure {
    param([Parameter(Mandatory = $true)]$Gate)

    if ($Gate.exitCode -eq 0) {
        return
    }

    if ($Gate.output -notmatch "Status:\s*NotSigned") {
        $InternalFailures.Add("Signed release candidate inspection failed for a reason other than the expected unsigned desktop package.") | Out-Null
    }
}

$resolvedOutputRoot = Resolve-RepoPath $OutputRoot
New-CleanDirectory $resolvedOutputRoot
$childWorkRoot = Join-Path $resolvedOutputRoot "work"
$releaseCandidateInspectionWorkRoot = Join-Path $childWorkRoot "release-candidate-inspection"
$releaseCandidateInspectionVerificationWorkRoot = Join-Path $childWorkRoot "release-candidate-inspection-verification"
$desktopSigningReadinessWorkRoot = Join-Path $childWorkRoot "desktop-signing-readiness"
$publicationMetadataFinalizationWorkRoot = Join-Path $childWorkRoot "publication-metadata-finalization"
$publicationReadinessAllowedWorkRoot = Join-Path $childWorkRoot "publication-readiness-allowed"
$publicationReadinessStrictWorkRoot = Join-Path $childWorkRoot "publication-readiness-strict"
$signedInspectionWorkRoot = Join-Path $childWorkRoot "signed-release-candidate-inspection"

$resolvedArtifactsRoot = Resolve-RepoPath $ArtifactsRoot
$manifestPath = Join-Path $resolvedArtifactsRoot "release-manifest.json"
$provenancePath = Join-Path $resolvedArtifactsRoot "release-provenance.json"

$gates = @()
$gates += Invoke-EvidenceCommand `
    -Name "open-source metadata" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/verify-open-source-metadata.ps1")) `
    -Required
$gates += Invoke-EvidenceCommand `
    -Name "third-party license metadata" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/verify-third-party-license-metadata.ps1")) `
    -Required
$gates += Invoke-EvidenceCommand `
    -Name "release candidate inspection" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/inspect-release-candidate.ps1"), "-ArtifactsRoot", $resolvedArtifactsRoot, "-WorkRoot", $releaseCandidateInspectionWorkRoot) `
    -Required
$gates += Invoke-EvidenceCommand `
    -Name "release candidate inspection behavior" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/verify-release-candidate-inspection.ps1"), "-WorkRoot", $releaseCandidateInspectionVerificationWorkRoot) `
    -Required
$gates += Invoke-EvidenceCommand `
    -Name "desktop signing readiness" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/verify-desktop-signing-readiness.ps1"), "-WorkRoot", $desktopSigningReadinessWorkRoot) `
    -Required
$gates += Invoke-EvidenceCommand `
    -Name "publication metadata finalization behavior" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/verify-publication-metadata-finalization.ps1"), "-WorkRoot", $publicationMetadataFinalizationWorkRoot) `
    -Required
$gates += Invoke-EvidenceCommand `
    -Name "publication readiness with pending external allowed" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/verify-publication-readiness.ps1"), "-ArtifactsRoot", $resolvedArtifactsRoot, "-WorkRoot", $publicationReadinessAllowedWorkRoot, "-AllowPendingExternal") `
    -Required
$strictReadiness = Invoke-EvidenceCommand `
    -Name "strict publication readiness" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/verify-publication-readiness.ps1"), "-ArtifactsRoot", $resolvedArtifactsRoot, "-WorkRoot", $publicationReadinessStrictWorkRoot) `
    -PendingAllowed
$gates += $strictReadiness
$signedInspection = Invoke-EvidenceCommand `
    -Name "signed release candidate inspection" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/inspect-release-candidate.ps1"), "-ArtifactsRoot", $resolvedArtifactsRoot, "-WorkRoot", $signedInspectionWorkRoot, "-RequireSignedDesktop") `
    -PendingAllowed
$gates += $signedInspection

Test-StrictReadinessFailure $strictReadiness
Test-SignedInspectionFailure $signedInspection

$pendingExternal = New-Object System.Collections.Generic.List[string]
foreach ($message in (Get-PendingExternalMessages $gates)) {
    $pendingExternal.Add($message) | Out-Null
}

if (-not $ConfirmMitLicense) {
    $pendingExternal.Add("Final MIT license decision has not been explicitly confirmed for publication.") | Out-Null
}

if ([string]::IsNullOrWhiteSpace($GitHubActionsRunUrl)) {
    $pendingExternal.Add("GitHub-hosted Windows CI proof URL has not been supplied.") | Out-Null
}
elseif ($GitHubActionsRunUrl -notmatch "^https://github\.com/[^/]+/[^/]+/actions/runs/\d+(/.*)?$") {
    $InternalFailures.Add("GitHubActionsRunUrl must point to a GitHub Actions run URL.") | Out-Null
}

$pendingExternal = @($pendingExternal | Select-Object -Unique)

$manifest = $null
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}
else {
    $InternalFailures.Add("Missing release manifest at $manifestPath.") | Out-Null
}

$provenance = $null
if (Test-Path -LiteralPath $provenancePath -PathType Leaf) {
    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
}
else {
    $InternalFailures.Add("Missing release provenance at $provenancePath.") | Out-Null
}

$artifactKinds = @()
$artifactCount = 0
if ($null -ne $manifest) {
    $artifactCount = @($manifest.artifacts).Count
    $artifactKinds = @($manifest.artifacts | ForEach-Object { $_.kind } | Select-Object -Unique | Sort-Object)
}

$publishable = ($InternalFailures.Count -eq 0 -and $pendingExternal.Count -eq 0 -and $strictReadiness.exitCode -eq 0 -and $signedInspection.exitCode -eq 0)

$gateSummaries = @($gates | ForEach-Object {
    [ordered]@{
        name = $_.name
        status = Get-GateStatus $_
        exitCode = $_.exitCode
        pendingAllowed = $_.pendingAllowed
        command = $_.command
        output = $_.output
    }
})

$evidence = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
    product = "OpenLineOps"
    publishable = $publishable
    repoRoot = $RepoRoot
    artifactsRoot = $resolvedArtifactsRoot
    outputRoot = $resolvedOutputRoot
    license = [ordered]@{
        fileLicense = "MIT"
        confirmedForPublication = [bool] $ConfirmMitLicense
    }
    githubActions = [ordered]@{
        runUrl = $GitHubActionsRunUrl
        proofSupplied = -not [string]::IsNullOrWhiteSpace($GitHubActionsRunUrl)
    }
    release = [ordered]@{
        product = if ($null -ne $manifest) { $manifest.product } else { $null }
        version = if ($null -ne $manifest) { $manifest.version } else { $null }
        manifestPath = $manifestPath
        provenancePath = $provenancePath
        provenanceGeneratedAtUtc = if ($null -ne $provenance) { $provenance.generatedAtUtc } else { $null }
        artifactCount = $artifactCount
        artifactKinds = $artifactKinds
    }
    pendingExternal = $pendingExternal
    internalFailures = @($InternalFailures)
    gates = $gateSummaries
}

$jsonPath = Join-Path $resolvedOutputRoot "publication-evidence.json"
$markdownPath = Join-Path $resolvedOutputRoot "publication-evidence.md"
Write-Utf8NoBom -Path $jsonPath -Content (($evidence | ConvertTo-Json -Depth 12) + [Environment]::NewLine)

$markdown = New-Object System.Collections.Generic.List[string]
$markdown.Add("# Publication Evidence") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("- Generated at UTC: $($evidence.generatedAtUtc)") | Out-Null
$markdown.Add("- Product: OpenLineOps") | Out-Null
$markdown.Add("- Publishable: $publishable") | Out-Null
$markdown.Add("- Release version: $($evidence.release.version)") | Out-Null
$markdown.Add("- Artifact kinds: $($artifactKinds -join ', ')") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("## Pending External Items") | Out-Null
if ($pendingExternal.Count -eq 0) {
    $markdown.Add("- None") | Out-Null
}
else {
    foreach ($item in $pendingExternal) {
        $markdown.Add("- $item") | Out-Null
    }
}

$markdown.Add("") | Out-Null
$markdown.Add("## Internal Failures") | Out-Null
if ($InternalFailures.Count -eq 0) {
    $markdown.Add("- None") | Out-Null
}
else {
    foreach ($failure in $InternalFailures) {
        $markdown.Add("- $failure") | Out-Null
    }
}

$markdown.Add("") | Out-Null
$markdown.Add("## Gate Results") | Out-Null
$markdown.Add("| Gate | Status | Exit code |") | Out-Null
$markdown.Add("| --- | --- | --- |") | Out-Null
foreach ($gate in $gateSummaries) {
    $markdown.Add("| $($gate.name) | $($gate.status) | $($gate.exitCode) |") | Out-Null
}

$markdown.Add("") | Out-Null
$markdown.Add("Command output is captured in publication-evidence.json.") | Out-Null
Write-Utf8NoBom -Path $markdownPath -Content (($markdown -join [Environment]::NewLine) + [Environment]::NewLine)

Write-Host "Publication evidence written."
Write-Host "Markdown: $markdownPath"
Write-Host "JSON: $jsonPath"

if ($InternalFailures.Count -gt 0) {
    Write-Host "Publication evidence failed:" -ForegroundColor Red
    foreach ($failure in $InternalFailures) {
        Write-Host " - $failure" -ForegroundColor Red
    }

    exit 1
}

if ($pendingExternal.Count -gt 0) {
    foreach ($item in $pendingExternal) {
        Write-Warning "PENDING EXTERNAL: $item"
    }
}

if ($RequirePublishable -and -not $publishable) {
    Write-Host "Publication is not yet publishable." -ForegroundColor Red
    exit 1
}

Write-Host "Publication evidence checks passed."
