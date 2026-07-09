param(
    [string] $WorkRoot = "output/release-candidate-inspection-verification",

    [switch] $SkipClean
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RequiredKinds = @("source", "api", "desktop", "plugin-host", "script-worker", "sample-plugin")

Add-Type -AssemblyName System.IO.Compression
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

function New-CleanDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    Assert-UnderRepoRoot $Path
    if ((Test-Path -LiteralPath $Path) -and -not $SkipClean) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function New-TestZip {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string[]] $Entries
    )

    $zipPath = Join-Path $Root $Name
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    $archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entryName in $Entries) {
            $entry = $archive.CreateEntry($entryName)
            $writer = [System.IO.StreamWriter]::new($entry.Open())
            try {
                $writer.WriteLine("test content for $entryName")
            }
            finally {
                $writer.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Invoke-ReleaseManifestGeneration {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Version
    )

    $manifestPath = Join-Path $Root "release-manifest.json"
    $checksumsPath = Join-Path $Root "checksums.sha256"
    $notesPath = Join-Path $Root "release-notes.md"
    $manifestProject = Resolve-RepoPath "tools/OpenLineOps.ReleaseManifest/OpenLineOps.ReleaseManifest.csproj"

    $arguments = @(
        "run",
        "--project",
        $manifestProject,
        "--",
        "--version",
        $Version,
        "--artifacts",
        $Root,
        "--output",
        $manifestPath,
        "--checksums",
        $checksumsPath,
        "--notes",
        $notesPath
    )

    foreach ($kind in $RequiredKinds) {
        $arguments += @("--require-kind", $kind)
    }

    $output = & dotnet @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host ($output | Out-String)
        throw "Failed to generate release manifest for fixture '$Root'."
    }
}

