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

function Test-OrdinalStringEquals {
    param(
        [AllowNull()][string] $Actual,
        [AllowNull()][string] $Expected
    )

    return [string]::Equals($Actual, $Expected, [System.StringComparison]::Ordinal)
}

function Test-ExactJsonObjectShape {
    param(
        [AllowNull()]$Value,
        [Parameter(Mandatory = $true)][string] $Description,
        [Parameter(Mandatory = $true)][string[]] $ExpectedProperties
    )

    if ($null -eq $Value -or $Value.GetType().FullName -ne "System.Management.Automation.PSCustomObject") {
        Add-Failure "$Description must be a JSON object."
        return $false
    }

    $actualProperties = @($Value.PSObject.Properties | ForEach-Object { $_.Name })
    $missing = @($ExpectedProperties | Where-Object {
        $expectedName = $_
        -not ($actualProperties | Where-Object {
            [string]::Equals($_, $expectedName, [System.StringComparison]::Ordinal)
        })
    })
    $unexpected = @($actualProperties | Where-Object {
        $actualName = $_
        -not ($ExpectedProperties | Where-Object {
            [string]::Equals($_, $actualName, [System.StringComparison]::Ordinal)
        })
    })

    if ($missing.Count -gt 0) {
        Add-Failure "$Description is missing exact property name(s): $($missing -join ", ")."
    }

    if ($unexpected.Count -gt 0) {
        Add-Failure "$Description contains unexpected or non-canonical property name(s): $($unexpected -join ", ")."
    }

    return $missing.Count -eq 0 -and $unexpected.Count -eq 0
}

function Get-ExactJsonPropertyValue {
    param(
        [AllowNull()]$Value,
        [Parameter(Mandatory = $true)][string] $PropertyName
    )

    if ($null -eq $Value -or $Value.GetType().FullName -ne "System.Management.Automation.PSCustomObject") {
        return $null
    }

    $property = @($Value.PSObject.Properties | Where-Object {
        [string]::Equals($_.Name, $PropertyName, [System.StringComparison]::Ordinal)
    })
    if ($property.Count -ne 1) {
        return $null
    }

    return $property[0].Value
}

