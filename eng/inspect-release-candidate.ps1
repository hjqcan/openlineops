param(
    [string] $ArtifactsRoot = "artifacts/release",

    [string] $WorkRoot = "output/release-candidate-inspection",

    [switch] $RequireSignedDesktop,

    [switch] $SkipClean
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RequiredKinds = @("source", "api", "desktop", "plugin-host", "script-worker", "sample-plugin")
$Failures = New-Object System.Collections.Generic.List[string]

Add-Type -AssemblyName System.IO.Compression.FileSystem

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

function Add-Failure {
    param([Parameter(Mandatory = $true)][string] $Message)
    $Failures.Add($Message) | Out-Null
}

function Test-RequiredFile {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Failure "Missing required release file: $Path"
        return $false
    }

    return $true
}

function Test-TextContains {
    param(
        [Parameter(Mandatory = $true)][string] $Content,
        [Parameter(Mandatory = $true)][string] $Expected,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if (-not $Content.Contains($Expected)) {
        Add-Failure "$Description is missing expected text: $Expected"
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Open-ZipArchive {
    param([Parameter(Mandatory = $true)][string] $Path)

    try {
        return [System.IO.Compression.ZipFile]::OpenRead($Path)
    }
    catch {
        Add-Failure "Cannot open zip archive '$Path': $($_.Exception.Message)"
        return $null
    }
}

function Get-ZipEntries {
    param([Parameter(Mandatory = $true)]$Archive)

    return @($Archive.Entries | ForEach-Object { $_.FullName.Replace([char]92, [char]47) })
}

function Test-ZipEntrySafety {
    param(
        [Parameter(Mandatory = $true)]$Archive,
        [Parameter(Mandatory = $true)][string] $ArchiveName
    )

    $seenEntries = @{}
    $safeExtractionRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot "output/release-archive-safety-root"))
    $safeExtractionPrefix = $safeExtractionRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    foreach ($entry in @($Archive.Entries)) {
        $rawName = $entry.FullName
        $normalizedName = $rawName.Replace([char]92, [char]47)
        $trimmedName = $normalizedName.TrimEnd("/")

        if ([string]::IsNullOrWhiteSpace($trimmedName)) {
            Add-Failure "$ArchiveName contains an empty zip entry name."
            continue
        }

        if ($normalizedName.Contains([char]0)) {
            Add-Failure "$ArchiveName contains a zip entry with a NUL character: $normalizedName"
            continue
        }

        if ($normalizedName.StartsWith("/", [System.StringComparison]::Ordinal) `
            -or $normalizedName.StartsWith("//", [System.StringComparison]::Ordinal) `
            -or $normalizedName -match "^[A-Za-z]:/") {
            Add-Failure "$ArchiveName contains an absolute zip entry path: $normalizedName"
            continue
        }

        $segments = @($trimmedName.Split("/", [System.StringSplitOptions]::None))
        if ($segments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -eq "." -or $_ -eq ".." }) {
            Add-Failure "$ArchiveName contains an unsafe zip entry path segment: $normalizedName"
            continue
        }

        $comparisonKey = $trimmedName.ToLowerInvariant()
        if ($seenEntries.ContainsKey($comparisonKey)) {
            Add-Failure "$ArchiveName contains duplicate zip entry paths after normalization: $trimmedName"
            continue
        }

        $seenEntries[$comparisonKey] = $true

        $destinationPath = [System.IO.Path]::GetFullPath(
            (Join-Path $safeExtractionRoot $trimmedName.Replace("/", [System.IO.Path]::DirectorySeparatorChar)))
        if (-not $destinationPath.StartsWith($safeExtractionPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            Add-Failure "$ArchiveName contains a path traversal zip entry: $normalizedName"
        }
    }
}

function Test-ZipContains {
    param(
        [Parameter(Mandatory = $true)]$Archive,
        [Parameter(Mandatory = $true)][string] $ArchiveName,
        [Parameter(Mandatory = $true)][string[]] $ExpectedEntries
    )

    $entries = Get-ZipEntries -Archive $Archive
    foreach ($expectedEntry in $ExpectedEntries) {
        if ($entries -notcontains $expectedEntry) {
            Add-Failure "$ArchiveName is missing expected entry: $expectedEntry"
        }
    }
}

function Test-SensitiveSourceArchiveEntries {
    param(
        [Parameter(Mandatory = $true)]$Archive,
        [Parameter(Mandatory = $true)][string] $ArchiveName
    )

    foreach ($entryName in Get-ZipEntries -Archive $Archive) {
        if (Test-IsSensitiveSourceArchivePath $entryName) {
            Add-Failure "$ArchiveName contains a sensitive source archive entry: $entryName"
        }
    }
}

function Test-IsSensitiveSourceArchivePath {
    param([Parameter(Mandatory = $true)][string] $EntryName)

    $normalizedName = $EntryName.Replace([char]92, [char]47)
    $segments = @($normalizedName.Split("/", [System.StringSplitOptions]::RemoveEmptyEntries))
    if ($segments.Count -eq 0) {
        return $false
    }

    $lowerSegments = @($segments | ForEach-Object { $_.ToLowerInvariant() })
    $fileName = $segments[$segments.Count - 1]
    $lowerFileName = $fileName.ToLowerInvariant()

    if ($lowerFileName -like ".env*" -and $lowerFileName -ne ".env.example") {
        return $true
    }

    if ($lowerFileName -in @(
            ".npmrc",
            ".netrc",
            "credentials.json",
            "client_secret.json",
            "service-account.json",
            "token.json")) {
        return $true
    }

    if ($lowerFileName -match '\.(pfx|p12|key|snk|publishsettings)$') {
        return $true
    }

    if ($lowerFileName -match '^(id_rsa|id_dsa|id_ecdsa|id_ed25519)(\..*)?$') {
        return $true
    }

    if ($lowerFileName -match '\.pem$' -and $lowerFileName -match '(private|secret|signing|codesign|code-signing|certificate|cert|key)') {
        return $true
    }

    if ($lowerSegments -contains ".ssh" -or $lowerSegments -contains ".secrets") {
        return $true
    }

    return $false
}

function Get-ManifestArtifact {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string] $Kind
    )

    $matches = @($Manifest.artifacts | Where-Object { $_.kind -eq $Kind })
    if ($matches.Count -ne 1) {
        Add-Failure "Expected exactly one artifact of kind '$Kind', found $($matches.Count)."
        return $null
    }

    return $matches[0]
}