function Write-TestProvenance {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Version,
        [string] $RecordedVersion
    )

    $manifestPath = Join-Path $Root "release-manifest.json"
    $checksumsPath = Join-Path $Root "checksums.sha256"
    $notesPath = Join-Path $Root "release-notes.md"
    $dependencyInventoryPath = Join-Path $Root "release-dependency-inventory.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

    if ([string]::IsNullOrWhiteSpace($RecordedVersion)) {
        $RecordedVersion = $Version
    }

    $artifacts = @($manifest.artifacts | ForEach-Object {
        [ordered]@{
            kind = $_.kind
            relativePath = $_.relativePath
            fileName = $_.fileName
            sizeBytes = $_.sizeBytes
            sha256 = $_.sha256
        }
    })

    $provenance = [ordered]@{
        schemaVersion = 1
        product = "OpenLineOps"
        version = $RecordedVersion
        generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
        source = [ordered]@{
            available = $false
            commit = $null
            branch = $null
            dirty = $null
        }
        build = [ordered]@{
            configuration = "Release"
            noRestore = $true
            skipDesktopBuild = $true
            signDesktopPackage = $false
            requiredArtifactKinds = $RequiredKinds
        }
        tools = [ordered]@{
            powershell = $PSVersionTable.PSVersion.ToString()
            dotnetSdk = "test"
            node = "test"
            npm = "test"
        }
        release = [ordered]@{
            manifest = [ordered]@{
                path = "release-manifest.json"
                sha256 = Get-FileSha256 $manifestPath
            }
            checksums = [ordered]@{
                path = "checksums.sha256"
                sha256 = Get-FileSha256 $checksumsPath
            }
            notes = [ordered]@{
                path = "release-notes.md"
                sha256 = Get-FileSha256 $notesPath
            }
            dependencyInventory = [ordered]@{
                path = "release-dependency-inventory.json"
                sha256 = Get-FileSha256 $dependencyInventoryPath
            }
        }
        artifacts = $artifacts
    }

    $provenancePath = Join-Path $Root "release-provenance.json"
    [System.IO.File]::WriteAllText(
        $provenancePath,
        (($provenance | ConvertTo-Json -Depth 12) + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
}

function Write-TestMetadataChecksums {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [switch] $Tamper
    )

    $metadata = [ordered]@{
        "release-manifest.json" = (Join-Path $Root "release-manifest.json")
        "checksums.sha256" = (Join-Path $Root "checksums.sha256")
        "release-notes.md" = (Join-Path $Root "release-notes.md")
        "release-dependency-inventory.json" = (Join-Path $Root "release-dependency-inventory.json")
        "release-provenance.json" = (Join-Path $Root "release-provenance.json")
    }

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $metadata.GetEnumerator()) {
        $hash = Get-FileSha256 $entry.Value
        if ($Tamper -and $entry.Key -eq "release-provenance.json") {
            $hash = "0000000000000000000000000000000000000000000000000000000000000000"
        }

        $lines.Add("$hash  $($entry.Key)") | Out-Null
    }

    [System.IO.File]::WriteAllText(
        (Join-Path $Root "release-metadata-checksums.sha256"),
        (($lines -join "`r`n") + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
}

function Write-TestDependencyInventory {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Version,
        [string] $RecordedVersion
    )

    if ([string]::IsNullOrWhiteSpace($RecordedVersion)) {
        $RecordedVersion = $Version
    }

    $inventory = [ordered]@{
        schemaVersion = 1
        product = "OpenLineOps"
        version = $RecordedVersion
        generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
        packageCounts = [ordered]@{
            total = 2
            nuget = 1
            npm = 1
            uniqueLicenseValues = 1
        }
        reviewPolicy = [ordered]@{
            blockedLicensePatterns = @("AGPL", "GPL", "LGPL", "SSPL")
        }
        packages = @(
            [ordered]@{
                ecosystem = "nuget"
                name = "Example.NuGet"
                version = "1.0.0"
                license = "MIT"
                licenseSource = "license:expression"
            },
            [ordered]@{
                ecosystem = "npm"
                name = "example-npm"
                version = "1.0.0"
                license = "MIT"
                licenseSource = "package-lock"
            }
        )
    }

    $inventoryPath = Join-Path $Root "release-dependency-inventory.json"
    [System.IO.File]::WriteAllText(
        $inventoryPath,
        (($inventory | ConvertTo-Json -Depth 8) + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
}

function New-MinimalReleaseCandidate {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [string[]] $ExtraSourceEntries = @(),
        [string] $ProvenanceVersion,
        [string] $DependencyInventoryVersion,
        [switch] $SkipDependencyInventory,
        [switch] $SkipMetadataChecksums,
        [switch] $TamperMetadataChecksums,
        [switch] $SkipProvenance
    )

    $root = Join-Path $ResolvedWorkRoot $Name
    New-CleanDirectory $root

    $version = "0.0.0-$Name"
    New-TestZip -Root $root -Name "source-openlineops-$version.zip" -Entries (@(
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
    ) + $ExtraSourceEntries)
    New-TestZip -Root $root -Name "api-openlineops-$version.zip" -Entries @("OpenLineOps.Api.dll")
    New-TestZip -Root $root -Name "desktop-openlineops-$version.zip" -Entries @(
        "dist/index.html",
        "dist-electron/main/main.js",
        "dist-electron/preload/preload.js",
        "package/win-unpacked/OpenLineOps.exe",
        "package/win-unpacked/OPENLINEOPS-PACKAGE-NOTES.txt",
        "package/win-unpacked/resources/app/package.json")
    New-TestZip -Root $root -Name "plugin-host-openlineops-$version.zip" -Entries @("OpenLineOps.PluginHost.dll")
    New-TestZip -Root $root -Name "script-worker-openlineops-$version.zip" -Entries @("OpenLineOps.ScriptWorker.dll")
    New-TestZip -Root $root -Name "sample-plugin-loopback-device-$version.zip" -Entries @(
        "manifest.json",
        "OpenLineOps.SamplePlugins.LoopbackDevice.dll")

    Invoke-ReleaseManifestGeneration -Root $root -Version $version
    if (-not $SkipDependencyInventory) {
        Write-TestDependencyInventory -Root $root -Version $version -RecordedVersion $DependencyInventoryVersion
    }

    if (-not $SkipProvenance) {
        Write-TestProvenance -Root $root -Version $version -RecordedVersion $ProvenanceVersion
    }

    if (-not $SkipMetadataChecksums) {
        Write-TestMetadataChecksums -Root $root -Tamper:$TamperMetadataChecksums
    }

    return $root
}

function Invoke-Inspection {
    param([Parameter(Mandatory = $true)][string] $Root)

    $inspectionScript = Resolve-RepoPath "eng/inspect-release-candidate.ps1"
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $inspectionScript -ArtifactsRoot $Root 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Text = ($output | Out-String)
    }
}

