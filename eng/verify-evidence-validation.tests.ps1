param(
    [string] $WorkRoot = "output/evidence-validation-tests"
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ProductionVerifier = Join-Path $PSScriptRoot "verify-production-closure-evidence.ps1"
$StagedAgentVerifier = Join-Path $PSScriptRoot "verify-staged-agent-evidence.ps1"

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Assert-UnderRepoRoot {
    param([Parameter(Mandatory = $true)][string] $Path)

    $resolved = [System.IO.Path]::GetFullPath($Path)
    $prefix = $RepoRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Evidence validation test path must stay inside the repository: $resolved"
    }
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Content
    )

    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Write-Json {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Value
    )

    Write-Utf8NoBom -Path $Path -Content (($Value | ConvertTo-Json -Depth 40) + [Environment]::NewLine)
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Reset-Directory {
    param([Parameter(Mandatory = $true)][string] $Path)

    Assert-UnderRepoRoot $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Update-ProductionEvidenceManifest {
    param([Parameter(Mandatory = $true)][string] $RunRoot)

    $resolvedRoot = [System.IO.Path]::GetFullPath($RunRoot).TrimEnd('\', '/')
    $prefix = $resolvedRoot + [System.IO.Path]::DirectorySeparatorChar
    $entryByPath = [System.Collections.Generic.Dictionary[string,object]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($file in Get-ChildItem -LiteralPath $RunRoot -Recurse -File) {
        if ($file.Name -ceq "evidence-manifest.json") {
            continue
        }
        $relativePath = $file.FullName.Substring($prefix.Length).Replace('\', '/')
        $entryByPath.Add($relativePath, [ordered]@{
                relativePath = $relativePath
                sizeBytes = $file.Length
                sha256 = Get-FileSha256 $file.FullName
            })
    }
    $paths = [string[]]@($entryByPath.Keys)
    [System.Array]::Sort($paths, [System.StringComparer]::Ordinal)
    Write-Json `
        -Path (Join-Path $RunRoot "evidence-manifest.json") `
        -Value ([ordered]@{
            schema = "openlineops.production-closure-evidence-manifest"
            schemaVersion = 1
            generatedAtUtc = "2026-07-15T00:12:00Z"
            files = @($paths | ForEach-Object { $entryByPath[$_] })
        })
}

function Update-SummaryFileReference {
    param(
        [Parameter(Mandatory = $true)][string] $RunRoot,
        [Parameter(Mandatory = $true)][string] $RelativePath
    )

    $summaryPath = Join-Path $RunRoot "summary.json"
    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    $reference = if ($RelativePath -ceq "public-release/frozen-manifest.json") {
        $summary.frozenRelease.releaseManifest
    }
    elseif ($RelativePath -match '^public-release/station-packages/') {
        @($summary.frozenRelease.stationPackages | Where-Object {
                $_.relativePath -ceq $RelativePath
            }) | Select-Object -First 1
    }
    elseif ($RelativePath -match '^public-release/deployment-catalog/') {
        @($summary.frozenRelease.deploymentCatalogs | Where-Object {
                $_.relativePath -ceq $RelativePath
            }) | Select-Object -First 1
    }
    else { $null }
    if ($null -eq $reference) { throw "No summary file reference exists for '$RelativePath'." }
    $path = Join-Path $RunRoot $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $file = Get-Item -LiteralPath $path
    $reference.sizeBytes = $file.Length
    $reference.sha256 = Get-FileSha256 $path
    Write-Json -Path $summaryPath -Value $summary
}

function Read-ZipTextEntry {
    param([string] $Path, [string] $EntryName)
    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $false)
        try {
            $entry = $archive.GetEntry($EntryName)
            if ($null -eq $entry) { throw "Zip entry '$EntryName' is missing." }
            $reader = [System.IO.StreamReader]::new($entry.Open(), [System.Text.Encoding]::UTF8)
            try { return $reader.ReadToEnd() }
            finally { $reader.Dispose() }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Set-ZipTextEntries {
    param(
        [string] $Path,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Entries
    )
    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Update,
            $false)
        try {
            foreach ($name in $Entries.Keys) {
                $existing = $archive.GetEntry([string]$name)
                if ($null -eq $existing) { throw "Zip entry '$name' is missing." }
                $existing.Delete()
                $replacement = $archive.CreateEntry(
                    [string]$name,
                    [System.IO.Compression.CompressionLevel]::Optimal)
                $writer = [System.IO.StreamWriter]::new(
                    $replacement.Open(),
                    [System.Text.UTF8Encoding]::new($false))
                try { $writer.Write([string]$Entries[$name]) }
                finally { $writer.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Add-ZipTextEntry {
    param([string] $Path, [string] $EntryName, [string] $Text)
    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Update,
            $false)
        try {
            $entry = $archive.CreateEntry(
                $EntryName,
                [System.IO.Compression.CompressionLevel]::Optimal)
            $writer = [System.IO.StreamWriter]::new(
                $entry.Open(),
                [System.Text.UTF8Encoding]::new($false))
            try { $writer.Write($Text) }
            finally { $writer.Dispose() }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Invoke-ExpectedFailure {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][scriptblock] $Action,
        [Parameter(Mandatory = $true)][string] $Pattern
    )

    $message = $null
    try {
        & $Action
    }
    catch {
        $message = $_.Exception.Message
    }
    if ([string]::IsNullOrWhiteSpace($message)) {
        throw "Mutation '$Name' unexpectedly passed."
    }
    if ($message -notmatch $Pattern) {
        throw "Mutation '$Name' failed for the wrong reason: $message"
    }
    Write-Host "Mutation rejected: $Name"
}

. (Join-Path $PSScriptRoot "evidence-validation-test-fixtures.ps1")

$resolvedWorkRoot = Resolve-RepoPath $WorkRoot
Reset-Directory $resolvedWorkRoot
$productionParent = Join-Path $resolvedWorkRoot "production"
$productionRunRoot = Join-Path $productionParent "run"
$stagedAgentRoot = Join-Path $resolvedWorkRoot "staged-agent"

function Reset-ProductionFixture {
    param(
        [string] $StationOperationSourcePath = "source/operation.py",
        [string] $StationOperationSourceMediaType = "text/x-python",
        [string] $StationOperationSourceText = "print('fixture operation')`n",
        [AllowNull()][byte[]] $StationOperationSourceBytes = $null)

    if (Test-Path -LiteralPath $productionParent) {
        Remove-Item -LiteralPath $productionParent -Recurse -Force
    }
    Write-ProductionClosureEvidenceFixture `
        -RunRoot $productionRunRoot `
        -StationOperationSourcePath $StationOperationSourcePath `
        -StationOperationSourceMediaType $StationOperationSourceMediaType `
        -StationOperationSourceText $StationOperationSourceText `
        -StationOperationSourceBytes $StationOperationSourceBytes
}

function Reset-StagedAgentFixture {
    if (Test-Path -LiteralPath $stagedAgentRoot) {
        Remove-Item -LiteralPath $stagedAgentRoot -Recurse -Force
    }
    Write-StagedAgentEvidenceFixture -Root $stagedAgentRoot
}

function Set-StagedExactTestHash {
    param(
        [Parameter(Mandatory = $true)] $Evidence,
        [Parameter(Mandatory = $true)][int] $Index,
        [Parameter(Mandatory = $true)][string] $Sha256
    )

    $Evidence.exactTestEvidence[$Index].trxSha256 = $Sha256
    switch ($Index) {
        0 { $Evidence.signedPackageProcessChains[0].exactTest.trxSha256 = $Sha256 }
        1 { $Evidence.signedPackageProcessChains[1].exactTest.trxSha256 = $Sha256 }
        2 { $Evidence.productionPythonPolicy.stagedIsolationTest.trxSha256 = $Sha256 }
        3 { $Evidence.productionPythonPolicy.stagedCrashRecoveryTest.trxSha256 = $Sha256 }
        4 { $Evidence.productionPythonPolicy.stagedPythonRuntimeProvisioningTest.trxSha256 = $Sha256 }
        default { throw "Unknown staged exact-test index: $Index" }
    }
}

function Set-StagedMaterialArrivalIpcField {
    param(
        [Parameter(Mandatory = $true)][string] $Field,
        [Parameter(Mandatory = $true)] $Value
    )

    $evidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
    $rawPath = Join-Path $stagedAgentRoot "rabbitmq-process/evidence.json"
    $raw = Get-Content -LiteralPath $rawPath -Raw | ConvertFrom-Json
    if ($evidence.rabbitMqTransportCoverage.materialArrivalIpc.PSObject.Properties.Name `
        -cnotcontains $Field `
        -or $raw.materialArrivalIpc.PSObject.Properties.Name -cnotcontains $Field) {
        throw "Unknown staged Agent material-arrival IPC fixture field '$Field'."
    }

    $evidence.rabbitMqTransportCoverage.materialArrivalIpc.$Field = $Value
    $raw.materialArrivalIpc.$Field = $Value
    Write-Json -Path $rawPath -Value $raw
    $evidence.rabbitMqTransportCoverage.evidenceSha256 = Get-FileSha256 $rawPath
    Write-Json -Path $stagedEvidencePath -Value $evidence
}

function Set-StagedImmutableContentCacheField {
    param(
        [Parameter(Mandatory = $true)][string] $Field,
        [Parameter(Mandatory = $true)] $Value
    )

    $evidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
    $rawPath = Join-Path $stagedAgentRoot "rabbitmq-process/evidence.json"
    $raw = Get-Content -LiteralPath $rawPath -Raw | ConvertFrom-Json
    if ($evidence.rabbitMqTransportCoverage.immutableContentCache.PSObject.Properties.Name `
        -cnotcontains $Field `
        -or $raw.immutableContentCache.PSObject.Properties.Name -cnotcontains $Field) {
        throw "Unknown staged Agent immutable content-cache fixture field '$Field'."
    }

    $evidence.rabbitMqTransportCoverage.immutableContentCache.$Field = $Value
    $raw.immutableContentCache.$Field = $Value
    Write-Json -Path $rawPath -Value $raw
    $evidence.rabbitMqTransportCoverage.evidenceSha256 = Get-FileSha256 $rawPath
    Write-Json -Path $stagedEvidencePath -Value $evidence
}

Reset-ProductionFixture
& $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed

Reset-ProductionFixture
$summaryPath = Join-Path $productionRunRoot "summary.json"
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.externalProgramTrial.directoryImport.entryPoint = "files/bin/not-imported.exe"
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production external program entry point missing from imported directory" `
    -Pattern "directory import entry point or file inventory" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.externalProgramTrial.directoryImport.preservedSameBasenames[1] =
    $summary.externalProgramTrial.directoryImport.entryPoint
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production external program nested same-basename proof is false" `
    -Pattern "same basename" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$summaryPath = Join-Path $productionRunRoot "summary.json"
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.applicationPortability.afterPublishTreeSha256 = "f" * 64
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "portable Application changed after publish" `
    -Pattern "portability file-tree hashes differ" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.applicationPortability.unchanged = $false
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "portable Application unchanged proof removed" `
    -Pattern "unchanged Application copied across two Projects" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture `
    -StationOperationSourceText "print('C:\private\customer-project\vendor.py')`n"
Invoke-ExpectedFailure `
    -Name "signed text/x-python package member contains absolute path" `
    -Pattern "credential|absolute-path|sensitive" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture `
    -StationOperationSourcePath "source/operation.payload" `
    -StationOperationSourceMediaType "application/octet-stream" `
    -StationOperationSourceText "password=fixture-secret-value`n"
Invoke-ExpectedFailure `
    -Name "decodable package member with unknown type contains credential" `
    -Pattern "credential|absolute-path|sensitive" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

[byte[]]$utf16OperationSource = [System.Text.Encoding]::Unicode.GetPreamble() `
    + [System.Text.Encoding]::Unicode.GetBytes("C:\private\utf16-customer-project")
Reset-ProductionFixture `
    -StationOperationSourcePath "source/operation.payload" `
    -StationOperationSourceMediaType "application/octet-stream" `
    -StationOperationSourceBytes $utf16OperationSource
Invoke-ExpectedFailure `
    -Name "UTF-16 package member with unknown type contains absolute path" `
    -Pattern "credential|absolute-path|sensitive" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

[byte[]]$invalidDeclaredText = @([byte]0xff, [byte]0x00, [byte]0xfe)
Reset-ProductionFixture `
    -StationOperationSourceBytes $invalidDeclaredText
Invoke-ExpectedFailure `
    -Name "declared text package member is not strictly decodable" `
    -Pattern "declared as text" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture `
    -StationOperationSourceText "password = os.getenv('VENDOR_PASSWORD')`n"
& $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed
Write-Host "Source-code environment reference did not produce a credential false positive."

[byte[]]$binaryOperationSource = @(
    [byte]0xff,
    [byte]0x00) + [System.Text.Encoding]::ASCII.GetBytes("C:\private\binary-symbol")
Reset-ProductionFixture `
    -StationOperationSourcePath "source/operation.payload" `
    -StationOperationSourceMediaType "application/octet-stream" `
    -StationOperationSourceBytes $binaryOperationSource
& $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed
Write-Host "Binary package member did not produce a text-content false positive."

Reset-ProductionFixture
$summaryPath = Join-Path $productionRunRoot "summary.json"
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.scenarios.PSObject.Properties.Remove("recovery")
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production summary missing recovery scenario" `
    -Pattern "exactly six scenarios|recovery" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
Write-Utf8NoBom `
    -Path (Join-Path $productionRunRoot "studio-standard.token") `
    -Content "fixture-secret-must-never-be-public"
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production evidence contains token path" `
    -Pattern "forbidden.*path" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary | Add-Member -NotePropertyName apiAccessToken -NotePropertyValue "fixture-secret"
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production evidence contains credential text" `
    -Pattern "credential|private-key" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary | Add-Member -NotePropertyName logs -NotePropertyValue @("sanitized diagnostic")
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production summary contains unknown logs" `
    -Pattern "forbidden property|exactly" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent }

Reset-ProductionFixture
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.packagedExecutable = "C:\private\OpenLineOps.exe"
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production summary contains absolute packaged executable path" `
    -Pattern "canonical|unsafe string|absolute-path" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent }

Reset-ProductionFixture
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.packagedBinaries.before.desktopExecutable.path = "C:\private\OpenLineOps.exe"
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production binary before contains absolute path" `
    -Pattern "canonical|unsafe string|absolute-path" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent }

Reset-ProductionFixture
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.packagedBinaries.after.desktopExecutable.path = "../different/OpenLineOps.exe"
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production binary after contains traversal" `
    -Pattern "canonical|unsafe string|absolute-path|before/after" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent }

Reset-ProductionFixture
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.packagedBinaries.after.desktopExecutable.sha256 = "f" * 64
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production packaged binary byte identity changed" `
    -Pattern "identity changed|immutable" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.packagedBinaries.after.runtimeApiExecutable.sizeBytes++
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production packaged binary size identity changed" `
    -Pattern "identity changed|immutable" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.packagedBinaries.after.desktopExecutable.modifiedAtUtc = "2026-07-15T00:00:01Z"
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production packaged binary mtime identity changed" `
    -Pattern "identity changed|immutable" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.scenarios.vendorPassed.immutableRunTrace.after.sha256 = "f" * 64
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production immutable Run Trace changed after unload" `
    -Pattern "immutable Run Trace" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.scenarios.operatorCancel.vendorProcessesBeforeCancel[0] |
    Add-Member -NotePropertyName commandLine -NotePropertyValue "sanitized command"
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production public process contains command line" `
    -Pattern "forbidden property|exactly" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent }

Reset-ProductionFixture
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.scenarios.vendorCrash.incidents[0] |
    Add-Member -NotePropertyName message -NotePropertyValue "sanitized Incident"
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production public Incident contains message" `
    -Pattern "forbidden property|exactly" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent }

Reset-ProductionFixture
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.scenarios.vendorFailedRework.run.operations[0] |
    Add-Member -NotePropertyName resultPayload -NotePropertyValue '{"vendor":"raw"}'
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production public Operation contains result payload" `
    -Pattern "forbidden property|exactly" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent }

Reset-ProductionFixture
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$summary.scenarios.vendorFailedRework.run.operations[0] |
    Add-Member -NotePropertyName rawData -NotePropertyValue "sanitized but unknown"
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "production public Operation contains unknown nested property" `
    -Pattern "exactly" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent }

Reset-ProductionFixture
$frozenManifestPath = Join-Path $productionRunRoot "public-release/frozen-manifest.json"
$frozenManifest = Get-Content -LiteralPath $frozenManifestPath -Raw | ConvertFrom-Json
$frozenManifest.metadata | Add-Member -NotePropertyName sourcePath -NotePropertyValue "C:\workspace\customer\secret-project"
Write-Json -Path $frozenManifestPath -Value $frozenManifest
Update-SummaryFileReference $productionRunRoot "public-release/frozen-manifest.json"
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "frozen release manifest contains local absolute path" `
    -Pattern "absolute-path|unsafe string" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$frozenManifest = Get-Content -LiteralPath $frozenManifestPath -Raw | ConvertFrom-Json
$frozenManifest.metadata | Add-Member -NotePropertyName sourcePath -NotePropertyValue "\\server\share\customer-project"
Write-Json -Path $frozenManifestPath -Value $frozenManifest
Update-SummaryFileReference $productionRunRoot "public-release/frozen-manifest.json"
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "frozen release manifest contains JSON-escaped UNC path" `
    -Pattern "absolute-path|unsafe string" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$catalogPath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/deployment-catalog") -File)[0].FullName
$catalogRelativePath = "public-release/deployment-catalog/$([System.IO.Path]::GetFileName($catalogPath))"
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
$catalog.projectId = "project.forged"
Write-Json -Path $catalogPath -Value $catalog
Update-SummaryFileReference $productionRunRoot $catalogRelativePath
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "deployment catalog identity differs from frozen release" `
    -Pattern "deployment catalog.*bind" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$catalogPath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/deployment-catalog") -File)[0].FullName
$catalogOldRelativePath = "public-release/deployment-catalog/$([System.IO.Path]::GetFileName($catalogPath))"
$catalogNewRelativePath = "public-release/deployment-catalog/$("f" * 64).json"
$catalogNewPath = Join-Path $productionRunRoot $catalogNewRelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
Move-Item -LiteralPath $catalogPath -Destination $catalogNewPath
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$catalogReference = @($summary.frozenRelease.deploymentCatalogs | Where-Object {
        $_.relativePath -ceq $catalogOldRelativePath
    })[0]
$catalogReference.relativePath = $catalogNewRelativePath
$catalogReference.sizeBytes = (Get-Item -LiteralPath $catalogNewPath).Length
$catalogReference.sha256 = Get-FileSha256 $catalogNewPath
Write-Json -Path $summaryPath -Value $summary
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "deployment catalog filename is not canonical identity hash" `
    -Pattern "deployment catalog.*bind" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$packagePath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/station-packages") -File)[0].FullName
$packageRelativePath = "public-release/station-packages/$([System.IO.Path]::GetFileName($packagePath))"
$signatureDocument = Read-ZipTextEntry $packagePath "package.signature.json" | ConvertFrom-Json
[byte[]]$forgedSignature = [System.Convert]::FromBase64String([string]$signatureDocument.signature)
$forgedSignature[0] = $forgedSignature[0] -bxor 1
$signatureDocument.signature = [System.Convert]::ToBase64String($forgedSignature)
Set-ZipTextEntries $packagePath ([ordered]@{
        "package.signature.json" = ($signatureDocument | ConvertTo-Json -Compress)
    })
Update-SummaryFileReference $productionRunRoot $packageRelativePath
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "Station package RSA-PSS signature changed" `
    -Pattern "RSA-PSS signature verification failed" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$packagePath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/station-packages") -File)[0].FullName
$packageRelativePath = "public-release/station-packages/$([System.IO.Path]::GetFileName($packagePath))"
$packageManifest = Read-ZipTextEntry $packagePath "package.manifest.json" | ConvertFrom-Json
$packageManifest.contentSha256 = "f" * 64
Set-ZipTextEntries $packagePath ([ordered]@{
        "package.manifest.json" = (($packageManifest | ConvertTo-Json -Depth 20 -Compress) + "`n")
    })
Update-SummaryFileReference $productionRunRoot $packageRelativePath
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "Station package claimed content identity changed" `
    -Pattern "manifest identity" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$packagePath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/station-packages") -File)[0].FullName
$packageRelativePath = "public-release/station-packages/$([System.IO.Path]::GetFileName($packagePath))"
$packageManifest = Read-ZipTextEntry $packagePath "package.manifest.json" | ConvertFrom-Json
$configurationManifestEntry = @($packageManifest.entries | Where-Object {
        $_.path -ceq "source/station-configuration.json"
    })[0]
$configurationManifestEntry.mediaType = "application/vnd.forged+json"
Set-ZipTextEntries $packagePath ([ordered]@{
        "package.manifest.json" = (($packageManifest | ConvertTo-Json -Depth 20 -Compress) + "`n")
    })