function Invoke-ReleaseManifestVerify {
    $arguments = @(
        "run",
        "--project",
        (Resolve-RepoPath "tools/OpenLineOps.ReleaseManifest/OpenLineOps.ReleaseManifest.csproj"),
        "--",
        "--verify",
        "--artifacts",
        $ResolvedArtifactsRoot,
        "--manifest",
        $ManifestPath,
        "--checksums",
        $ChecksumsPath
    )

    foreach ($kind in $RequiredKinds) {
        $arguments += @("--require-kind", $kind)
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        Add-Failure "Release manifest verification failed with exit code $LASTEXITCODE."
    }
}

function Test-ReleaseProvenance {
    param([Parameter(Mandatory = $true)]$Manifest)

    if (-not (Test-Path -LiteralPath $ReleaseProvenancePath -PathType Leaf)) {
        Add-Failure "Missing required release file: $ReleaseProvenancePath"
        return
    }

    $provenance = Get-Content -LiteralPath $ReleaseProvenancePath -Raw | ConvertFrom-Json
    if ($provenance.schemaVersion -ne 1) {
        Add-Failure "Expected release provenance schemaVersion 1, found $($provenance.schemaVersion)."
    }

    if ($provenance.product -ne $Manifest.product) {
        Add-Failure "Release provenance product '$($provenance.product)' does not match manifest product '$($Manifest.product)'."
    }

    if ($provenance.version -ne $Manifest.version) {
        Add-Failure "Release provenance version '$($provenance.version)' does not match manifest version '$($Manifest.version)'."
    }

    Test-ProvenanceHash `
        -ActualPath $ManifestPath `
        -RecordedHash $provenance.release.manifest.sha256 `
        -Description "Release provenance manifest hash"
    Test-ProvenanceHash `
        -ActualPath $ChecksumsPath `
        -RecordedHash $provenance.release.checksums.sha256 `
        -Description "Release provenance checksums hash"
    Test-ProvenanceHash `
        -ActualPath $ReleaseNotesPath `
        -RecordedHash $provenance.release.notes.sha256 `
        -Description "Release provenance release-notes hash"
    Test-ProvenanceHash `
        -ActualPath $ReleaseDependencyInventoryPath `
        -RecordedHash $provenance.release.dependencyInventory.sha256 `
        -Description "Release provenance dependency-inventory hash"

    foreach ($toolName in @("powershell", "dotnetSdk", "node", "npm")) {
        $toolValue = $provenance.tools.$toolName
        if ([string]::IsNullOrWhiteSpace($toolValue)) {
            Add-Failure "Release provenance is missing tool version: $toolName"
        }
    }

    $manifestArtifacts = @($Manifest.artifacts)
    $provenanceArtifacts = @($provenance.artifacts)
    if ($provenanceArtifacts.Count -ne $manifestArtifacts.Count) {
        Add-Failure "Release provenance artifact count $($provenanceArtifacts.Count) does not match manifest artifact count $($manifestArtifacts.Count)."
        return
    }

    foreach ($artifact in $manifestArtifacts) {
        $matches = @($provenanceArtifacts | Where-Object { $_.relativePath -eq $artifact.relativePath })
        if ($matches.Count -ne 1) {
            Add-Failure "Release provenance is missing manifest artifact: $($artifact.relativePath)"
            continue
        }

        $provenanceArtifact = $matches[0]
        foreach ($propertyName in @("kind", "fileName", "sizeBytes", "sha256")) {
            if ($provenanceArtifact.$propertyName -ne $artifact.$propertyName) {
                Add-Failure "Release provenance artifact '$($artifact.relativePath)' has mismatched $propertyName."
            }
        }
    }
}

