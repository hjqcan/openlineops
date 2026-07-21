function Get-FixtureBytesSha256 {
    param([Parameter(Mandatory = $true)][byte[]] $Bytes)

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString(
                $algorithm.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Write-FixtureBytes {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][byte[]] $Bytes
    )

    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [System.IO.File]::WriteAllBytes($Path, $Bytes)
}

function New-FixtureFileReference {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $RelativePath
    )

    $path = Join-Path $Root $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $file = Get-Item -LiteralPath $path
    return [ordered]@{
        relativePath = $RelativePath
        sizeBytes = $file.Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function New-DerLengthBytes {
    param([Parameter(Mandatory = $true)][int] $Length)
    if ($Length -lt 0) { throw "DER length cannot be negative." }
    if ($Length -lt 128) { return ,([byte[]]@([byte]$Length)) }
    $bytes = [System.Collections.Generic.List[byte]]::new()
    $remaining = $Length
    while ($remaining -gt 0) {
        $bytes.Insert(0, [byte]($remaining -band 0xff))
        $remaining = $remaining -shr 8
    }
    return ,([byte[]](@([byte](0x80 -bor $bytes.Count)) + $bytes.ToArray()))
}

function New-DerElementBytes {
    param([Parameter(Mandatory = $true)][byte] $Tag, [Parameter(Mandatory = $true)][byte[]] $Content)
    [byte[]]$length = New-DerLengthBytes $Content.Length
    return ,([byte[]](@($Tag) + $length + $Content))
}

function New-DerIntegerBytes {
    param([Parameter(Mandatory = $true)][byte[]] $UnsignedBigEndian)
    [byte[]]$content = if (($UnsignedBigEndian[0] -band 0x80) -ne 0) {
        [byte[]](@(0) + $UnsignedBigEndian)
    }
    else { $UnsignedBigEndian }
    return ,(New-DerElementBytes 0x02 $content)
}

function Export-FixtureRsaPublicKeyPemBytes {
    param([Parameter(Mandatory = $true)][System.Security.Cryptography.RSA] $Rsa)
    $parameters = $Rsa.ExportParameters($false)
    [byte[]]$modulus = New-DerIntegerBytes $parameters.Modulus
    [byte[]]$exponent = New-DerIntegerBytes $parameters.Exponent
    [byte[]]$rsaPublicKey = New-DerElementBytes 0x30 ([byte[]]($modulus + $exponent))
    [byte[]]$algorithmIdentifier = @(
        0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86,
        0xf7, 0x0d, 0x01, 0x01, 0x01, 0x05, 0x00)
    [byte[]]$bitString = New-DerElementBytes 0x03 ([byte[]](@(0) + $rsaPublicKey))
    [byte[]]$subjectPublicKeyInfo = New-DerElementBytes 0x30 `
        ([byte[]]($algorithmIdentifier + $bitString))
    $base64 = [System.Convert]::ToBase64String($subjectPublicKeyInfo)
    $lines = for ($index = 0; $index -lt $base64.Length; $index += 64) {
        $base64.Substring($index, [System.Math]::Min(64, $base64.Length - $index))
    }
    $pem = "-----BEGIN PUBLIC KEY-----`n$($lines -join "`n")`n-----END PUBLIC KEY-----`n"
    return ,([System.Text.Encoding]::UTF8.GetBytes($pem))
}

function New-FixtureStationPackageEntries {
    param(
        [byte[]] $ReleaseBytes,
        [byte[]] $ConfigurationBytes,
        [byte[]] $OperationSourceBytes,
        [string] $OperationSourcePath,
        [string] $OperationSourceMediaType)
    return @(
        [ordered]@{
            path = "release.json"
            length = $ReleaseBytes.Length
            sha256 = Get-FixtureBytesSha256 $ReleaseBytes
            mediaType = "application/json"
        },
        [ordered]@{
            path = $OperationSourcePath
            length = $OperationSourceBytes.Length
            sha256 = Get-FixtureBytesSha256 $OperationSourceBytes
            mediaType = $OperationSourceMediaType
        },
        [ordered]@{
            path = "source/station-configuration.json"
            length = $ConfigurationBytes.Length
            sha256 = Get-FixtureBytesSha256 $ConfigurationBytes
            mediaType = "application/json"
        })
}

function Write-FixtureStationPackageInt32 {
    param([System.IO.Stream] $Stream, [int] $Value)
    [byte[]]$bytes = [System.BitConverter]::GetBytes($Value)
    if ([System.BitConverter]::IsLittleEndian) {
        [System.Array]::Reverse($bytes)
    }
    $Stream.Write($bytes, 0, $bytes.Length)
}

function Write-FixtureStationPackageInt64 {
    param([System.IO.Stream] $Stream, [long] $Value)
    [byte[]]$bytes = [System.BitConverter]::GetBytes($Value)
    if ([System.BitConverter]::IsLittleEndian) {
        [System.Array]::Reverse($bytes)
    }
    $Stream.Write($bytes, 0, $bytes.Length)
}

function Write-FixtureStationPackageText {
    param([System.IO.Stream] $Stream, [string] $Value)
    $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
    [byte[]]$bytes = $strictUtf8.GetBytes($Value)
    Write-FixtureStationPackageInt32 $Stream $bytes.Length
    $Stream.Write($bytes, 0, $bytes.Length)
}

function Get-FixtureStationPackageContentSha256 {
    param([string] $StationSystemId, [object[]] $Entries)
    [object[]]$orderedEntries = @($Entries)
    $entryComparer = [System.Collections.Generic.Comparer[object]]::Create(
        [System.Comparison[object]]{
            param($left, $right)
            return [System.StringComparer]::Ordinal.Compare(
                [string]$left.path,
                [string]$right.path)
        })
    [System.Array]::Sort($orderedEntries, $entryComparer)
    $stream = [System.IO.MemoryStream]::new()
    try {
        Write-FixtureStationPackageText $stream "openlineops.station-package-content"
        foreach ($value in @(
                "project.fixture",
                "application.fixture",
                "snapshot.fixture",
                "line.fixture",
                $StationSystemId)) {
            Write-FixtureStationPackageText $stream $value
        }
        Write-FixtureStationPackageInt32 $stream $orderedEntries.Count
        foreach ($entry in $orderedEntries) {
            Write-FixtureStationPackageText $stream $entry.path
            Write-FixtureStationPackageInt64 $stream ([long]$entry.length)
            Write-FixtureStationPackageText $stream $entry.sha256
            Write-FixtureStationPackageText $stream $entry.mediaType
        }
        return Get-FixtureBytesSha256 $stream.ToArray()
    }
    finally { $stream.Dispose() }
}

function Get-FixtureDeploymentCatalogName {
    param([string] $StationSystemId)
    $stream = [System.IO.MemoryStream]::new()
    try {
        Write-FixtureStationPackageText $stream `
            "openlineops.station-package-deployment-catalog"
        foreach ($value in @(
                "project.fixture",
                "application.fixture",
                "snapshot.fixture",
                $StationSystemId)) {
            Write-FixtureStationPackageText $stream $value
        }
        return (Get-FixtureBytesSha256 $stream.ToArray()) + ".json"
    }
    finally { $stream.Dispose() }
}

function New-TestStationPackage {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $ContentSha256,
        [Parameter(Mandatory = $true)][string] $StationSystemId,
        [Parameter(Mandatory = $true)][byte[]] $ReleaseBytes,
        [Parameter(Mandatory = $true)][byte[]] $ConfigurationBytes,
        [Parameter(Mandatory = $true)][byte[]] $OperationSourceBytes,
        [Parameter(Mandatory = $true)][string] $OperationSourcePath,
        [Parameter(Mandatory = $true)][string] $OperationSourceMediaType,
        [Parameter(Mandatory = $true)][System.Security.Cryptography.RSA] $SigningRsa,
        [Parameter(Mandatory = $true)][string] $SigningKeyId
    )

    Add-Type -AssemblyName System.IO.Compression
    $entries = New-FixtureStationPackageEntries `
        $ReleaseBytes `
        $ConfigurationBytes `
        $OperationSourceBytes `
        $OperationSourcePath `
        $OperationSourceMediaType
    $manifest = [ordered]@{
        format = "openlineops.station-package"
        packageId = "project.fixture/application.fixture/snapshot.fixture/$StationSystemId"
        projectId = "project.fixture"
        applicationId = "application.fixture"
        projectSnapshotId = "snapshot.fixture"
        productionLineDefinitionId = "line.fixture"
        stationSystemId = $StationSystemId
        contentSha256 = $ContentSha256
        createdAtUtc = "2026-07-15T00:00:00+00:00"
        entries = $entries
    }
    $manifestBytes = [System.Text.Encoding]::UTF8.GetBytes(
        (($manifest | ConvertTo-Json -Depth 10 -Compress) + "`n"))
    $signature = $SigningRsa.SignData(
        $manifestBytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pss)
    $signatureDocument = [ordered]@{
        algorithm = "RSA-PSS-SHA256"
        keyId = $SigningKeyId
        signature = [System.Convert]::ToBase64String($signature)
    }
    $signatureBytes = [System.Text.Encoding]::UTF8.GetBytes(
        ($signatureDocument | ConvertTo-Json -Compress))
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $true)
        try {
            foreach ($item in @(
                    @{ Name = "release.json"; Bytes = $releaseBytes },
                    @{ Name = $OperationSourcePath; Bytes = $OperationSourceBytes },
                    @{ Name = "source/station-configuration.json"; Bytes = $configurationBytes },
                    @{ Name = "package.manifest.json"; Bytes = $manifestBytes },
                    @{ Name = "package.signature.json"; Bytes = $signatureBytes })) {
                $entry = $archive.CreateEntry(
                    $item.Name,
                    [System.IO.Compression.CompressionLevel]::Optimal)
                $entryStream = $entry.Open()
                try {
                    $entryStream.Write($item.Bytes, 0, $item.Bytes.Length)
                }
                finally {
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function New-FixturePublicOperation {
    param(
        [Parameter(Mandatory = $true)][string] $OperationId,
        [Parameter(Mandatory = $true)][int] $Attempt,
        [Parameter(Mandatory = $true)][string] $Judgement
    )

    return [ordered]@{
        operationRunId = "$OperationId@$($Attempt.ToString('0000'))"
        operationId = $OperationId
        attempt = $Attempt
        stationSystemId = "application.fixture.station"
        executionStatus = "Completed"
        judgement = $Judgement
        isTerminal = $true
        startedAtUtc = "2026-07-15T00:01:00Z"
        completedAtUtc = "2026-07-15T00:02:00Z"
        failureCode = $null
        completedStepCount = 1
        commandCount = 1
        incidentCount = 0
        resources = @()
        outputs = @()
    }
}

function New-FixturePublicRun {
    param(
        [Parameter(Mandatory = $true)][string] $ExecutionStatus,
        [Parameter(Mandatory = $true)][string] $Judgement,
        [AllowNull()][string] $Disposition,
        [string] $ControlState = "Active",
        [int] $IncidentCount = 0,
        [object[]] $Operations = @(),
        [object[]] $RouteDecisions = @(),
        [object[]] $RecoveryDecisions = @()
    )

    return [ordered]@{
        productionRunId = "run.fixture"
        productionUnitId = "unit.fixture"
        identity = [ordered]@{
            modelId = "model.fixture"
            inputKey = "vendorMode"
            value = "Passed"
        }
        executionStatus = $ExecutionStatus
        judgement = $Judgement
        disposition = $Disposition
        controlState = $ControlState
        failureCode = $null
        operationCount = @($Operations).Count
        operations = @($Operations)
        routeDecisions = @($RouteDecisions)
        recoveryDecisions = @($RecoveryDecisions)
        incidentCount = $IncidentCount
    }
}

function New-FixturePublicTrace {
    param(
        [Parameter(Mandatory = $true)][string] $ExecutionStatus,
        [Parameter(Mandatory = $true)][string] $Judgement,
        [AllowNull()][string] $Disposition,
        [object[]] $RouteDecisions = @(),
        [object[]] $AuditEntries = @()
    )

    return [ordered]@{
        traceRecordId = "trace.fixture"
        productionRunId = "run.fixture"
        executionStatus = $ExecutionStatus
        judgement = $Judgement
        disposition = $Disposition
        failureCode = $null
        operations = @()
        routeDecisions = @($RouteDecisions)
        genealogyCount = 0
        materialLocationTransitions = @()
        slotOccupancyTransitions = @()
        dispositionTransitions = @()
        auditEntries = @($AuditEntries)
    }
}

function Write-ProductionClosureEvidenceFixture {
    param(
        [Parameter(Mandatory = $true)][string] $RunRoot,
        [string] $StationOperationSourcePath = "source/operation.py",
        [string] $StationOperationSourceMediaType = "text/x-python",
        [string] $StationOperationSourceText = "print('fixture operation')`n",
        [AllowNull()][byte[]] $StationOperationSourceBytes = $null)

    New-Item -ItemType Directory -Path $RunRoot -Force | Out-Null
    $screenshotNames = @(
        "studio-saved-route-authoring",
        "scenario-concurrent-two-stations",
        "scenario-concurrent-topology-2d",
        "scenario-concurrent-topology-3d",
        "scenario-vendor-passed-trace",
        "scenario-vendor-failed-rework-trace",
        "scenario-cancel-spawn-child-running",
        "scenario-cancel-spawn-child-trace",
        "scenario-vendor-crash-incident-trace",
        "scenario-recovery-required-no-replay",
        "scenario-recovery-reconciled-trace",
        "restart-persisted-line-projection",
        "restart-persisted-trace")
    foreach ($name in $screenshotNames) {
        Write-FixtureBytes `
            -Path (Join-Path $RunRoot "screenshots/$name.png") `
            -Bytes ([System.Text.Encoding]::UTF8.GetBytes("fixture-png-$name"))
    }
    Write-FixtureBytes `
        -Path (Join-Path $RunRoot "verified-trace-artifact-saves/measurements.csv") `
        -Bytes ([System.Text.Encoding]::UTF8.GetBytes("voltage,passed`n3.3,true`n"))
    $frozenManifest = [ordered]@{
        schema = "openlineops.project-release-artifact"
        schemaVersion = 1
        snapshotId = "snapshot.fixture"
        projectId = "project.fixture"
        applicationId = "application.fixture"
        publishedAtUtc = "2026-07-15T00:00:00+00:00"
        sourceApplicationRelativePath = "applications/application.fixture"
        applicationProjectRelativePath = "applications/application.fixture/application.fixture.oloapp"
        metadata = [ordered]@{
            topologyId = "topology.fixture"
            layoutIds = @()
            productionLine = [ordered]@{ lineDefinitionId = "line.fixture" }
            externalProgramResources = @()
            capabilityBindings = @()
            targetReferences = @()
            blockVersionIds = @()
            packageDependencies = @()
        }
        files = @()
        contentSha256 = "5" * 64
    }
    $frozenManifestBytes = [System.Text.Encoding]::UTF8.GetBytes(
        (($frozenManifest | ConvertTo-Json -Depth 15) + "`n"))
    Write-FixtureBytes `
        -Path (Join-Path $RunRoot "public-release/frozen-manifest.json") `
        -Bytes $frozenManifestBytes
    $fixtureSigningRsa = [System.Security.Cryptography.RSACng]::new(3072)
    [byte[]]$fixturePublicKeyBytes = Export-FixtureRsaPublicKeyPemBytes $fixtureSigningRsa
    $fixturePublicKeySha256 = Get-FixtureBytesSha256 $fixturePublicKeyBytes
    $fixtureSigningKeyId = "studio-" + $fixturePublicKeySha256.Substring(0, 24)
    Write-FixtureBytes `
        -Path (Join-Path $RunRoot "public-release/release-signing-public.pem") `
        -Bytes $fixturePublicKeyBytes

    $fixtureConfigurationBytes = [System.Text.Encoding]::UTF8.GetBytes(
        '{"schema":"openlineops.fixture-station-configuration","value":"safe"}')
    $fixtureOperationSourceBytes = if ($null -ne $StationOperationSourceBytes) {
        $StationOperationSourceBytes
    }
    else {
        [System.Text.Encoding]::UTF8.GetBytes($StationOperationSourceText)
    }
    $stationDefinitions = @(
        [ordered]@{
            stationSystemId = "application.fixture.station.preparation"
        },
        [ordered]@{
            stationSystemId = "application.fixture.station.vendor-test"
        })
    foreach ($station in $stationDefinitions) {
        $entries = New-FixtureStationPackageEntries `
            $frozenManifestBytes `
            $fixtureConfigurationBytes `
            $fixtureOperationSourceBytes `
            $StationOperationSourcePath `
            $StationOperationSourceMediaType
        $station["contentSha256"] = Get-FixtureStationPackageContentSha256 `
            $station.stationSystemId `
            $entries
        $station["catalogName"] = Get-FixtureDeploymentCatalogName $station.stationSystemId
    }
    $packageReferences = @()
    $catalogReferences = @()
    foreach ($station in $stationDefinitions) {
        $packageRelativePath = "public-release/station-packages/$($station.contentSha256).olopkg"
        New-TestStationPackage `
            -Path (Join-Path $RunRoot $packageRelativePath) `
            -ContentSha256 $station.contentSha256 `
            -StationSystemId $station.stationSystemId `
            -ReleaseBytes $frozenManifestBytes `
            -ConfigurationBytes $fixtureConfigurationBytes `
            -OperationSourceBytes $fixtureOperationSourceBytes `
            -OperationSourcePath $StationOperationSourcePath `
            -OperationSourceMediaType $StationOperationSourceMediaType `
            -SigningRsa $fixtureSigningRsa `
            -SigningKeyId $fixtureSigningKeyId
        $packageFileReference = New-FixtureFileReference $RunRoot $packageRelativePath
        $packageReferences += [ordered]@{
            stationSystemId = $station.stationSystemId
            packageContentSha256 = $station.contentSha256
            relativePath = $packageFileReference.relativePath
            sizeBytes = $packageFileReference.sizeBytes
            sha256 = $packageFileReference.sha256
        }

        $catalogRelativePath = "public-release/deployment-catalog/$($station.catalogName)"
        $catalog = [ordered]@{
            schema = "openlineops.station-package-deployment"
            projectId = "project.fixture"
            applicationId = "application.fixture"
            projectSnapshotId = "snapshot.fixture"
            productionLineDefinitionId = "line.fixture"
            stationSystemId = $station.stationSystemId
            packageContentSha256 = $station.contentSha256
            publishedAtUtc = "2026-07-15T00:00:00+00:00"
        }
        $catalogBytes = [System.Text.Encoding]::UTF8.GetBytes(
            (($catalog | ConvertTo-Json -Compress) + "`n"))
        Write-FixtureBytes `
            -Path (Join-Path $RunRoot $catalogRelativePath) `
            -Bytes $catalogBytes
        $catalogFileReference = New-FixtureFileReference $RunRoot $catalogRelativePath
        $catalogReferences += [ordered]@{
            stationSystemId = $station.stationSystemId
            packageContentSha256 = $station.contentSha256
            relativePath = $catalogFileReference.relativePath
            sizeBytes = $catalogFileReference.sizeBytes
            sha256 = $catalogFileReference.sha256
        }
    }
    $fixtureSigningRsa.Dispose()

    $screenshotReference = New-FixtureFileReference `
        $RunRoot `
        "screenshots/studio-saved-route-authoring.png"
    $screenshot = [ordered]@{
        name = "studio-saved-route-authoring"
        path = $screenshotReference.relativePath
        sha256 = $screenshotReference.sha256
        sizeBytes = $screenshotReference.sizeBytes
    }
    $savedArtifactReference = New-FixtureFileReference `
        $RunRoot `
        "verified-trace-artifact-saves/measurements.csv"
    $artifactBytes = [System.Text.Encoding]::UTF8.GetBytes("artifact")
    $artifactSha256 = Get-FixtureBytesSha256 $artifactBytes
    $requiredArtifacts = @(
        "measurements.csv",
        "inspection.png",
        "report.pdf",
        "stdout.log",
        "stderr.log")
    $artifacts = @($requiredArtifacts | ForEach-Object {
            [ordered]@{
                name = $_
                kind = "Binary"
                storageKeySha256 = $artifactSha256
                mediaType = "application/octet-stream"
                sizeBytes = $artifactBytes.Length
                sha256 = $artifactSha256
            }
        })
    $downloads = @($requiredArtifacts | ForEach-Object {
            [ordered]@{
                name = $_
                kind = $null
                storageKeySha256 = $artifactSha256
                mediaType = "application/octet-stream"
                sizeBytes = $artifactBytes.Length
                sha256 = $artifactSha256
            }
        })
    $savedArtifact = [ordered]@{
        name = "measurements.csv"
        storageKeySha256 = $artifactSha256
        path = $savedArtifactReference.relativePath
        sizeBytes = $savedArtifactReference.sizeBytes
        sha256 = $savedArtifactReference.sha256
        invokedThroughPreloadIpc = $true
        atomicTemporaryFileRemoved = $true
    }
    $passedRoute = [ordered]@{
        sourceOperationRunId = "operation.vendor-test@0001"
        transitionId = "route.vendor-default-terminal"
        targetOperationId = $null
        terminalDisposition = "Completed"
        sourceJudgement = "Passed"
        traversal = 1
        decidedAtUtc = "2026-07-15T00:03:00Z"
    }
    $passedRun = New-FixturePublicRun `
        -ExecutionStatus "Completed" `
        -Judgement "Passed" `
        -Disposition "Completed" `
        -RouteDecisions @($passedRoute)
    $passedTrace = New-FixturePublicTrace `
        -ExecutionStatus "Completed" `
        -Judgement "Passed" `
        -Disposition "Completed" `
        -RouteDecisions @($passedRoute)
    $failedOperations = @(
        (New-FixturePublicOperation "operation.vendor-test" 1 "Failed"),
        (New-FixturePublicOperation "operation.preparation" 2 "Passed"),
        (New-FixturePublicOperation "operation.vendor-test" 2 "Failed"))
    $failedRoutes = @(
        [ordered]@{
            sourceOperationRunId = "operation.vendor-test@0001"
            transitionId = "route.vendor-failed-rework"
            targetOperationId = "operation.preparation"
            terminalDisposition = $null
            sourceJudgement = "Failed"
            traversal = 1
            decidedAtUtc = "2026-07-15T00:03:00Z"
        },
        [ordered]@{
            sourceOperationRunId = "operation.vendor-test@0002"
            transitionId = "route.vendor-failed-terminal"
            targetOperationId = $null
            terminalDisposition = "Nonconforming"
            sourceJudgement = "Failed"
            traversal = 1
            decidedAtUtc = "2026-07-15T00:04:00Z"
        })
    $failedRun = New-FixturePublicRun `
        -ExecutionStatus "Completed" `
        -Judgement "Failed" `
        -Disposition "Nonconforming" `
        -Operations $failedOperations `
        -RouteDecisions $failedRoutes
    $summary = [ordered]@{
        schema = "openlineops.production-closure-e2e"
        status = "passed"
        startedAtUtc = "2026-07-15T00:00:00Z"
        completedAtUtc = "2026-07-15T00:10:00Z"
        packagedExecutable = "packaged-desktop/OpenLineOps.exe"
        artifactRoot = "."
        projectPath = "private-runtime/project"
        projectId = "project.fixture"
        applicationId = "application.fixture"
        topologyId = "topology.fixture"
        productionLineDefinitionId = "line.fixture"
        projectSnapshotId = "snapshot.fixture"
        applicationPortability = [ordered]@{
            status = "passed"
            sourceProjectId = "project.source.fixture"
            targetProjectId = "project.fixture"
            applicationId = "application.fixture"
            fileCount = 24
            totalSizeBytes = 65536
            sourceBeforeCopyTreeSha256 = "6" * 64
            copiedTreeSha256 = "6" * 64
            afterImportTreeSha256 = "6" * 64
            afterPublishTreeSha256 = "6" * 64
            afterExecutionTreeSha256 = "6" * 64
            sourceAfterExecutionTreeSha256 = "6" * 64
            unchanged = $true
        }
        diagnostics = $null
        failure = $null
        packagedBinaries = [ordered]@{
            before = [ordered]@{
                desktopExecutable = [ordered]@{ path = "packaged-desktop/OpenLineOps.exe"; sha256 = "3" * 64; sizeBytes = 100; modifiedAtUtc = "2026-07-15T00:00:00Z" }
                runtimeApiExecutable = [ordered]@{ path = "packaged-desktop/runtime/api/OpenLineOps.Api.exe"; sha256 = "4" * 64; sizeBytes = 100; modifiedAtUtc = "2026-07-15T00:00:00Z" }
            }
            after = [ordered]@{
                desktopExecutable = [ordered]@{ path = "packaged-desktop/OpenLineOps.exe"; sha256 = "3" * 64; sizeBytes = 100; modifiedAtUtc = "2026-07-15T00:00:00Z" }
                runtimeApiExecutable = [ordered]@{ path = "packaged-desktop/runtime/api/OpenLineOps.Api.exe"; sha256 = "4" * 64; sizeBytes = 100; modifiedAtUtc = "2026-07-15T00:00:00Z" }
            }
            unchangedDuringRun = $true
        }
        frozenRelease = [ordered]@{
            releaseManifest = New-FixtureFileReference $RunRoot "public-release/frozen-manifest.json"
            projectRelativeReleaseManifestPath = ".openlineops/releases/snapshot.fixture/release.json"
            releaseContentSha256 = "5" * 64
            manifestSchema = "openlineops.project-release-artifact"
            externalPrograms = @()
            signingPublicKey = New-FixtureFileReference $RunRoot "public-release/release-signing-public.pem"
            stationPackages = $packageReferences
            deploymentCatalogs = $catalogReferences
            entryStationDeployment = [ordered]@{
                stationSystemId = "application.fixture.station.preparation"
                stationId = "application.fixture.station.preparation"
                packageContentSha256 = $stationDefinitions[0].contentSha256
            }
        }
        externalProgramTrial = [ordered]@{
            status = "passed"
            executionStatus = "Completed"
            judgement = "Passed"
            artifactCount = 1
            directoryImport = [ordered]@{
                entryPoint = "files/bin/OpenLineOps.VendorTestHelper.exe"
                files = @(
                    "files/bin/OpenLineOps.VendorTestHelper.exe",
                    "files/config/shared.settings.json",
                    "files/lib/shared.settings.json")
                preservedSameBasenames = @(
                    "files/config/shared.settings.json",
                    "files/lib/shared.settings.json")
            }
        }
        studioAuthoring = [ordered]@{
            status = "passed"
            productModelId = "model.fixture"
            operationCount = 2
            terminalCount = 2
            terminalDispositions = @("Completed", "Nonconforming")
            transitionCount = 4
            publishEnabled = $true
            screenshot = $screenshot
        }
        scenarios = [ordered]@{
            concurrentPipeline = [ordered]@{
                status = "passed"
                unitA = [ordered]@{ unitId = "unit-a"; runId = "run-a"; identityValue = "Delay"; runSubmitted = $true }
                unitB = [ordered]@{ unitId = "unit-b"; runId = "run-b"; identityValue = "Passed"; runSubmitted = $true }
                observedAtUtc = "2026-07-15T00:01:00Z"
                assertion = "Two products overlapped."
                lineState = [ordered]@{
                    productionLineDefinitionId = "line.fixture"
                    generatedAtUtc = "2026-07-15T00:03:00Z"
                    activeRunCount = 2
                    activeRuns = @()
                    stations = @(
                        [ordered]@{
                            stationSystemId = "station.one"; status = "Running"; stationId = "station.one"
                            presenceState = $null; presenceHealth = $null; queueCount = 0
                            activeOperations = @([ordered]@{
                                productionRunId = "run-b"; productionUnitId = "unit-b"
                                operationRunId = "operation.preparation@0001"; operationId = "operation.preparation"
                                executionStatus = "Running"; judgement = "Unknown"; resources = @()
                            })
                        },
                        [ordered]@{
                            stationSystemId = "station.two"; status = "Running"; stationId = "station.two"
                            presenceState = $null; presenceHealth = $null; queueCount = 0
                            activeOperations = @([ordered]@{
                                productionRunId = "run-a"; productionUnitId = "unit-a"
                                operationRunId = "operation.vendor-test@0001"; operationId = "operation.vendor-test"
                                executionStatus = "Running"; judgement = "Unknown"; resources = @()
                            })
                        })
                    slots = @(
                        [ordered]@{
                            stationSystemId = "station.one"; slotId = "slot.one"; status = "Running"
                            materialKind = "ProductionUnit"; materialId = "unit-b"; lastTransitionAtUtc = "2026-07-15T00:03:00Z"
                        },
                        [ordered]@{
                            stationSystemId = "station.two"; slotId = "slot.two"; status = "Running"
                            materialKind = "ProductionUnit"; materialId = "unit-a"; lastTransitionAtUtc = "2026-07-15T00:03:00Z"
                        })
                    carrierCount = 0
                }
                screenshots = @()
            }
            vendorPassed = [ordered]@{
                status = "passed"
                run = $passedRun
                trace = $passedTrace
                immutableRunTrace = [ordered]@{
                    before = [ordered]@{ sha256 = "6" * 64; sizeBytes = 512 }
                    after = [ordered]@{ sha256 = "6" * 64; sizeBytes = 512 }
                    unchanged = $true
                    terminalCompletedAtUtc = "2026-07-15T00:04:00Z"
                    unloadAtUtc = "2026-07-15T00:04:01Z"
                }
                materialLifecycle = [ordered]@{
                    productionUnitId = "unit.fixture"
                    currentDisposition = "Completed"
                    currentLocation = $null
                    currentCarrierLocation = $null
                    registeredAtUtc = "2026-07-15T00:00:00Z"
                    observedThroughUtc = "2026-07-15T00:04:01Z"
                    genealogyCount = 0
                    materialLocationTransitions = @()
                    slotOccupancyTransitions = @()
                    dispositionTransitions = @()
                }
                artifacts = $artifacts
                artifactDownloads = $downloads
                verifiedSaveActionCount = 5
                verifiedArtifactSave = $savedArtifact
                screenshots = @()
            }
            vendorFailedRework = [ordered]@{
                status = "passed"
                unit = [ordered]@{ unitId = "unit-f"; runId = "run-f"; identityValue = "Failed"; runSubmitted = $true }
                run = $failedRun
                trace = New-FixturePublicTrace -ExecutionStatus "Completed" -Judgement "Failed" -Disposition "Nonconforming" -RouteDecisions $failedRoutes
                assertion = "Failed remained a product judgement."
                screenshots = @()
            }
            operatorCancel = [ordered]@{
                status = "passed"
                unit = [ordered]@{ unitId = "unit-c"; runId = "run-c"; identityValue = "SpawnChildDelay"; runSubmitted = $true }
                run = New-FixturePublicRun -ExecutionStatus "Canceled" -Judgement "Aborted" -Disposition "Held"
                trace = New-FixturePublicTrace -ExecutionStatus "Canceled" -Judgement "Aborted" -Disposition "Held"
                processTreeTerminated = $true
                vendorProcessesBeforeCancel = @(
                    [ordered]@{ processId = 101; parentProcessId = 1; imageName = "OpenLineOps.VendorTestHelper.exe" },
                    [ordered]@{ processId = 102; parentProcessId = 101; imageName = "dotnet.exe" })
                screenshots = @()
            }
            vendorCrash = [ordered]@{
                status = "passed"
                unit = [ordered]@{ unitId = "unit-x"; runId = "run-x"; identityValue = "Crash"; runSubmitted = $true }
                run = New-FixturePublicRun -ExecutionStatus "Failed" -Judgement "Unknown" -Disposition "Held" -IncidentCount 1
                trace = New-FixturePublicTrace -ExecutionStatus "Failed" -Judgement "Unknown" -Disposition "Held"
                incidents = @([ordered]@{
                    runtimeIncidentId = "incident.fixture"
                    severity = "Error"
                    code = "Vendor.Crash"
                    occurredAtUtc = "2026-07-15T00:05:00Z"
                })
                screenshots = @()
            }
            recovery = [ordered]@{
                status = "passed"
                unit = [ordered]@{ unitId = "unit-r"; runId = "run-r"; identityValue = "SpawnChildDelayRecovery"; runSubmitted = $true }
                interruptedOperationRunId = "operation.vendor-test@0001"
                backendPidTerminated = 201
                vendorProcessesBeforeCrash = @(
                    [ordered]@{ processId = 201; parentProcessId = 1; imageName = "OpenLineOps.VendorTestHelper.exe" },
                    [ordered]@{ processId = 202; parentProcessId = 201; imageName = "dotnet.exe" })
                noAutomaticReplay = $true
                recoveryRequired = New-FixturePublicRun `
                    -ExecutionStatus "Running" `
                    -Judgement "Unknown" `
                    -Disposition "InProcess" `
                    -ControlState "RecoveryRequired" `
                    -Operations @((New-FixturePublicOperation "operation.vendor-test" 1 "Unknown"))
                terminal = New-FixturePublicRun `
                    -ExecutionStatus "Completed" `
                    -Judgement "Passed" `
                    -Disposition "Completed" `
                    -Operations @((New-FixturePublicOperation "operation.vendor-test" 1 "Passed"))
                recoveryDecisions = @([ordered]@{
                    decisionId = "decision.fixture"; kind = "Reconcile"
                    operationRunId = "operation.vendor-test@0001"; operationId = "operation.vendor-test"
                    observedJudgement = "Passed"; observedOutputCount = 1; decidedAtUtc = "2026-07-15T00:08:00Z"
                })
                trace = New-FixturePublicTrace `
                    -ExecutionStatus "Completed" `
                    -Judgement "Passed" `
                    -Disposition "Completed" `
                    -AuditEntries @([ordered]@{
                        auditEntryId = "audit.fixture"
                        action = "ProductionRun.Recovery.Reconcile"
                        occurredAtUtc = "2026-07-15T00:08:00Z"
                    })
                screenshots = @()
            }
        }
        restart = [ordered]@{
            status = "passed"
            previousCdpPort = 9222
            traceCountBefore = 6
            traceCountAfter = 6
            activeRunCount = 0
            rebuiltProjection = [ordered]@{
                productionLineDefinitionId = "line.fixture"
                generatedAtUtc = "2026-07-15T00:10:00Z"
                activeRunCount = 0
                activeRuns = @()
                stations = @()
                slots = @()
                carrierCount = 0
            }
            screenshots = @()
        }
    }
    Write-Json -Path (Join-Path $RunRoot "summary.json") -Value $summary

    $manifestEntryByPath = [System.Collections.Generic.Dictionary[string,object]]::new(
        [System.StringComparer]::Ordinal)
    $allFiles = @(Get-ChildItem -LiteralPath $RunRoot -File -Recurse | Sort-Object FullName)
    $resolvedRunRoot = [System.IO.Path]::GetFullPath($RunRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $runRootPrefix = $resolvedRunRoot + [System.IO.Path]::DirectorySeparatorChar
    foreach ($file in $allFiles) {
        $relativePath = $file.FullName.Substring($runRootPrefix.Length).Replace('\', '/')
        $manifestEntryByPath.Add($relativePath, [ordered]@{
            relativePath = $relativePath
            sizeBytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    $sortedManifestPaths = [string[]]@($manifestEntryByPath.Keys)
    [System.Array]::Sort($sortedManifestPaths, [System.StringComparer]::Ordinal)
    $manifestEntries = @($sortedManifestPaths | ForEach-Object { $manifestEntryByPath[$_] })
    Write-Json `
        -Path (Join-Path $RunRoot "evidence-manifest.json") `
        -Value ([ordered]@{
            schema = "openlineops.production-closure-evidence-manifest"
            schemaVersion = 1
            generatedAtUtc = "2026-07-15T00:11:00Z"
            files = $manifestEntries
        })
}

function Write-StagedAgentExactTestTrxFixture {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $FullyQualifiedName,
        [Parameter(Mandatory = $true)][string] $TestId
    )

    [System.IO.Directory]::CreateDirectory(
        [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($Path))) | Out-Null
    $lastSeparator = $FullyQualifiedName.LastIndexOf('.')
    $className = $FullyQualifiedName.Substring(0, $lastSeparator)
    $methodName = $FullyQualifiedName.Substring($lastSeparator + 1)
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.NewLineChars = "`n"
    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement(
            "TestRun",
            "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
        $writer.WriteAttributeString("name", "staged-agent-bundle-e2e")
        $writer.WriteAttributeString("runUser", "redacted")
        $writer.WriteStartElement("Results")
        $writer.WriteStartElement("UnitTestResult")
        $writer.WriteAttributeString("testId", $TestId)
        $writer.WriteAttributeString("testName", $FullyQualifiedName)
        $writer.WriteAttributeString("computerName", "redacted")
        $writer.WriteAttributeString("outcome", "Passed")
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteStartElement("TestDefinitions")
        $writer.WriteStartElement("UnitTest")
        $writer.WriteAttributeString("name", $FullyQualifiedName)
        $writer.WriteAttributeString("storage", "OpenLineOps.Agent.Tests.dll")
        $writer.WriteAttributeString("id", $TestId)
        $writer.WriteStartElement("TestMethod")
        $writer.WriteAttributeString("codeBase", "OpenLineOps.Agent.Tests.dll")
        $writer.WriteAttributeString("className", $className)
        $writer.WriteAttributeString("name", $methodName)
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteStartElement("ResultSummary")
        $writer.WriteAttributeString("outcome", "Completed")
        $writer.WriteStartElement("Counters")
        $writer.WriteAttributeString("total", "1")
        $writer.WriteAttributeString("executed", "1")
        $writer.WriteAttributeString("passed", "1")
        $writer.WriteAttributeString("failed", "0")
        $writer.WriteAttributeString("notExecuted", "0")
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    }
    finally {
        $writer.Dispose()
    }
}

function Write-StagedAgentEvidenceFixture {
    param([Parameter(Mandatory = $true)][string] $Root)

    New-Item -ItemType Directory -Path $Root -Force | Out-Null
    $testNames = @(
        "OpenLineOps.Agent.Tests.SignedVendorProgramStationE2ETests.SignedFrozenPluginRunsThroughAgentStationRuntimeAndBundledHost",
        "OpenLineOps.Agent.Tests.SignedVendorProgramStationE2ETests.SignedFrozenPythonFlowRunsThroughAgentStationRuntimeAndWorker",
        "OpenLineOps.Agent.Tests.LeastPrivilegeLauncherContractTests.ConcurrentWorkersUseDistinctAppContainersAndKillDescendants",
        "OpenLineOps.Agent.Tests.LeastPrivilegeLauncherContractTests.StaleAppContainerProfileIsRecoveredBeforeNextLaunch",
        "OpenLineOps.Agent.Tests.LeastPrivilegeLauncherContractTests.ProvisioningCommandGrantsRuntimeCapabilityRecursively")
    $testResultNames = @(
        "signed-frozen-plugin",
        "signed-frozen-python",
        "staged-launcher-isolation",
        "staged-launcher-crash-recovery",
        "staged-python-runtime-provisioning")
    $tests = @()
    for ($index = 0; $index -lt $testNames.Count; $index++) {
        $relativePath = "test-results/$($testResultNames[$index]).trx"
        $testId = "00000000-0000-0000-0000-{0:D12}" -f ($index + 1)
        Write-StagedAgentExactTestTrxFixture `
            -Path (Join-Path $Root $relativePath) `
            -FullyQualifiedName $testNames[$index] `
            -TestId $testId
        $tests += [ordered]@{
            fullyQualifiedName = $testNames[$index]
            result = "passed"
            trxRelativePath = $relativePath
            trxSha256 = Get-FileSha256 (Join-Path $Root $relativePath)
        }
    }
    $identity = [ordered]@{
        nonAdministrative = $true
        isPrimaryToken = $true
        isElevated = $false
        isRestrictedToken = $true
        administratorGroupPresent = $false
        administratorGroupEnabled = $false
        administratorGroupDenyOnly = $false
        serviceLogonSidPresent = $true
        serviceLogonSidEnabled = $true
        exactServiceSidPresent = $true
        exactServiceSidEnabled = $true
        exactServiceSidRestricted = $true
        isAuthenticated = $true
        isSystem = $false
        identityStrategy = "local-service-restricted-service-sid"
        serviceAccountName = "NT AUTHORITY\LocalService"
        serviceAccountSid = "S-1-5-19"
        serviceSid = "S-1-5-80-1318403588-2035430339-3331843247-126381569-1994642067"
    }
    $artifactDefinitions = [ordered]@{
        "measurements.csv" = "Csv"
        "inspection.png" = "Image"
        "report.pdf" = "Report"
        "stdout.log" = "Log"
        "stderr.log" = "Log"
    }
    $artifacts = @()
    $artifactIndex = 0
    foreach ($name in $artifactDefinitions.Keys) {
        $sha = $artifactIndex.ToString("x2") + ("a" * 62)
        $receipt = $artifactIndex.ToString("x2") + ("b" * 62)
        $artifacts += [ordered]@{
            Name = $name
            Kind = $artifactDefinitions[$name]
            StorageKey = "station-artifacts/fixture/$name"
            ReceiptId = $receipt
            SizeBytes = 10
            Sha256 = $sha
        }
        $artifactIndex++
    }
    $presence = [ordered]@{
        persistedStates = @("Started", "Heartbeat")
        startedAndHeartbeatPersisted = $true
        expiredOfflineDuringBrokerOutage = $true
        offlineDuringBrokerOutage = [ordered]@{ status = "Offline"; health = "Expired" }
        freshOnlineAfterReconnect = $true
        onlineAfterReconnect = [ordered]@{ status = "Idle"; health = "Online" }
    }
    $materialArrivalIpc = [ordered]@{
        serviceTokenConnected = $true
        pipeExactAclVerified = $true
        durablePublicationVerified = $true
        ordinaryCiTokenExplicitAccessDenied = $true
    }
    $immutableContentCache = [ordered]@{
        packagedProvisionCommandVerified = $true
        runningServiceAdministrationRejected = $true
        serviceTokenReadExecuteVerified = $true
        sealedMutationAccessDenied = $true
        deepAncestorMutationAccessDenied = $true
        preSealRecoveryVerified = $true
        cleanupCrashResumeVerified = $true
        committedAdminRemovalVerified = $true
        packagedRemovalCommandVerified = $true
        cacheNamespaceRemoved = $true
    }
    $raw = [ordered]@{
        schema = "openlineops.staged-agent-rabbitmq-e2e-evidence"
        schemaVersion = 1
        broker = [ordered]@{ Host = "localhost"; Port = 5671; tls = $true }
        executionStatus = "Completed"
        judgement = "Passed"
        vendorProgram = "OpenLineOps.VendorTestHelper.exe"
        centralArtifactTransport = "authenticated-http-stream"
        operatorTraceGetVerified = $true
        brokerOutageVerified = $true
        coordinatorTransportResultInboxRestartedAfterBrokerRecovery = $true
        offlinePendingOutboxCount = 2
        offlineCompletionWasNotDelivered = $true
        completionDeliveredOnceAfterReconnect = $true
        duplicateRedeliveryRejected = $true
        duplicateAfterRestartRejected = $true
        runtimeFinishedExecutionCount = 1
        firstAgentPid = 301
        restartedAgentPid = 302
        packageContentSha256 = "c" * 64
        AgentId = "agent.fixture"
        StationId = "station.fixture"
        windowsServiceName = "OpenLineOpsAgentE2E-0123456789abcdef0123456789abcdef"
        windowsServiceLifecycleVerified = $true
        vendorArtifacts = $artifacts
        agentHostIdentity = $identity
        restartedAgentHostIdentity = $identity
        materialArrivalIpc = $materialArrivalIpc
        immutableContentCache = $immutableContentCache
        presence = $presence
        cleanShutdownVerified = $true
    }
    $rawPath = Join-Path $Root "rabbitmq-process/evidence.json"
    Write-Json -Path $rawPath -Value $raw
    $rabbit = [ordered]@{
        status = "passed"
        executionStatus = $raw.executionStatus
        judgement = $raw.judgement
        vendorProgram = $raw.vendorProgram
        centralArtifactTransport = $raw.centralArtifactTransport
        operatorTraceGetVerified = $raw.operatorTraceGetVerified
        vendorArtifacts = $raw.vendorArtifacts
        brokerOutageVerified = $raw.brokerOutageVerified
        coordinatorTransportResultInboxRestartedAfterBrokerRecovery = $raw.coordinatorTransportResultInboxRestartedAfterBrokerRecovery
        agentHostIdentity = $identity
        restartedAgentHostIdentity = $identity
        materialArrivalIpc = $materialArrivalIpc
        immutableContentCache = $immutableContentCache
        agentId = $raw.AgentId
        stationId = $raw.StationId
        windowsServiceName = $raw.windowsServiceName
        windowsServiceLifecycleVerified = $raw.windowsServiceLifecycleVerified
        packageContentSha256 = $raw.packageContentSha256
        firstAgentPid = $raw.firstAgentPid
        restartedAgentPid = $raw.restartedAgentPid
        runtimeFinishedExecutionCount = $raw.runtimeFinishedExecutionCount
        eventKinds = @("StationJobAccepted", "StationJobProgressed")
        progressPhases = @("runtime-finished")
        offlinePendingOutboxCount = $raw.offlinePendingOutboxCount
        offlineCompletionWasNotDelivered = $raw.offlineCompletionWasNotDelivered
        completionDeliveredOnceAfterReconnect = $raw.completionDeliveredOnceAfterReconnect
        duplicateRedeliveryRejected = $raw.duplicateRedeliveryRejected
        duplicateAfterRestartRejected = $raw.duplicateAfterRestartRejected
        outageControlMode = "windows-service"
        presence = $presence
        cleanShutdownVerified = $true
        evidence = "rabbitmq-process/evidence.json"
        evidenceSha256 = Get-FileSha256 $rawPath
    }
    $appContainerSid = "S-1-15-2-1-2-3-4-5-6-7"
    $evidence = [ordered]@{
        schemaVersion = 1
        product = "OpenLineOps"
        generatedAtUtc = "2026-07-15T00:00:00Z"
        releaseVersion = "0.0.0-fixture"
        agentArtifact = [ordered]@{ relativePath = "agent.zip"; sha256 = "d" * 64 }
        samplePluginArtifact = [ordered]@{ relativePath = "plugin.zip"; sha256 = "e" * 64 }
        entryPoints = @(
            [ordered]@{ role = "station-agent-service"; relativePath = "OpenLineOps.Agent.exe"; sha256 = "1" * 64 },
            [ordered]@{ role = "station-runtime"; relativePath = "OpenLineOps.StationRuntime.exe"; sha256 = "2" * 64 },
            [ordered]@{ role = "plugin-host"; relativePath = "OpenLineOps.PluginHost.exe"; sha256 = "3" * 64 },
            [ordered]@{ role = "python-script-worker"; relativePath = "OpenLineOps.ScriptWorker.exe"; sha256 = "4" * 64 })
        entryPointProbes = @(
            [ordered]@{
                name = "station-agent-service"
                executable = "OpenLineOps.Agent.exe"
                executableSha256 = "1" * 64
                exitCode = 1
                outputContract = '^OpenLineOps Station Agent terminated: OpenLineOps:WindowsServiceName must contain 1-80 ASCII letters, digits, periods, underscores, or hyphens\.$'
                status = "passed"
            },
            [ordered]@{
                name = "station-runtime"
                executable = "OpenLineOps.StationRuntime.exe"
                executableSha256 = "2" * 64
                exitCode = 64
                outputContract = "execute-operation --request-file"
                status = "passed"
            },
            [ordered]@{
                name = "plugin-host"
                executable = "OpenLineOps.PluginHost.exe"
                executableSha256 = "3" * 64
                exitCode = 2
                outputContract = "--manifest is required"
                status = "passed"
            },
            [ordered]@{
                name = "python-script-worker"
                executable = "OpenLineOps.ScriptWorker.exe"
                executableSha256 = "4" * 64
                exitCode = 2
                outputContract = "Python script worker request body is required"
                status = "passed"
            })
        exactTestEvidence = $tests
        signedPackageProcessChains = @(
            [ordered]@{
                name = "signed-frozen-plugin"
                path = "Agent application layer -> staged StationRuntime -> staged PluginHost"
                packageFormat = ".olopkg"
                exactTest = $tests[0]
                status = "passed"
            },
            [ordered]@{
                name = "signed-frozen-python"
                path = "Agent application layer -> staged StationRuntime -> staged ScriptWorker"
                packageFormat = ".olopkg"
                executionPolicy = "Required PerExecutionAppContainer"
                status = "passed"
                tokenIsAppContainer = $true
                appContainerSid = $appContainerSid
                integrityRid = 4096
                exactTest = $tests[1]
            })
        productionPythonPolicy = [ordered]@{
            templateVerified = $true
            requireLeastPrivilegeExecution = $true
            isolationMode = "LeastPrivilegeIdentity"
            identity = "PerExecutionAppContainer"
            launcher = "OpenLineOps.LeastPrivilegeLauncher.exe"
            noInteractivePrompt = $true
            childTokenIsAppContainer = $true
            childAppContainerSid = $appContainerSid
            childIntegrityRid = 4096
            stagedIsolationTest = $tests[2]
            stagedCrashRecoveryTest = $tests[3]
            stagedPythonRuntimeProvisioningTest = $tests[4]
            stagedExecutionVerified = $true
        }
        rabbitMqTransportCoverage = $rabbit
        status = "passed"
    }
    $entryPointProbeRawRelativePath = "entry-point-probes/evidence.json"
    $entryPointProbeRawPath = Join-Path $Root $entryPointProbeRawRelativePath
    Write-Json -Path $entryPointProbeRawPath -Value ([ordered]@{
            schema = "openlineops.staged-agent-entry-point-probe-evidence"
            schemaVersion = 1
            agentArtifactSha256 = $evidence.agentArtifact.sha256
            entryPoints = $evidence.entryPoints
            probes = $evidence.entryPointProbes
        })
    $evidence.entryPointProbeEvidence = [ordered]@{
        evidence = $entryPointProbeRawRelativePath
        evidenceSha256 = Get-FileSha256 $entryPointProbeRawPath
    }
    Write-Json -Path (Join-Path $Root "evidence.json") -Value $evidence
}