Update-SummaryFileReference $productionRunRoot $packageRelativePath
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "Station package canonical content hash changed" `
    -Pattern "content hash.*canonical entry manifest" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$packagePath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/station-packages") -File)[0].FullName
$packageRelativePath = "public-release/station-packages/$([System.IO.Path]::GetFileName($packagePath))"
$packageManifest = Read-ZipTextEntry $packagePath "package.manifest.json" | ConvertFrom-Json
$packageManifest.entries = @($packageManifest.entries[1], $packageManifest.entries[0])
Set-ZipTextEntries $packagePath ([ordered]@{
        "package.manifest.json" = (($packageManifest | ConvertTo-Json -Depth 20 -Compress) + "`n")
    })
Update-SummaryFileReference $productionRunRoot $packageRelativePath
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "Station package manifest entries are not ordinal" `
    -Pattern "strictly ordered|missing, duplicated, or invalid" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$packagePath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/station-packages") -File)[0].FullName
$packageRelativePath = "public-release/station-packages/$([System.IO.Path]::GetFileName($packagePath))"
$packageManifest = Read-ZipTextEntry $packagePath "package.manifest.json" | ConvertFrom-Json
$packageManifest.createdAtUtc = "2026-07-15T08:00:00+08:00"
Set-ZipTextEntries $packagePath ([ordered]@{
        "package.manifest.json" = (($packageManifest | ConvertTo-Json -Depth 20 -Compress) + "`n")
    })