function Test-DependencyInventory {
    param([Parameter(Mandatory = $true)]$Manifest)

    if (-not (Test-Path -LiteralPath $ReleaseDependencyInventoryPath -PathType Leaf)) {
        Add-Failure "Missing required release file: $ReleaseDependencyInventoryPath"
        return
    }

    $inventory = Get-Content -LiteralPath $ReleaseDependencyInventoryPath -Raw | ConvertFrom-Json
    if ($inventory.schemaVersion -ne 1) {
        Add-Failure "Expected dependency inventory schemaVersion 1, found $($inventory.schemaVersion)."
    }

    if ($inventory.product -ne $Manifest.product) {
        Add-Failure "Dependency inventory product '$($inventory.product)' does not match manifest product '$($Manifest.product)'."
    }

    if ($inventory.version -ne $Manifest.version) {
        Add-Failure "Dependency inventory version '$($inventory.version)' does not match manifest version '$($Manifest.version)'."
    }

    $packages = @($inventory.packages)
    if ($packages.Count -le 0) {
        Add-Failure "Dependency inventory must contain at least one package."
        return
    }

    if ($inventory.packageCounts.total -ne $packages.Count) {
        Add-Failure "Dependency inventory package count $($inventory.packageCounts.total) does not match package list count $($packages.Count)."
    }

    foreach ($ecosystem in @("nuget", "npm")) {
        if (-not ($packages | Where-Object { $_.ecosystem -eq $ecosystem })) {
            Add-Failure "Dependency inventory is missing $ecosystem packages."
        }
    }

    foreach ($package in $packages) {
        foreach ($propertyName in @("ecosystem", "name", "version", "license")) {
            if ([string]::IsNullOrWhiteSpace($package.$propertyName)) {
                Add-Failure "Dependency inventory contains a package with missing $propertyName."
                return
            }
        }

        if ($package.license -match "(?i)\b(AGPL|GPL|LGPL|SSPL)\b") {
            Add-Failure "Dependency inventory contains a license requiring release review: $($package.name) $($package.version) $($package.license)"
            return
        }
    }
}

function Test-MetadataChecksums {
    if (-not (Test-Path -LiteralPath $ReleaseMetadataChecksumsPath -PathType Leaf)) {
        Add-Failure "Missing required release file: $ReleaseMetadataChecksumsPath"
        return
    }

    $expectedMetadata = [ordered]@{
        "release-manifest.json" = $ManifestPath
        "checksums.sha256" = $ChecksumsPath
        "release-notes.md" = $ReleaseNotesPath
        "release-dependency-inventory.json" = $ReleaseDependencyInventoryPath
        "release-provenance.json" = $ReleaseProvenancePath
    }
    $actualMetadata = @{}

    foreach ($rawLine in Get-Content -LiteralPath $ReleaseMetadataChecksumsPath) {
        $line = $rawLine.Trim()
        if ($line.Length -eq 0) {
            continue
        }

        $separatorIndex = $line.IndexOfAny([char[]]@(" ", "`t"))
        if ($separatorIndex -le 0) {
            Add-Failure "Invalid metadata checksum line: $rawLine"
            continue
        }

        $hash = $line.Substring(0, $separatorIndex).Trim().ToLowerInvariant()
        $relativePath = $line.Substring($separatorIndex).Trim().Replace([char]92, [char]47)
        if ([string]::IsNullOrWhiteSpace($relativePath)) {
            Add-Failure "Invalid metadata checksum line: $rawLine"
            continue
        }

        if ($actualMetadata.ContainsKey($relativePath)) {
            Add-Failure "Duplicate metadata checksum entry: $relativePath"
            continue
        }

        $actualMetadata[$relativePath] = $hash
    }

    foreach ($entry in $expectedMetadata.GetEnumerator()) {
        if (-not $actualMetadata.ContainsKey($entry.Key)) {
            Add-Failure "Metadata checksums are missing $($entry.Key)."
            continue
        }

        $actualHash = $actualMetadata[$entry.Key]
        if ($actualHash.Length -ne 64 -or $actualHash -notmatch "^[0-9a-f]{64}$") {
            Add-Failure "Metadata checksum for $($entry.Key) is not a lowercase SHA-256 hash."
            continue
        }

        $expectedHash = Get-FileSha256 $entry.Value
        if ($actualHash -ne $expectedHash) {
            Add-Failure "Metadata checksum for $($entry.Key) does not match $($entry.Value)."
        }
    }

    $extraMetadata = @($actualMetadata.Keys | Where-Object { -not $expectedMetadata.Contains($_) } | Sort-Object)
    if ($extraMetadata.Count -gt 0) {
        Add-Failure "Metadata checksums contain unexpected file(s): $($extraMetadata -join ", ")"
    }
}