function Assert-InspectionPasses {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $result = Invoke-Inspection -Root $Root
    if ($result.ExitCode -ne 0) {
        Write-Host $result.Text
        throw "Expected fixture '$Name' to pass release candidate inspection."
    }

    Write-Host "Fixture '$Name' passed inspection."
}

function Assert-InspectionFails {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $ExpectedPattern
    )

    $result = Invoke-Inspection -Root $Root
    if ($result.ExitCode -eq 0) {
        throw "Expected fixture '$Name' to fail release candidate inspection."
    }

    if ($result.Text -notmatch $ExpectedPattern) {
        Write-Host $result.Text
        throw "Fixture '$Name' failed for an unexpected reason."
    }

    Write-Host "Fixture '$Name' failed as expected."
}

$ResolvedWorkRoot = Resolve-RepoPath $WorkRoot
Assert-UnderRepoRoot $ResolvedWorkRoot
New-CleanDirectory $ResolvedWorkRoot

$positiveRoot = New-MinimalReleaseCandidate -Name "positive"
Assert-InspectionPasses -Root $positiveRoot -Name "positive"

$unsafePathRoot = New-MinimalReleaseCandidate -Name "unsafe-path" -ExtraSourceEntries @("../evil.txt")
Assert-InspectionFails `
    -Root $unsafePathRoot `
    -Name "unsafe-path" `
    -ExpectedPattern "unsafe zip entry path segment|path traversal zip entry"

$sensitiveSourceRoot = New-MinimalReleaseCandidate -Name "sensitive-source" -ExtraSourceEntries @("certs/openlineops-code-signing.pfx")
Assert-InspectionFails `
    -Root $sensitiveSourceRoot `
    -Name "sensitive-source" `
    -ExpectedPattern "sensitive source archive entry"

$badProvenanceRoot = New-MinimalReleaseCandidate -Name "bad-provenance" -ProvenanceVersion "0.0.0-wrong"
Assert-InspectionFails `
    -Root $badProvenanceRoot `
    -Name "bad-provenance" `
    -ExpectedPattern "Release provenance version"

$missingProvenanceRoot = New-MinimalReleaseCandidate -Name "missing-provenance" -SkipProvenance -SkipMetadataChecksums
Assert-InspectionFails `
    -Root $missingProvenanceRoot `
    -Name "missing-provenance" `
    -ExpectedPattern "release-provenance\.json"

$missingDependencyInventoryRoot = New-MinimalReleaseCandidate -Name "missing-dependency-inventory" -SkipDependencyInventory -SkipProvenance -SkipMetadataChecksums
Assert-InspectionFails `
    -Root $missingDependencyInventoryRoot `
    -Name "missing-dependency-inventory" `
    -ExpectedPattern "release-dependency-inventory\.json"

$badDependencyInventoryRoot = New-MinimalReleaseCandidate -Name "bad-dependency-inventory" -DependencyInventoryVersion "0.0.0-wrong"
Assert-InspectionFails `
    -Root $badDependencyInventoryRoot `
    -Name "bad-dependency-inventory" `
    -ExpectedPattern "Dependency inventory version"

$missingMetadataChecksumsRoot = New-MinimalReleaseCandidate -Name "missing-metadata-checksums" -SkipMetadataChecksums
Assert-InspectionFails `
    -Root $missingMetadataChecksumsRoot `
    -Name "missing-metadata-checksums" `
    -ExpectedPattern "release-metadata-checksums\.sha256"

$badMetadataChecksumsRoot = New-MinimalReleaseCandidate -Name "bad-metadata-checksums" -TamperMetadataChecksums
Assert-InspectionFails `
    -Root $badMetadataChecksumsRoot `
    -Name "bad-metadata-checksums" `
    -ExpectedPattern "Metadata checksum for release-provenance\.json"

Write-Host "Release candidate inspection verification passed."
exit 0