Update-SummaryFileReference $productionRunRoot $packageRelativePath
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "Station package creation timestamp is not UTC offset zero" `
    -Pattern "manifest identity" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$packagePath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/station-packages") -File)[0].FullName
$packageRelativePath = "public-release/station-packages/$([System.IO.Path]::GetFileName($packagePath))"
Add-ZipTextEntry $packagePath "SOURCE/station-configuration.json" "{}"
Update-SummaryFileReference $productionRunRoot $packageRelativePath
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "Station package archive contains case-insensitive path collision" `
    -Pattern "duplicate|canonical" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$packagePath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/station-packages") -File)[0].FullName
$packageRelativePath = "public-release/station-packages/$([System.IO.Path]::GetFileName($packagePath))"
Add-ZipTextEntry $packagePath "unsafe./entry.json" "{}"
Update-SummaryFileReference $productionRunRoot $packageRelativePath
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "Station package archive path segment has trailing dot" `
    -Pattern "canonical Station package path" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$packagePath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/station-packages") -File)[0].FullName
$packageRelativePath = "public-release/station-packages/$([System.IO.Path]::GetFileName($packagePath))"
Add-ZipTextEntry $packagePath "source/CON.txt" "reserved"
Update-SummaryFileReference $productionRunRoot $packageRelativePath
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "Station package archive contains Windows reserved segment" `
    -Pattern "canonical Station package path" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$packagePath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/station-packages") -File)[0].FullName