function Test-ProvenanceHash {
    param(
        [Parameter(Mandatory = $true)][string] $ActualPath,
        [AllowNull()][string] $RecordedHash,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if ([string]::IsNullOrWhiteSpace($RecordedHash)) {
        Add-Failure "$Description is missing."
        return
    }

    $actualHash = Get-FileSha256 $ActualPath
    if ($RecordedHash.ToLowerInvariant() -ne $actualHash) {
        Add-Failure "$Description does not match $ActualPath."
    }
}

function Test-DesktopSignature {
    param([Parameter(Mandatory = $true)]$DesktopArchive)

    $entry = $DesktopArchive.Entries |
        Where-Object { $_.FullName.Replace([char]92, [char]47) -eq "package/win-unpacked/OpenLineOps.exe" } |
        Select-Object -First 1

    if ($entry -eq $null) {
        Add-Failure "Cannot verify desktop signature because OpenLineOps.exe was not found in the desktop archive."
        return
    }

    $resolvedWorkRoot = Resolve-RepoPath $WorkRoot
    Assert-UnderRepoRoot $resolvedWorkRoot
    if ((Test-Path -LiteralPath $resolvedWorkRoot) -and -not $SkipClean) {
        Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $resolvedWorkRoot -Force | Out-Null
    $extractedExe = Join-Path $resolvedWorkRoot "OpenLineOps.exe"
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $extractedExe, $true)

    $signature = Get-AuthenticodeSignature -LiteralPath $extractedExe
    if ($signature.Status -ne "Valid") {
        Add-Failure "Desktop OpenLineOps.exe signature is not valid. Status: $($signature.Status)."
    }
}

$ResolvedArtifactsRoot = Resolve-RepoPath $ArtifactsRoot
if (-not (Test-Path -LiteralPath $ResolvedArtifactsRoot -PathType Container)) {
    throw "ArtifactsRoot does not exist: $ResolvedArtifactsRoot"
}

$ManifestPath = Join-Path $ResolvedArtifactsRoot "release-manifest.json"
$ChecksumsPath = Join-Path $ResolvedArtifactsRoot "checksums.sha256"
$ReleaseNotesPath = Join-Path $ResolvedArtifactsRoot "release-notes.md"
$ReleaseDependencyInventoryPath = Join-Path $ResolvedArtifactsRoot "release-dependency-inventory.json"
$ReleaseProvenancePath = Join-Path $ResolvedArtifactsRoot "release-provenance.json"
$ReleaseMetadataChecksumsPath = Join-Path $ResolvedArtifactsRoot "release-metadata-checksums.sha256"

$metadataFilesExist = $true
foreach ($metadataFile in @($ManifestPath, $ChecksumsPath, $ReleaseNotesPath, $ReleaseDependencyInventoryPath, $ReleaseProvenancePath, $ReleaseMetadataChecksumsPath)) {
    if (-not (Test-RequiredFile $metadataFile)) {
        $metadataFilesExist = $false
    }
}

if ($metadataFilesExist) {
    Invoke-ReleaseManifestVerify
}

if (Test-Path -LiteralPath $ManifestPath -PathType Leaf) {
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1) {
        Add-Failure "Expected release manifest schemaVersion 1, found $($manifest.schemaVersion)."
    }

    if ($manifest.product -ne "OpenLineOps") {
        Add-Failure "Expected release manifest product OpenLineOps, found '$($manifest.product)'."
    }

    Test-ReleaseProvenance -Manifest $manifest
    Test-DependencyInventory -Manifest $manifest
    Test-MetadataChecksums

    foreach ($kind in $RequiredKinds) {
        $artifact = Get-ManifestArtifact -Manifest $manifest -Kind $kind
        if ($artifact -eq $null) {
            continue
        }

        $artifactPath = Join-Path $ResolvedArtifactsRoot $artifact.relativePath
        if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
            Add-Failure "Manifest artifact file is missing: $($artifact.relativePath)"
            continue
        }

        if ($artifact.sizeBytes -le 0) {
            Add-Failure "Manifest artifact has invalid size: $($artifact.relativePath)"
        }
    }

    $artifactByKind = @{}
    foreach ($artifact in @($manifest.artifacts)) {
        $artifactByKind[$artifact.kind] = $artifact
    }

    $requiredZipEntries = @{
        "api" = @("OpenLineOps.Api.dll")
        "desktop" = @(
            "dist/index.html",
            "dist-electron/main/main.js",
            "dist-electron/preload/preload.js",
            "package/win-unpacked/OpenLineOps.exe",
            "package/win-unpacked/OPENLINEOPS-PACKAGE-NOTES.txt",
            "package/win-unpacked/resources/app/package.json"
        )
        "plugin-host" = @("OpenLineOps.PluginHost.dll")
        "script-worker" = @("OpenLineOps.ScriptWorker.dll")
        "sample-plugin" = @(
            "manifest.json",
            "OpenLineOps.SamplePlugins.LoopbackDevice.dll"
        )
        "source" = @(
            "README.md",
            "THIRD-PARTY-NOTICES.md",
            "Directory.Build.props",
            "docs/development-execution-plan.md",
            "eng/stage-release-artifacts.ps1",
            "eng/verify-ci-workflow-actions.ps1",
            "eng/inspect-ci-release-artifact.ps1",
            "eng/inspect-release-candidate.ps1",
            "eng/prepare-final-publication.ps1",
            "eng/verify-final-publication-preflight.ps1",
            "eng/write-publication-evidence.ps1",
            "eng/verify-publication-evidence.ps1",
            "eng/verify-release-candidate-inspection.ps1",
            "eng/verify-open-source-metadata.ps1",
            "eng/verify-third-party-license-metadata.ps1"
        )
    }

    foreach ($kind in $RequiredKinds) {
        if (-not $artifactByKind.ContainsKey($kind)) {
            continue
        }

        $artifactPath = Join-Path $ResolvedArtifactsRoot $artifactByKind[$kind].relativePath
        if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
            continue
        }

        $archive = Open-ZipArchive -Path $artifactPath
        if ($archive -eq $null) {
            continue
        }

        try {
            if ($archive.Entries.Count -eq 0) {
                Add-Failure "$($artifactByKind[$kind].fileName) is empty."
            }

            Test-ZipEntrySafety `
                -Archive $archive `
                -ArchiveName $artifactByKind[$kind].fileName

            Test-ZipContains `
                -Archive $archive `
                -ArchiveName $artifactByKind[$kind].fileName `
                -ExpectedEntries $requiredZipEntries[$kind]

            if ($kind -eq "source") {
                Test-SensitiveSourceArchiveEntries `
                    -Archive $archive `
                    -ArchiveName $artifactByKind[$kind].fileName
            }

            if ($kind -eq "desktop" -and $RequireSignedDesktop) {
                Test-DesktopSignature -DesktopArchive $archive
            }
        }
        finally {
            $archive.Dispose()
        }
    }

    if (Test-Path -LiteralPath $ReleaseNotesPath -PathType Leaf) {
        $releaseNotes = Get-Content -LiteralPath $ReleaseNotesPath -Raw
        Test-TextContains -Content $releaseNotes -Expected "# OpenLineOps $($manifest.version)" -Description "Release notes"
        Test-TextContains -Content $releaseNotes -Expected "## Artifacts" -Description "Release notes"
        Test-TextContains -Content $releaseNotes -Expected "## Migration Notes" -Description "Release notes"

        foreach ($artifact in @($manifest.artifacts)) {
            Test-TextContains -Content $releaseNotes -Expected $artifact.fileName -Description "Release notes"
        }
    }
}

if ($Failures.Count -gt 0) {
    Write-Host "Release candidate inspection failed:" -ForegroundColor Red
    foreach ($failure in $Failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }

    exit 1
}

Write-Host "Release candidate inspection passed."
Write-Host "Artifacts: $ResolvedArtifactsRoot"
if ($RequireSignedDesktop) {
    Write-Host "Desktop signature requirement: enforced"
}