function Test-ExactStringArray {
    param(
        [AllowNull()]$Value,
        [Parameter(Mandatory = $true)][string[]] $Expected,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if ($null -eq $Value -or $Value -is [string] -or $Value -isnot [System.Collections.IList]) {
        Add-Failure "$Description must be a JSON array."
        return
    }

    $actual = @($Value)
    if ($actual.Count -ne $Expected.Count) {
        Add-Failure "$Description must contain exactly: $($Expected -join ", ")."
        return
    }

    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($actual[$index] -isnot [string] `
            -or -not (Test-OrdinalStringEquals -Actual $actual[$index] -Expected $Expected[$index])) {
            Add-Failure "$Description must contain exactly: $($Expected -join ", ")."
            return
        }
    }
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

    return @($Archive.Entries | ForEach-Object { $_.FullName })
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
        if ($rawName.Contains([char]92)) {
            Add-Failure "$ArchiveName contains a non-canonical backslash zip entry path: $rawName"
            continue
        }

        $trimmedName = $rawName.TrimEnd("/")

        if ([string]::IsNullOrWhiteSpace($trimmedName)) {
            Add-Failure "$ArchiveName contains an empty zip entry name."
            continue
        }

        if ($rawName.Contains([char]0)) {
            Add-Failure "$ArchiveName contains a zip entry with a NUL character: $rawName"
            continue
        }

        if ($rawName.StartsWith("/", [System.StringComparison]::Ordinal) `
            -or $rawName.StartsWith("//", [System.StringComparison]::Ordinal) `
            -or $rawName -cmatch "^[A-Za-z]:/") {
            Add-Failure "$ArchiveName contains an absolute zip entry path: $rawName"
            continue
        }

        $segments = @($trimmedName.Split("/", [System.StringSplitOptions]::None))
        if ($segments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -ceq "." -or $_ -ceq ".." }) {
            Add-Failure "$ArchiveName contains an unsafe zip entry path segment: $rawName"
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
            Add-Failure "$ArchiveName contains a path traversal zip entry: $rawName"
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
        $hasExactEntry = @($entries | Where-Object {
            [string]::Equals($_, $expectedEntry, [System.StringComparison]::Ordinal)
        }).Count -gt 0
        if (-not $hasExactEntry) {
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

    $matches = @($Manifest.artifacts | Where-Object { $_.kind -ceq $Kind })
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

function Invoke-JsonPropertyVerification {
    param([Parameter(Mandatory = $true)][string[]] $Paths)

    $arguments = @(
        "run",
        "--project",
        (Resolve-RepoPath "tools/OpenLineOps.ReleaseManifest/OpenLineOps.ReleaseManifest.csproj"),
        "--"
    )
    foreach ($path in $Paths) {
        $arguments += @("--verify-json", $path)
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $verificationOutput = & dotnet @arguments 2>&1
        $verificationExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($verificationExitCode -ne 0) {
        $details = (($verificationOutput | ForEach-Object { $_.ToString() }) -join " ").Trim()
        Add-Failure "Release JSON property verification failed with exit code $verificationExitCode. $details"
    }
}

function Test-ProvenanceSource {
    param([AllowNull()]$Source)

    Test-ExactJsonObjectShape `
        -Value $Source `
        -Description "Release provenance source" `
        -ExpectedProperties @("available", "commit", "branch", "dirty") | Out-Null

    $available = Get-ExactJsonPropertyValue -Value $Source -PropertyName "available"
    if ($available -isnot [bool]) {
        Add-Failure "Release provenance source.available must be a JSON boolean."
    }

    $dirty = Get-ExactJsonPropertyValue -Value $Source -PropertyName "dirty"
    if ($null -ne $dirty -and $dirty -isnot [bool]) {
        Add-Failure "Release provenance source.dirty must be null or a JSON boolean."
    }

    $commit = Get-ExactJsonPropertyValue -Value $Source -PropertyName "commit"
    if ($null -ne $commit `
        -and ($commit -isnot [string] -or $commit -cnotmatch "^(?:[0-9a-f]{40}|[0-9a-f]{64})$")) {
        Add-Failure "Release provenance source.commit must be null or a lowercase Git object id."
    }

    $branch = Get-ExactJsonPropertyValue -Value $Source -PropertyName "branch"
    if ($null -ne $branch -and ($branch -isnot [string] -or [string]::IsNullOrWhiteSpace($branch))) {
        Add-Failure "Release provenance source.branch must be null or a non-empty string."
    }
}

function Test-ProvenanceBuild {
    param([AllowNull()]$Build)

    Test-ExactJsonObjectShape `
        -Value $Build `
        -Description "Release provenance build" `
        -ExpectedProperties @(
            "configuration",
            "noRestore",
            "skipDesktopBuild",
            "signDesktopPackage",
            "requiredArtifactKinds") | Out-Null

    $configuration = Get-ExactJsonPropertyValue -Value $Build -PropertyName "configuration"
    if ($configuration -isnot [string] `
        -or -not ((Test-OrdinalStringEquals $configuration "Debug") `
            -or (Test-OrdinalStringEquals $configuration "Release"))) {
        Add-Failure "Release provenance build.configuration must be exactly 'Debug' or 'Release'."
    }

    foreach ($propertyName in @("noRestore", "skipDesktopBuild", "signDesktopPackage")) {
        $propertyValue = Get-ExactJsonPropertyValue -Value $Build -PropertyName $propertyName
        if ($propertyValue -isnot [bool]) {
            Add-Failure "Release provenance build.$propertyName must be a JSON boolean."
        }
    }

    Test-ExactStringArray `
        -Value (Get-ExactJsonPropertyValue -Value $Build -PropertyName "requiredArtifactKinds") `
        -Expected $RequiredKinds `
        -Description "Release provenance build.requiredArtifactKinds"
}

function Test-ProvenanceTools {
    param([AllowNull()]$Tools)

    $toolNames = @("powershell", "dotnetSdk", "node", "npm")
    Test-ExactJsonObjectShape `
        -Value $Tools `
        -Description "Release provenance tools" `
        -ExpectedProperties $toolNames | Out-Null

    foreach ($toolName in $toolNames) {
        $toolValue = Get-ExactJsonPropertyValue -Value $Tools -PropertyName $toolName
        if ($toolValue -isnot [string] -or [string]::IsNullOrWhiteSpace($toolValue)) {
            Add-Failure "Release provenance is missing exact tool version: $toolName"
        }
    }
}

function Test-ProvenanceReleaseFile {
    param(
        [AllowNull()]$Entry,
        [Parameter(Mandatory = $true)][string] $Description,
        [Parameter(Mandatory = $true)][string] $ExpectedPath,
        [Parameter(Mandatory = $true)][string] $ActualPath
    )

    Test-ExactJsonObjectShape `
        -Value $Entry `
        -Description $Description `
        -ExpectedProperties @("path", "sha256") | Out-Null

    $recordedPath = Get-ExactJsonPropertyValue -Value $Entry -PropertyName "path"
    if ($recordedPath -isnot [string] `
        -or -not (Test-OrdinalStringEquals -Actual $recordedPath -Expected $ExpectedPath)) {
        Add-Failure "$Description path must be exactly '$ExpectedPath'."
    }

    Test-ProvenanceHash `
        -ActualPath $ActualPath `
        -RecordedHash (Get-ExactJsonPropertyValue -Value $Entry -PropertyName "sha256") `
        -Description "$Description hash"
}

function Test-ReleaseProvenance {
    param([Parameter(Mandatory = $true)]$Manifest)

    if (-not (Test-Path -LiteralPath $ReleaseProvenancePath -PathType Leaf)) {
        Add-Failure "Missing required release file: $ReleaseProvenancePath"
        return
    }

    try {
        $provenance = Get-Content -LiteralPath $ReleaseProvenancePath -Raw | ConvertFrom-Json
    }
    catch {
        Add-Failure "Release provenance is not valid JSON: $($_.Exception.Message)"
        return
    }

    Test-ExactJsonObjectShape `
        -Value $provenance `
        -Description "Release provenance" `
        -ExpectedProperties @(
            "schemaVersion",
            "product",
            "version",
            "generatedAtUtc",
            "source",
            "build",
            "tools",
            "release",
            "artifacts") | Out-Null

    $schemaVersion = Get-ExactJsonPropertyValue -Value $provenance -PropertyName "schemaVersion"
    if ($schemaVersion -isnot [int] -or $schemaVersion -ne 1) {
        Add-Failure "Release provenance schemaVersion must be the JSON integer 1."
    }

    $product = Get-ExactJsonPropertyValue -Value $provenance -PropertyName "product"
    if ($product -isnot [string] `
        -or -not (Test-OrdinalStringEquals -Actual $product -Expected "OpenLineOps")) {
        Add-Failure "Release provenance product must be exactly 'OpenLineOps'."
    }
    if (-not (Test-OrdinalStringEquals -Actual $product -Expected $Manifest.product)) {
        Add-Failure "Release provenance product '$product' does not exactly match manifest product '$($Manifest.product)'."
    }

    $version = Get-ExactJsonPropertyValue -Value $provenance -PropertyName "version"
    if ($version -isnot [string] -or [string]::IsNullOrWhiteSpace($version)) {
        Add-Failure "Release provenance version must be a non-empty string."
    }
    elseif (-not (Test-OrdinalStringEquals -Actual $version -Expected $Manifest.version)) {
        Add-Failure "Release provenance version '$version' does not exactly match manifest version '$($Manifest.version)'."
    }

    $generatedAtUtc = Get-ExactJsonPropertyValue -Value $provenance -PropertyName "generatedAtUtc"
    $parsedGeneratedAtUtc = [System.DateTimeOffset]::MinValue
    $isCanonicalGeneratedAtUtc = $generatedAtUtc -is [string] `
        -and [System.DateTimeOffset]::TryParseExact(
            $generatedAtUtc,
            "O",
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind,
            [ref] $parsedGeneratedAtUtc) `
        -and $parsedGeneratedAtUtc.Offset -eq [System.TimeSpan]::Zero `
        -and (Test-OrdinalStringEquals `
            -Actual $generatedAtUtc `
            -Expected $parsedGeneratedAtUtc.ToUniversalTime().ToString(
                "O",
                [System.Globalization.CultureInfo]::InvariantCulture))
    if (-not $isCanonicalGeneratedAtUtc) {
        Add-Failure "Release provenance generatedAtUtc must be a canonical UTC round-trip timestamp."
    }

    Test-ProvenanceSource `
        -Source (Get-ExactJsonPropertyValue -Value $provenance -PropertyName "source")
    Test-ProvenanceBuild `
        -Build (Get-ExactJsonPropertyValue -Value $provenance -PropertyName "build")
    Test-ProvenanceTools `
        -Tools (Get-ExactJsonPropertyValue -Value $provenance -PropertyName "tools")

    $release = Get-ExactJsonPropertyValue -Value $provenance -PropertyName "release"
    Test-ExactJsonObjectShape `
        -Value $release `
        -Description "Release provenance release" `
        -ExpectedProperties @("manifest", "checksums", "notes", "dependencyInventory") | Out-Null
    Test-ProvenanceReleaseFile `
        -Entry (Get-ExactJsonPropertyValue -Value $release -PropertyName "manifest") `
        -Description "Release provenance manifest" `
        -ExpectedPath "release-manifest.json" `
        -ActualPath $ManifestPath
    Test-ProvenanceReleaseFile `
        -Entry (Get-ExactJsonPropertyValue -Value $release -PropertyName "checksums") `
        -Description "Release provenance checksums" `
        -ExpectedPath "checksums.sha256" `
        -ActualPath $ChecksumsPath
    Test-ProvenanceReleaseFile `
        -Entry (Get-ExactJsonPropertyValue -Value $release -PropertyName "notes") `
        -Description "Release provenance release notes" `
        -ExpectedPath "release-notes.md" `
        -ActualPath $ReleaseNotesPath
    Test-ProvenanceReleaseFile `
        -Entry (Get-ExactJsonPropertyValue -Value $release -PropertyName "dependencyInventory") `
        -Description "Release provenance dependency inventory" `
        -ExpectedPath "release-dependency-inventory.json" `
        -ActualPath $ReleaseDependencyInventoryPath

    $manifestArtifacts = @($Manifest.artifacts)
    $artifactsValue = Get-ExactJsonPropertyValue -Value $provenance -PropertyName "artifacts"
    if ($null -eq $artifactsValue -or $artifactsValue -is [string] -or $artifactsValue -isnot [System.Collections.IList]) {
        Add-Failure "Release provenance artifacts must be a JSON array."
        return
    }

    $provenanceArtifacts = @($artifactsValue)
    foreach ($provenanceArtifact in $provenanceArtifacts) {
        Test-ExactJsonObjectShape `
            -Value $provenanceArtifact `
            -Description "Release provenance artifact" `
            -ExpectedProperties @("kind", "relativePath", "fileName", "sizeBytes", "sha256") | Out-Null
    }

    if ($provenanceArtifacts.Count -ne $manifestArtifacts.Count) {
        Add-Failure "Release provenance artifact count $($provenanceArtifacts.Count) does not match manifest artifact count $($manifestArtifacts.Count)."
        return
    }

    foreach ($artifact in $manifestArtifacts) {
        $matches = @($provenanceArtifacts | Where-Object {
            $relativePath = Get-ExactJsonPropertyValue -Value $_ -PropertyName "relativePath"
            $relativePath -is [string] `
                -and (Test-OrdinalStringEquals -Actual $relativePath -Expected $artifact.relativePath)
        })
        if ($matches.Count -ne 1) {
            Add-Failure "Release provenance is missing exact manifest artifact path: $($artifact.relativePath)"
            continue
        }

        $provenanceArtifact = $matches[0]
        foreach ($propertyName in @("kind", "fileName", "sha256")) {
            $actualValue = Get-ExactJsonPropertyValue -Value $provenanceArtifact -PropertyName $propertyName
            if ($actualValue -isnot [string] `
                -or -not (Test-OrdinalStringEquals -Actual $actualValue -Expected $artifact.$propertyName)) {
                Add-Failure "Release provenance artifact '$($artifact.relativePath)' has mismatched $propertyName."
            }
        }

        $sizeBytes = Get-ExactJsonPropertyValue -Value $provenanceArtifact -PropertyName "sizeBytes"
        if (($sizeBytes -isnot [int] -and $sizeBytes -isnot [long]) `
            -or [long] $sizeBytes -ne [long] $artifact.sizeBytes) {
            Add-Failure "Release provenance artifact '$($artifact.relativePath)' has mismatched sizeBytes."
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

    if ($inventory.product -cne $Manifest.product) {
        Add-Failure "Dependency inventory product '$($inventory.product)' does not match manifest product '$($Manifest.product)'."
    }

    if ($inventory.version -cne $Manifest.version) {
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
        if (-not ($packages | Where-Object { $_.ecosystem -ceq $ecosystem })) {
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

    $expectedMetadata = [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::Ordinal)
    $expectedMetadata.Add("release-manifest.json", $ManifestPath)
    $expectedMetadata.Add("checksums.sha256", $ChecksumsPath)
    $expectedMetadata.Add("release-notes.md", $ReleaseNotesPath)
    $expectedMetadata.Add("release-dependency-inventory.json", $ReleaseDependencyInventoryPath)
    $expectedMetadata.Add("release-provenance.json", $ReleaseProvenancePath)
    $actualMetadata = [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::Ordinal)

    foreach ($rawLine in Get-Content -LiteralPath $ReleaseMetadataChecksumsPath) {
        if ($rawLine.Length -le 66 -or $rawLine.Substring(64, 2) -cne "  ") {
            Add-Failure "Invalid metadata checksum line: $rawLine"
            continue
        }

        $hash = $rawLine.Substring(0, 64)
        $relativePath = $rawLine.Substring(66)
        if ($hash -cnotmatch "^[0-9a-f]{64}$" `
            -or [string]::IsNullOrWhiteSpace($relativePath) `
            -or $relativePath -cne $relativePath.Trim() `
            -or $relativePath.Contains([char]92)) {
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
        if ($actualHash.Length -ne 64 -or $actualHash -cnotmatch "^[0-9a-f]{64}$") {
            Add-Failure "Metadata checksum for $($entry.Key) is not a lowercase SHA-256 hash."
            continue
        }

        $expectedHash = Get-FileSha256 $entry.Value
        if ($actualHash -cne $expectedHash) {
            Add-Failure "Metadata checksum for $($entry.Key) does not match $($entry.Value)."
        }
    }

    $extraMetadata = @($actualMetadata.Keys | Where-Object { -not $expectedMetadata.ContainsKey($_) } | Sort-Object)
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

    if ($RecordedHash -cnotmatch "^[0-9a-f]{64}$") {
        Add-Failure "$Description must be a lowercase SHA-256 hash."
        return
    }

    $actualHash = Get-FileSha256 $ActualPath
    if ($RecordedHash -cne $actualHash) {
        Add-Failure "$Description does not match $ActualPath."
    }
}

function Test-DesktopSignature {
    param([Parameter(Mandatory = $true)]$DesktopArchive)

    $entry = $DesktopArchive.Entries |
        Where-Object {
            [string]::Equals(
                $_.FullName,
                "package/win-unpacked/OpenLineOps.exe",
                [System.StringComparison]::Ordinal)
        } |
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
    Invoke-JsonPropertyVerification -Paths @(
        $ManifestPath,
        $ReleaseDependencyInventoryPath,
        $ReleaseProvenancePath)
}

if (Test-Path -LiteralPath $ManifestPath -PathType Leaf) {
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1) {
        Add-Failure "Expected release manifest schemaVersion 1, found $($manifest.schemaVersion)."
    }

    if ($manifest.product -cne "OpenLineOps") {
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

            if ($kind -ceq "source") {
                Test-SensitiveSourceArchiveEntries `
                    -Archive $archive `
                    -ArchiveName $artifactByKind[$kind].fileName
            }

            if ($kind -ceq "desktop" -and $RequireSignedDesktop) {
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