$packageRelativePath = "public-release/station-packages/$([System.IO.Path]::GetFileName($packagePath))"
Add-ZipTextEntry $packagePath "source/invalid?.json" "invalid"
Update-SummaryFileReference $productionRunRoot $packageRelativePath
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "Station package archive contains invalid Windows path character" `
    -Pattern "canonical Station package path" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$packagePath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/station-packages") -File)[0].FullName
$packageRelativePath = "public-release/station-packages/$([System.IO.Path]::GetFileName($packagePath))"
$nonNormalizedEntry = "source/e$([char]0x0301).json"
Add-ZipTextEntry $packagePath $nonNormalizedEntry "non-normalized"
Update-SummaryFileReference $productionRunRoot $packageRelativePath
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "Station package archive path is not NFC" `
    -Pattern "canonical Station package path" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$packagePath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/station-packages") -File)[0].FullName
$packageRelativePath = "public-release/station-packages/$([System.IO.Path]::GetFileName($packagePath))"
$oversizedSegmentEntry = "source/$(('a' * 251)).json"
Add-ZipTextEntry $packagePath $oversizedSegmentEntry "oversized-segment"
Update-SummaryFileReference $productionRunRoot $packageRelativePath
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "Station package archive segment exceeds UTF-8 limit" `
    -Pattern "canonical Station package path" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$packagePath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/station-packages") -File)[0].FullName
$packageRelativePath = "public-release/station-packages/$([System.IO.Path]::GetFileName($packagePath))"
$longSegment = 'a' * 250
$oversizedPathEntry = "$longSegment/$longSegment/$longSegment/$longSegment/$longSegment.txt"
Add-ZipTextEntry $packagePath $oversizedPathEntry "oversized-path"
Update-SummaryFileReference $productionRunRoot $packageRelativePath
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "Station package archive path exceeds UTF-8 limit" `
    -Pattern "canonical Station package path" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$packagePath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/station-packages") -File)[0].FullName
$packageRelativePath = "public-release/station-packages/$([System.IO.Path]::GetFileName($packagePath))"
Add-ZipTextEntry $packagePath "source/ leading.json" "leading-whitespace"
Update-SummaryFileReference $productionRunRoot $packageRelativePath
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "Station package archive segment starts with whitespace" `
    -Pattern "canonical Station package path" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$packagePath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/station-packages") -File)[0].FullName
$packageRelativePath = "public-release/station-packages/$([System.IO.Path]::GetFileName($packagePath))"
$packageManifest = Read-ZipTextEntry $packagePath "package.manifest.json" | ConvertFrom-Json
$unsafeConfigurationText = ([ordered]@{
        schema = "openlineops.fixture-station-configuration"
        sourcePath = "\\server\share\private-station"
    } | ConvertTo-Json -Compress)
[byte[]]$unsafeConfigurationBytes = [System.Text.Encoding]::UTF8.GetBytes($unsafeConfigurationText)
$configurationEntry = @($packageManifest.entries | Where-Object {
        $_.path -ceq "source/station-configuration.json"
    })[0]
$configurationEntry.length = $unsafeConfigurationBytes.Length
$configurationEntry.sha256 = Get-FixtureBytesSha256 $unsafeConfigurationBytes
Set-ZipTextEntries $packagePath ([ordered]@{
        "source/station-configuration.json" = $unsafeConfigurationText
        "package.manifest.json" = (($packageManifest | ConvertTo-Json -Depth 20 -Compress) + "`n")
    })
Update-SummaryFileReference $productionRunRoot $packageRelativePath
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "Station package JSON entry contains escaped UNC path" `
    -Pattern "absolute-path|unsafe string" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-ProductionFixture
$packagePath = @(Get-ChildItem -LiteralPath (Join-Path $productionRunRoot "public-release/station-packages") -File)[0].FullName
Add-Type -AssemblyName System.IO.Compression
$packageStream = [System.IO.File]::Open(
    $packagePath,
    [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::ReadWrite,
    [System.IO.FileShare]::None)
try {
    $packageArchive = [System.IO.Compression.ZipArchive]::new(
        $packageStream,
        [System.IO.Compression.ZipArchiveMode]::Update,
        $false)
    try {
        $traversalEntry = $packageArchive.CreateEntry("../escaped.txt")
        $entryWriter = [System.IO.StreamWriter]::new($traversalEntry.Open())
        try {
            $entryWriter.Write("escaped")
        }
        finally {
            $entryWriter.Dispose()
        }
    }
    finally {
        $packageArchive.Dispose()
    }
}
finally {
    $packageStream.Dispose()
}
Update-SummaryFileReference `
    $productionRunRoot `
    "public-release/station-packages/$([System.IO.Path]::GetFileName($packagePath))"
Update-ProductionEvidenceManifest $productionRunRoot
Invoke-ExpectedFailure `
    -Name "station package contains zip traversal" `
    -Pattern "traversal|non-canonical|not a canonical" `
    -Action { & $ProductionVerifier -EvidenceRoot $productionParent -RequirePassed }

Reset-StagedAgentFixture
& $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot

$stagedEvidencePath = Join-Path $stagedAgentRoot "evidence.json"

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.entryPointProbes[0].exitCode = $false
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent service startup exit code is a false boolean" `
    -Pattern "entry-point probe.*value- and hash-bound|non-zero integer" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.entryPointProbes[0].exitCode = "1"
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent service startup exit code is a truthy string" `
    -Pattern "entry-point probe.*value- and hash-bound|non-zero integer" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.entryPointProbes[0] |
    Add-Member -NotePropertyName legacyStartupAccepted -NotePropertyValue $true
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent service startup probe public compatibility field rejected" `
    -Pattern "entry-point probe.*exact strict-schema properties" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.entryPointProbes[0].outputContract = "WindowsServiceName"
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent service startup output contract weakened" `
    -Pattern "entry-point probe.*value- and hash-bound" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.entryPoints[0].sha256 = "f" * 64
$stagedEvidence.entryPointProbes[0].executableSha256 = "f" * 64
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent public entry-point hash rebinding rejected" `
    -Pattern "public evidence is not value- and hash-bound to its raw probe evidence" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEntryPointRawPath = Join-Path `
    $stagedAgentRoot `
    $stagedEvidence.entryPointProbeEvidence.evidence
$stagedEntryPointRaw = Get-Content -LiteralPath $stagedEntryPointRawPath -Raw |
    ConvertFrom-Json
$stagedEntryPointRaw.probes[0] |
    Add-Member -NotePropertyName unexpectedCompatibilityProof -NotePropertyValue $true
Write-Json -Path $stagedEntryPointRawPath -Value $stagedEntryPointRaw
$stagedEvidence.entryPointProbeEvidence.evidenceSha256 =
    Get-FileSha256 $stagedEntryPointRawPath
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent raw entry-point compatibility field rejected after hash rebinding" `
    -Pattern "raw entry-point probe.*exact strict-schema properties" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.exactTestEvidence[0].fullyQualifiedName =
    "OpenLineOps.Agent.Tests.FakeGate.Passes"
$stagedEvidence.signedPackageProcessChains[0].exactTest.fullyQualifiedName =
    "OpenLineOps.Agent.Tests.FakeGate.Passes"
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent exact-test FQN substituted" `
    -Pattern "exact test evidence|exact-test" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedTrxPath = Join-Path `
    $stagedAgentRoot `
    $stagedEvidence.exactTestEvidence[0].trxRelativePath
[xml]$stagedTrx = Get-Content -LiteralPath $stagedTrxPath -Raw
$stagedTrx.TestRun.ResultSummary.Counters.passed = "0"
$stagedTrx.Save($stagedTrxPath)
$stagedTrxSha256 = Get-FileSha256 $stagedTrxPath
Set-StagedExactTestHash `
    -Evidence $stagedEvidence `
    -Index 0 `
    -Sha256 $stagedTrxSha256
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent TRX counters do not prove Passed" `
    -Pattern "sanitized TRX|Passed|zero-Skipped" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.productionPythonPolicy.stagedIsolationTest.trxSha256 = "f" * 64
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent nested exact-test evidence unbound" `
    -Pattern "not bound|binding" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedTrxPath = Join-Path `
    $stagedAgentRoot `
    $stagedEvidence.exactTestEvidence[0].trxRelativePath
[xml]$stagedTrx = Get-Content -LiteralPath $stagedTrxPath -Raw
$outputNode = $stagedTrx.CreateElement(
    "Output",
    "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
$stdoutNode = $stagedTrx.CreateElement(
    "StdOut",
    "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
$stdoutNode.InnerText = "private raw test log"
[void]$outputNode.AppendChild($stdoutNode)
[void]$stagedTrx.TestRun.ResultSummary.AppendChild($outputNode)
$stagedTrx.Save($stagedTrxPath)
$stagedTrxSha256 = Get-FileSha256 $stagedTrxPath
Set-StagedExactTestHash `
    -Evidence $stagedEvidence `
    -Index 0 `
    -Sha256 $stagedTrxSha256
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent TRX contains raw output" `
    -Pattern "raw-log|raw text|sanitized element" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
Write-Utf8NoBom -Path (Join-Path $stagedAgentRoot "dotnet-test.log") -Content "private raw log"
Invoke-ExpectedFailure `
    -Name "staged Agent public root contains raw log" `
    -Pattern "unknown.*file" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.PSObject.Properties.Remove("centralArtifactTransport")
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent missing central artifact transport" `
    -Pattern "RabbitMQ|artifact" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.PSObject.Properties.Remove("windowsServiceName")
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent Windows service name missing" `
    -Pattern "RabbitMQ|service|closure" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.windowsServiceLifecycleVerified = $false
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent Windows service lifecycle not verified" `
    -Pattern "RabbitMQ|service|closure" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

foreach ($mutation in @(
        [pscustomobject]@{
            Name = "staged Agent service token material-arrival connection not verified"
            Field = "serviceTokenConnected"
        },
        [pscustomobject]@{
            Name = "staged Agent material-arrival pipe exact ACL not verified"
            Field = "pipeExactAclVerified"
        },
        [pscustomobject]@{
            Name = "staged Agent material arrival not durably published"
            Field = "durablePublicationVerified"
        },
        [pscustomobject]@{
            Name = "staged Agent ordinary CI token not explicitly denied"
            Field = "ordinaryCiTokenExplicitAccessDenied"
        })) {
    Reset-StagedAgentFixture
    Set-StagedMaterialArrivalIpcField -Field $mutation.Field -Value $false
    Invoke-ExpectedFailure `
        -Name $mutation.Name `
        -Pattern "material-arrival IPC" `
        -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }
}

Reset-StagedAgentFixture
Set-StagedMaterialArrivalIpcField -Field "serviceTokenConnected" -Value 1
Invoke-ExpectedFailure `
    -Name "staged Agent material-arrival proof uses a truthy integer" `
    -Pattern "JSON boolean true" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$rawEvidencePath = Join-Path $stagedAgentRoot "rabbitmq-process/evidence.json"
$rawEvidence = Get-Content -LiteralPath $rawEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.windowsServiceLifecycleVerified = "true"
$rawEvidence.windowsServiceLifecycleVerified = "true"
Write-Json -Path $rawEvidencePath -Value $rawEvidence
$stagedEvidence.rabbitMqTransportCoverage.evidenceSha256 = Get-FileSha256 $rawEvidencePath
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent transport lifecycle proof uses a truthy string" `
    -Pattern "JSON boolean true" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$rawEvidencePath = Join-Path $stagedAgentRoot "rabbitmq-process/evidence.json"
$rawEvidence = Get-Content -LiteralPath $rawEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.agentHostIdentity.nonAdministrative = 1
$rawEvidence.agentHostIdentity.NonAdministrative = 1
Write-Json -Path $rawEvidencePath -Value $rawEvidence
$stagedEvidence.rabbitMqTransportCoverage.evidenceSha256 = Get-FileSha256 $rawEvidencePath
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent identity proof uses a truthy integer" `
    -Pattern "JSON boolean true" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$rawEvidencePath = Join-Path $stagedAgentRoot "rabbitmq-process/evidence.json"
$rawEvidence = Get-Content -LiteralPath $rawEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.presence.startedAndHeartbeatPersisted = "true"
$rawEvidence.presence.startedAndHeartbeatPersisted = "true"
Write-Json -Path $rawEvidencePath -Value $rawEvidence
$stagedEvidence.rabbitMqTransportCoverage.evidenceSha256 = Get-FileSha256 $rawEvidencePath
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent presence proof uses a truthy string" `
    -Pattern "JSON boolean true" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$rawEvidencePath = Join-Path $stagedAgentRoot "rabbitmq-process/evidence.json"
$rawEvidence = Get-Content -LiteralPath $rawEvidencePath -Raw | ConvertFrom-Json
$rawEvidence.broker.tls = 1
Write-Json -Path $rawEvidencePath -Value $rawEvidence
$stagedEvidence.rabbitMqTransportCoverage.evidenceSha256 = Get-FileSha256 $rawEvidencePath
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent raw broker TLS proof uses a truthy integer" `
    -Pattern "JSON boolean" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$rawEvidencePath = Join-Path $stagedAgentRoot "rabbitmq-process/evidence.json"
$rawEvidence = Get-Content -LiteralPath $rawEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.materialArrivalIpc |
    Add-Member -NotePropertyName unexpectedCompatibilityProof -NotePropertyValue $true
$rawEvidence.materialArrivalIpc |
    Add-Member -NotePropertyName unexpectedCompatibilityProof -NotePropertyValue $true
Write-Json -Path $rawEvidencePath -Value $rawEvidence
$stagedEvidence.rabbitMqTransportCoverage.evidenceSha256 = Get-FileSha256 $rawEvidencePath
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent material-arrival IPC compatibility field rejected" `
    -Pattern "exact strict-schema properties" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

$immutableContentCacheFields = @(
    "packagedProvisionCommandVerified",
    "runningServiceAdministrationRejected",
    "serviceTokenReadExecuteVerified",
    "sealedMutationAccessDenied",
    "deepAncestorMutationAccessDenied",
    "preSealRecoveryVerified",
    "cleanupCrashResumeVerified",
    "committedAdminRemovalVerified",
    "packagedRemovalCommandVerified",
    "cacheNamespaceRemoved")
foreach ($field in $immutableContentCacheFields) {
    Reset-StagedAgentFixture
    Set-StagedImmutableContentCacheField -Field $field -Value $false
    Invoke-ExpectedFailure `
        -Name "staged Agent immutable content-cache proof '$field' is false" `
        -Pattern "immutable content-cache|JSON boolean true" `
        -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }
}

foreach ($truthyMutation in @(1, 0, "true", "false")) {
    Reset-StagedAgentFixture
    Set-StagedImmutableContentCacheField `
        -Field "serviceTokenReadExecuteVerified" `
        -Value $truthyMutation
    Invoke-ExpectedFailure `
        -Name "staged Agent immutable content-cache proof rejects non-boolean '$truthyMutation'" `
        -Pattern "JSON boolean true" `
        -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }
}

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$rawEvidencePath = Join-Path $stagedAgentRoot "rabbitmq-process/evidence.json"
$rawEvidence = Get-Content -LiteralPath $rawEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.immutableContentCache |
    Add-Member -NotePropertyName unexpectedCompatibilityProof -NotePropertyValue $true
$rawEvidence.immutableContentCache |
    Add-Member -NotePropertyName unexpectedCompatibilityProof -NotePropertyValue $true
Write-Json -Path $rawEvidencePath -Value $rawEvidence
$stagedEvidence.rabbitMqTransportCoverage.evidenceSha256 = Get-FileSha256 $rawEvidencePath
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent immutable content-cache compatibility field rejected" `
    -Pattern "exact strict-schema properties" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.agentHostIdentity.identityStrategy =
    "temporary-standard-service-account"
$stagedEvidence.rabbitMqTransportCoverage.restartedAgentHostIdentity.identityStrategy =
    "temporary-standard-service-account"
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent legacy identity strategy rejected" `
    -Pattern "service account|identity" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.agentHostIdentity.hasRestrictions = $false
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent unrestricted token rejected" `
    -Pattern "service-SID|identity" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.agentHostIdentity.serviceLogonSidEnabled = $false
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent disabled service logon SID rejected" `
    -Pattern "service-SID|identity" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.agentHostIdentity.exactServiceSidRestricted = $false
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent missing restricted SID membership rejected" `
    -Pattern "service-SID|identity" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.agentHostIdentity.serviceAccountSid = "S-1-5-20"
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent non-LocalService account SID rejected" `
    -Pattern "service-SID|identity" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.restartedAgentHostIdentity.serviceSid =
    "S-1-5-80-1-2-3-4-5"
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent restart service SID mismatch rejected" `
    -Pattern "differs|identity" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.agentHostIdentity.serviceSid =
    "S-1-5-80-1-2-3-4-5"
$stagedEvidence.rabbitMqTransportCoverage.restartedAgentHostIdentity.serviceSid =
    "S-1-5-80-1-2-3-4-5"
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent service SID not derived from service name rejected" `
    -Pattern "service SID|service name" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.agentHostIdentity | Add-Member userSid "S-1-5-19"
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent legacy identity field rejected" `
    -Pattern "exactly|identity" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.agentHostIdentity.administratorGroupPresent = $true
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent administrator group token presence rejected" `
    -Pattern "service account|identity" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.completionDeliveredOnceAfterReconnect = $false
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent once-only proof reduced" `
    -Pattern "RabbitMQ|once-only|outage|duplicate" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$stagedEvidence = Get-Content -LiteralPath $stagedEvidencePath -Raw | ConvertFrom-Json
$stagedEvidence.rabbitMqTransportCoverage.vendorArtifacts = @(
    $stagedEvidence.rabbitMqTransportCoverage.vendorArtifacts | Select-Object -First 4)
Write-Json -Path $stagedEvidencePath -Value $stagedEvidence
Invoke-ExpectedFailure `
    -Name "staged Agent central artifact set reduced" `
    -Pattern "artifact" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Reset-StagedAgentFixture
$rawEvidencePath = Join-Path $stagedAgentRoot "rabbitmq-process/evidence.json"
Add-Content -LiteralPath $rawEvidencePath -Value " " -Encoding UTF8
Invoke-ExpectedFailure `
    -Name "staged Agent raw evidence hash changed" `
    -Pattern "SHA-256|raw" `
    -Action { & $StagedAgentVerifier -EvidenceRoot $stagedAgentRoot -RequireSanitizedRoot }

Write-Host "Evidence validation mutation tests passed."
exit 0
