param(
    [string] $ArtifactsRoot = "artifacts/release",

    [string] $WorkRoot = "output/publication-readiness",

    [switch] $SkipReleaseArtifacts,

    [switch] $AllowPendingExternal
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Failures = New-Object System.Collections.Generic.List[string]
$Warnings = New-Object System.Collections.Generic.List[string]

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

function Test-ContainsOrdinal {
    param(
        [Parameter(Mandatory = $true)][string[]] $Values,
        [Parameter(Mandatory = $true)][string] $Expected
    )

    return @($Values | Where-Object {
        [string]::Equals($_, $Expected, [System.StringComparison]::Ordinal)
    }).Count -gt 0
}

function Add-Warning {
    param([Parameter(Mandatory = $true)][string] $Message)
    $Warnings.Add($Message) | Out-Null
}

function Add-PendingExternal {
    param([Parameter(Mandatory = $true)][string] $Message)

    if ($AllowPendingExternal) {
        Add-Warning "PENDING EXTERNAL: $Message"
    }
    else {
        Add-Failure "PENDING EXTERNAL: $Message"
    }
}

function Test-RequiredFile {
    param([Parameter(Mandatory = $true)][string] $Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        Add-Failure "Missing required file: $Path"
        return $false
    }

    return $true
}

function Test-RequiredDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        Add-Failure "Missing required directory: $Path"
        return $false
    }

    return $true
}

function Test-FileContains {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Pattern,
        [Parameter(Mandatory = $true)][string] $Message
    )

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        Add-Failure "Cannot inspect missing file: $Path"
        return
    }

    if (-not (Select-String -LiteralPath $resolved -Pattern $Pattern -Quiet)) {
        Add-Failure $Message
    }
}

function Test-FileDoesNotContain {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Pattern,
        [Parameter(Mandatory = $true)][string] $Message
    )

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        Add-Failure "Cannot inspect missing file: $Path"
        return
    }

    if (Select-String -LiteralPath $resolved -Pattern $Pattern -Quiet) {
        Add-PendingExternal $Message
    }
}

function Test-FileForbiddenPattern {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Pattern,
        [Parameter(Mandatory = $true)][string] $Message
    )

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        Add-Failure "Cannot inspect missing file: $Path"
        return
    }

    if (Select-String -LiteralPath $resolved -Pattern $Pattern -Quiet) {
        Add-Failure $Message
    }
}

function Test-ReleaseArtifacts {
    if ($SkipReleaseArtifacts) {
        Add-Warning "Release artifact verification skipped."
        return
    }

    $resolvedArtifactsRoot = Resolve-RepoPath $ArtifactsRoot
    $manifestPath = Join-Path $resolvedArtifactsRoot "release-manifest.json"
    $checksumsPath = Join-Path $resolvedArtifactsRoot "checksums.sha256"
    $provenancePath = Join-Path $resolvedArtifactsRoot "release-provenance.json"
    $dependencyInventoryPath = Join-Path $resolvedArtifactsRoot "release-dependency-inventory.json"
    $metadataChecksumsPath = Join-Path $resolvedArtifactsRoot "release-metadata-checksums.sha256"
    $requiredFiles = @(
        $manifestPath,
        $checksumsPath,
        (Join-Path $resolvedArtifactsRoot "release-notes.md"),
        $dependencyInventoryPath,
        $provenancePath,
        $metadataChecksumsPath
    )

    $missingReleaseFiles = $false
    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            Add-Failure "Missing staged release file: $requiredFile"
            $missingReleaseFiles = $true
        }
    }

    if ($missingReleaseFiles) {
        return
    }

    $arguments = @(
        "run",
        "--project",
        (Resolve-RepoPath "tools/OpenLineOps.ReleaseManifest/OpenLineOps.ReleaseManifest.csproj"),
        "--no-build",
        "--",
        "--verify",
        "--artifacts",
        $resolvedArtifactsRoot,
        "--manifest",
        $manifestPath,
        "--checksums",
        $checksumsPath,
        "--require-kind",
        "source",
        "--require-kind",
        "api",
        "--require-kind",
        "agent",
        "--require-kind",
        "runner",
        "--require-kind",
        "desktop",
        "--require-kind",
        "plugin-host",
        "--require-kind",
        "script-worker",
        "--require-kind",
        "sample-plugin"
    )

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        Add-Failure "Release manifest verification failed with exit code $LASTEXITCODE."
    }

    Test-WindowsReleasePackage `
        -ResolvedArtifactsRoot $resolvedArtifactsRoot `
        -ArtifactKind "desktop" `
        -ArchivePattern "desktop-*.zip" `
        -RequiredEntries @(
            "package/win-unpacked/OpenLineOps.exe",
            "package/win-unpacked/OPENLINEOPS-PACKAGE-NOTES.txt",
            "package/win-unpacked/resources/app/package.json",
            "package/win-unpacked/resources/app/dist/index.html",
            "dist-electron/main/api-credential-security.js",
            "dist-electron/main/application-extension-import-security.js",
            "dist-electron/main/backend-api-security.js",
            "dist-electron/main/backend-process-handshake.js",
            "dist-electron/main/local-sqlite-connection.js",
            "dist-electron/main/renderer-navigation-security.js",
            "dist-electron/main/trace-artifact-save.js",
            "dist-electron/main/trace-artifact-save-core.js",
            "dist-electron/preload/preload.cjs",
            "package/win-unpacked/resources/app/dist-electron/main/api-credential-security.js",
            "package/win-unpacked/resources/app/dist-electron/main/application-extension-import-security.js",
            "package/win-unpacked/resources/app/dist-electron/main/backend-api-security.js",
            "package/win-unpacked/resources/app/dist-electron/main/backend-process-handshake.js",
            "package/win-unpacked/resources/app/dist-electron/main/local-sqlite-connection.js",
            "package/win-unpacked/resources/app/dist-electron/main/main.js",
            "package/win-unpacked/resources/app/dist-electron/main/renderer-navigation-security.js",
            "package/win-unpacked/resources/app/dist-electron/main/trace-artifact-save.js",
            "package/win-unpacked/resources/app/dist-electron/main/trace-artifact-save-core.js",
            "package/win-unpacked/resources/app/dist-electron/preload/preload.cjs",
            "package/win-unpacked/resources/app/runtime/api/OpenLineOps.Api.exe",
            "package/win-unpacked/resources/app/runtime/api/appsettings.json",
            "package/win-unpacked/resources/app/runtime/plugin-host/OpenLineOps.PluginHost.exe",
            "package/win-unpacked/resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.exe") `
        -SignedEntries @(
            "package/win-unpacked/OpenLineOps.exe",
            "package/win-unpacked/resources/app/runtime/api/OpenLineOps.Api.exe",
            "package/win-unpacked/resources/app/runtime/plugin-host/OpenLineOps.PluginHost.exe",
            "package/win-unpacked/resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.exe")
    Test-WindowsReleasePackage `
        -ResolvedArtifactsRoot $resolvedArtifactsRoot `
        -ArtifactKind "agent" `
        -ArchivePattern "agent-*.zip" `
        -RequiredEntries @(
            "OpenLineOps.Agent.exe",
            "OpenLineOps.StationRuntime.exe",
            "OpenLineOps.PluginHost.exe",
            "OpenLineOps.ScriptWorker.exe",
            "OpenLineOps.LeastPrivilegeLauncher.exe",
            "appsettings.json",
            "bundle-manifest.json",
            "bundle-checksums.sha256") `
        -SignedEntries @("OpenLineOps.Agent.exe", "OpenLineOps.StationRuntime.exe", "OpenLineOps.PluginHost.exe", "OpenLineOps.ScriptWorker.exe", "OpenLineOps.LeastPrivilegeLauncher.exe")
    Test-WindowsReleasePackage `
        -ResolvedArtifactsRoot $resolvedArtifactsRoot `
        -ArtifactKind "runner" `
        -ArchivePattern "runner-*.zip" `
        -RequiredEntries @(
            "OpenLineOps.Runner.exe",
            "bundle-manifest.json",
            "bundle-checksums.sha256") `
        -SignedEntries @("OpenLineOps.Runner.exe")
}

function Test-WindowsReleasePackage {
    param(
        [Parameter(Mandatory = $true)][string] $ResolvedArtifactsRoot,
        [Parameter(Mandatory = $true)][string] $ArtifactKind,
        [Parameter(Mandatory = $true)][string] $ArchivePattern,
        [Parameter(Mandatory = $true)][string[]] $RequiredEntries,
        [Parameter(Mandatory = $true)][string[]] $SignedEntries
    )

    $artifactDirectory = Join-Path $ResolvedArtifactsRoot $ArtifactKind
    $archives = if (Test-Path -LiteralPath $artifactDirectory -PathType Container) {
        @(Get-ChildItem -LiteralPath $artifactDirectory -Filter $ArchivePattern -File)
    }
    else {
        @()
    }
    if ($archives.Count -ne 1) {
        Add-Failure "Expected exactly one $ArtifactKind release archive, found $($archives.Count)."
        return
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($archives[0].FullName)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
        foreach ($entryName in $entries) {
            if ($entryName.Contains([char]92)) {
                Add-Failure "$ArtifactKind release archive contains a non-canonical backslash zip entry path: $entryName"
            }
        }

        foreach ($requiredEntry in $RequiredEntries) {
            if (-not (Test-ContainsOrdinal -Values $entries -Expected $requiredEntry)) {
                Add-Failure "$ArtifactKind release archive is missing $requiredEntry."
            }
        }

        foreach ($signedEntry in $SignedEntries) {
            Test-WindowsReleaseSignature `
                -Archive $archive `
                -ArtifactKind $ArtifactKind `
                -EntryName $signedEntry
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Test-WindowsReleaseSignature {
    param(
        [Parameter(Mandatory = $true)]$Archive,
        [Parameter(Mandatory = $true)][string] $ArtifactKind,
        [Parameter(Mandatory = $true)][string] $EntryName
    )

    $entry = $Archive.Entries |
        Where-Object {
            [string]::Equals(
                $_.FullName,
                $EntryName,
                [System.StringComparison]::Ordinal)
        } |
        Select-Object -First 1

    if ($entry -eq $null) {
        return
    }

    $resolvedWorkRoot = Resolve-RepoPath $WorkRoot
    Assert-UnderRepoRoot $resolvedWorkRoot
    if (Test-Path -LiteralPath $resolvedWorkRoot) {
        Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $resolvedWorkRoot -Force | Out-Null
    $safeName = ($ArtifactKind + "-" + $EntryName) -replace "[^A-Za-z0-9._-]", "_"
    $extractedExe = Join-Path $resolvedWorkRoot $safeName
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $extractedExe, $true)

    $signature = Get-AuthenticodeSignature -LiteralPath $extractedExe
    if ($signature.Status -ne "Valid") {
        Add-PendingExternal "$ArtifactKind release executable '$EntryName' is not signed with a valid Authenticode signature. Status: $($signature.Status)."
    }
}

$requiredFiles = @(
    "README.md",
    "LICENSE",
    "THIRD-PARTY-NOTICES.md",
    "CONTRIBUTING.md",
    "CODE_OF_CONDUCT.md",
    "SECURITY.md",
    "global.json",
    "OpenLineOps.sln",
    "OpenLineOps.slnx",
    ".github/workflows/build.yml",
    ".github/pull_request_template.md",
    ".github/ISSUE_TEMPLATE/bug_report.yml",
    ".github/ISSUE_TEMPLATE/feature_request.yml",
    ".github/ISSUE_TEMPLATE/plugin_request.yml",
    ".github/ISSUE_TEMPLATE/config.yml",
    "eng/verify-ci-workflow-actions.ps1",
    "eng/verify-ci-release-artifact-inspection.ps1",
    "eng/verify-dotnet-package-vulnerabilities.ps1",
    "eng/verify-dotnet-package-vulnerabilities.tests.ps1",
    "eng/verify-release-staging-security.ps1",
    "eng/verify-station-agent-content-cache-contract.ps1",
    "eng/verify-staged-agent-bundle-e2e.ps1",
    "eng/verify-staged-agent-rabbitmq-e2e.ps1",
    "eng/invoke-run-scoped-agent-service-cleanup.ps1",
    "eng/verify-agent-service-external-abort-cleanup.ps1",
    "eng/verify-staged-agent-evidence.ps1",
    "eng/verify-production-closure-evidence.ps1",
    "eng/verify-studio-two-agent-production-closure.ps1",
    "eng/verify-studio-two-agent-production-evidence.ps1",
    "eng/verify-studio-two-agent-production-evidence.tests.ps1",
    "eng/verify-runner-staged-agent-e2e.ps1",
    "eng/verify-runner-staged-agent-evidence.ps1",
    "eng/verify-runner-staged-agent-evidence.tests.ps1",
    "eng/verify-evidence-validation.tests.ps1",
    "eng/evidence-validation-test-fixtures.ps1",
    "eng/verify-solution-project-coverage.ps1",
    "eng/inspect-ci-release-artifact.ps1",
    "eng/inspect-release-candidate.ps1",
    "eng/prepare-final-publication.ps1",
    "eng/verify-final-publication-preflight.ps1",
    "eng/write-publication-evidence.ps1",
    "eng/verify-publication-evidence.ps1",
    "eng/write-production-integration-evidence.ps1",
    "eng/verify-production-integration-evidence.ps1",
    "eng/verify-release-candidate-inspection.ps1",
    "eng/verify-open-source-metadata.ps1",
    "eng/verify-third-party-license-metadata.ps1",
    "eng/sign-windows-package.ps1",
    "eng/verify-windows-signing-readiness.ps1",
    "eng/finalize-publication-metadata.ps1",
    "eng/verify-publication-metadata-finalization.ps1",
    "eng/verify-publication-readiness.ps1",
    "docs/release-packaging.md",
    "docs/station-agent-deployment.md",
    "docs/headless-runner.md",
    "docs/plugin-authoring.md",
    "docs/python-scripting-integration.md"
)

foreach ($requiredFile in $requiredFiles) {
    Test-RequiredFile $requiredFile | Out-Null
}

Test-RequiredDirectory "samples/plugins/OpenLineOps.SamplePlugins.LoopbackDevice" | Out-Null
Test-RequiredDirectory "apps/desktop" | Out-Null

Test-FileContains "global.json" '"version"\s*:\s*"10\.' "global.json must pin a .NET 10 SDK."
Test-FileContains "README.md" "OpenLineOps is licensed under the MIT License" "README.md must state the MIT license."
Test-FileContains "LICENSE" "^MIT License" "LICENSE must contain MIT License text."
Test-FileContains "THIRD-PARTY-NOTICES.md" "^# Third-Party Notices" "Third-party notices must be generated at the repository root."
Test-FileContains "Directory.Build.props" "PackageLicenseExpression" ".NET projects must declare default license metadata."
Test-FileContains "apps/desktop/package.json" '"license"\s*:\s*"MIT"' "Desktop package.json must declare MIT license metadata."
Test-FileContains ".github/workflows/build.yml" "dotnet-version:\s*10\.0\.301" "CI must use the exact global.json .NET SDK."
Test-FileContains ".github/workflows/build.yml" "Verify CI workflow actions" "CI must verify workflow action references."
Test-FileContains ".github/workflows/build.yml" "Verify solution project coverage" "CI must verify every formal project is covered by the solution."
Test-FileContains ".github/workflows/build.yml" "Verify open-source metadata" "CI must verify open-source metadata."
Test-FileContains ".github/workflows/build.yml" "Verify third-party license metadata" "CI must verify third-party license metadata."
Test-FileContains ".github/workflows/build.yml" "verify-dotnet-package-vulnerabilities\.ps1" "CI must run the fail-closed NuGet vulnerability audit."
Test-FileContains ".github/workflows/build.yml" "verify-dotnet-package-vulnerabilities\.tests\.ps1" "CI must prove vulnerable NuGet JSON fails the build."
Test-FileContains ".github/workflows/build.yml" "verify-release-staging-security\.ps1" "CI must regression-test release staging security boundaries."
Test-FileContains ".github/workflows/build.yml" "verify-station-agent-content-cache-contract\.ps1" "CI must enforce explicit administrator-only Station content-cache provisioning."
Test-FileContains ".github/workflows/build.yml" "verify-evidence-validation\.tests\.ps1" "CI must regression-test fail-closed publication evidence validation."
Test-FileContains ".github/workflows/build.yml" "verify-production-closure-evidence\.ps1" "CI must independently scan sanitized production closure evidence before upload."
Test-FileContains ".github/workflows/build.yml" "verify-studio-two-agent-production-closure\.ps1" "CI must run the packaged-to-two-staged-Agent production closure."
Test-FileContains ".github/workflows/build.yml" "verify-studio-two-agent-production-evidence\.ps1" "CI must independently scan Studio two-Agent public evidence."
Test-FileContains ".github/workflows/build.yml" "verify-runner-staged-agent-e2e\.ps1" "CI must run the staged Runner production closure."
Test-FileContains ".github/workflows/build.yml" "verify-runner-staged-agent-evidence\.ps1" "CI must independently scan Runner public evidence."
Test-FileContains ".github/workflows/build.yml" "write-production-integration-evidence\.ps1" "CI must bind the real integration TRX to its repository, commit, and run."
Test-FileContains ".github/workflows/build.yml" "verify-production-integration-evidence\.ps1" "CI must regression-test strict integration evidence parsing."
Test-FileContains ".github/workflows/build.yml" "actions/download-artifact@v8" "CI must download same-run candidate and integration proof artifacts with the pinned action."
Test-FileContains ".github/workflows/build.yml" "needs:\s*" "The final publication job must declare explicit job dependencies."
Test-FileContains ".github/workflows/build.yml" "production-integration" "The final publication job must depend on the real production integration gate."
Test-FileContains ".github/workflows/build.yml" "Stage release artifacts" "CI must stage release artifacts."
Test-FileContains ".github/workflows/build.yml" "Verify staged release manifest" "CI must verify staged release metadata."
Test-FileContains ".github/workflows/build.yml" "Inspect release candidate" "CI must inspect the staged release candidate."
Test-FileContains ".github/workflows/build.yml" "Run staged Agent bundle E2E" "CI must execute the extracted staged Agent process chain."
Test-FileContains ".github/workflows/build.yml" "Verify release candidate inspection" "CI must verify release candidate inspection behavior."
Test-FileContains ".github/workflows/build.yml" "Write publication evidence" "CI must write publication evidence."
Test-FileContains ".github/workflows/build.yml" "Verify publication evidence" "CI must verify publication evidence behavior."
Test-FileContains ".github/workflows/build.yml" "Verify final publication preflight" "CI must verify final publication preflight behavior."
Test-FileContains ".github/workflows/build.yml" "Inspect CI release artifact bundle" "CI must inspect the release artifact bundle before upload."
Test-FileContains ".github/workflows/build.yml" "Verify CI release artifact inspection" "CI must regression-test the CI release artifact inspector."
Test-FileContains ".github/workflows/build.yml" "actions/upload-artifact@" "CI must upload validated release artifacts."
Test-FileContains ".github/workflows/build.yml" "npm run test:api-credential-security" "CI must verify the local API credential and backend identity boundary."
Test-FileContains ".github/workflows/build.yml" "npm run test:extension-import-security" "CI must verify the trusted Application extension ZIP import boundary."
Test-FileContains ".github/workflows/build.yml" "npm run test:trace-artifact-save" "CI must verify Trace artifact hashing, session binding, and destination confinement."
Test-FileContains ".github/workflows/build.yml" "npm run test:production-route-validation" "CI must verify the production route graph contract."
Test-FileContains ".github/workflows/build.yml" "npm run test:production-route-layout" "CI must verify production route layout persistence and conflict handling."
Test-FileContains ".github/workflows/build.yml" "npm run smoke:e2e" "CI must run the Electron smoke test."
Test-FileContains ".github/workflows/build.yml" "npm audit --audit-level=high" "CI must run the desktop high-severity audit."
Test-FileContains "eng/verify-ci-workflow-actions.ps1" "Unexpected GitHub Action reference" "CI workflow action verification must reject unapproved action references."
Test-FileContains "eng/verify-ci-workflow-actions.ps1" "actions/upload-artifact@v7" "CI workflow action verification must require upload-artifact v7."
Test-FileContains "eng/verify-ci-workflow-actions.ps1" "if-no-files-found:\\s\*error" "CI workflow action verification must require missing artifact failures."
Test-FileContains "eng/verify-ci-workflow-actions.ps1" "output/final-publication-preflight" "CI workflow action verification must require uploaded final publication preflight diagnostics."
Test-FileContains "eng/verify-ci-workflow-actions.ps1" "verify-staged-agent-bundle-e2e" "CI workflow action verification must preserve the staged Agent bundle E2E gate."
Test-FileContains "eng/inspect-ci-release-artifact.ps1" "publication-evidence-verification" "CI release artifact inspection must verify publication evidence verification diagnostics."
Test-FileContains "eng/inspect-ci-release-artifact.ps1" "publication-preflight.json" "CI release artifact inspection must verify final publication preflight diagnostics."
Test-FileContains "eng/inspect-ci-release-artifact.ps1" "release-dependency-inventory.json" "CI release artifact inspection must verify dependency inventory metadata."
Test-FileContains "eng/inspect-ci-release-artifact.ps1" "release-metadata-checksums.sha256" "CI release artifact inspection must verify metadata checksums."
Test-FileContains "eng/inspect-ci-release-artifact.ps1" "ci-release-artifact-inspection.json" "CI release artifact inspection must write JSON evidence."
Test-FileContains "eng/inspect-ci-release-artifact.ps1" "verify-staged-agent-evidence.ps1" "CI release artifact inspection must rerun strict staged Agent evidence validation."
Test-FileContains "eng/inspect-ci-release-artifact.ps1" "verify-production-closure-evidence.ps1" "CI release artifact inspection must rerun strict production closure evidence validation."
Test-FileContains "eng/inspect-ci-release-artifact.ps1" "verify-studio-two-agent-production-evidence.ps1" "CI release artifact inspection must rerun strict Studio two-Agent evidence validation."
Test-FileContains "eng/inspect-ci-release-artifact.ps1" "verify-runner-staged-agent-evidence.ps1" "CI release artifact inspection must rerun strict Runner evidence validation."
Test-FileContains ".github/workflows/build.yml" "output/ci-release-artifact-inspection" "CI must upload release artifact inspection diagnostics."
Test-FileContains "eng/inspect-ci-release-artifact.ps1" "RequirePublishable" "CI release artifact inspection must support final publishable enforcement."
Test-FileContains "eng/inspect-ci-release-artifact.ps1" "inspect-release-candidate.ps1" "CI release artifact inspection must inspect the staged release candidate."
Test-FileContains "eng/verify-open-source-metadata.ps1" "PackageLicenseExpression" "Open-source metadata verification must inspect .NET package metadata."
Test-FileContains "eng/verify-third-party-license-metadata.ps1" "project.assets.json" "Third-party license metadata verification must inspect NuGet restore assets."
Test-FileContains "eng/verify-third-party-license-metadata.ps1" "package-lock.json" "Third-party license metadata verification must inspect the desktop package lock."
Test-FileContains "eng/verify-third-party-license-metadata.ps1" "UpdateNotice" "Third-party license metadata verification must support regenerating THIRD-PARTY-NOTICES.md."
Test-FileContains "eng/verify-third-party-license-metadata.ps1" "InventoryPath" "Third-party license metadata verification must support writing dependency inventory."
Test-FileContains "eng/stage-release-artifacts.ps1" "release-dependency-inventory.json" "Release staging must generate dependency inventory metadata."
Test-FileContains "eng/stage-release-artifacts.ps1" '\$safeArguments = Format-CommandArgumentsForLog -Arguments \$Arguments' "Release staging failures must redact sensitive child-process arguments."
Test-FileForbiddenPattern "eng/stage-release-artifacts.ps1" 'Command failed with exit code \$\{exitCode\}: \$FilePath \$\(\$Arguments -join' "Release staging failures must never echo raw signing arguments."
Test-FileContains "eng/stage-release-artifacts.ps1" "ls-files --cached" "Release source staging must enumerate only tracked Git paths."
Test-FileContains "eng/stage-release-artifacts.ps1" "RequireCleanGitWorkTree" "Formal release staging must require a clean Git worktree."
Test-FileContains "eng/stage-release-artifacts.ps1" "ExpectedGitCommit" "Formal release staging must bind source bytes to an expected Git commit."
Test-FileContains "eng/verify-release-staging-security.ps1" "untracked-secret" "Release staging security regression must prove untracked files cannot enter source archives."
Test-FileContains "eng/verify-release-staging-security.ps1" "requires a clean Git worktree" "Release staging security regression must prove formal publication rejects dirty trees."
Test-FileContains "eng/verify-release-staging-security.ps1" "publication-password-sentinel" "Release staging security regression must prove removed password arguments cannot leak secrets."
Test-FileContains "eng/verify-release-staging-security.ps1" "taskkill" "Release staging security regression must prove timed-out process trees are terminated."
Test-FileContains "eng/stage-release-artifacts.ps1" "release-metadata-checksums.sha256" "Release staging must generate metadata checksums."
Test-FileContains "eng/inspect-release-candidate.ps1" "RequireSignedWindowsArtifacts" "Release candidate inspection must enforce every shipped Windows executable signature."
Test-FileContains "eng/inspect-release-candidate.ps1" "bundle-manifest.json" "Release candidate inspection must verify the Agent and Runner bundle manifests."
Test-FileContains "eng/inspect-release-candidate.ps1" "OpenLineOps.StationRuntime.exe" "Release candidate inspection must require Station Runtime in the Agent bundle."
Test-FileContains "eng/inspect-release-candidate.ps1" "OpenLineOps.PluginHost.exe" "Release candidate inspection must require Plugin Host in the Agent bundle."
Test-FileContains "eng/inspect-release-candidate.ps1" "OpenLineOps.ScriptWorker.exe" "Release candidate inspection must require the Python Script Worker in the Agent bundle."
Test-FileContains "eng/inspect-release-candidate.ps1" "OpenLineOps.LeastPrivilegeLauncher.exe" "Release candidate inspection must require the Least Privilege Launcher in the Agent bundle."
Test-FileContains "eng/inspect-release-candidate.ps1" "SafetyExecutablePath release template must be empty" "Release candidate inspection must require a machine-specific empty safety actuator template."
Test-FileContains "eng/inspect-release-candidate.ps1" "path traversal zip entry" "Release candidate inspection must reject unsafe zip entry paths."
Test-FileContains "eng/inspect-release-candidate.ps1" "sensitive source archive entry" "Release candidate inspection must reject sensitive source archive entries."
Test-FileContains "eng/inspect-release-candidate.ps1" "release-provenance.json" "Release candidate inspection must verify release provenance metadata."
Test-FileContains "eng/inspect-release-candidate.ps1" "release-dependency-inventory.json" "Release candidate inspection must verify dependency inventory metadata."
Test-FileContains "eng/inspect-release-candidate.ps1" "dependencyInventory" "Release candidate inspection must verify dependency inventory provenance hash."
Test-FileContains "eng/inspect-release-candidate.ps1" "release-metadata-checksums.sha256" "Release candidate inspection must verify metadata checksums."
Test-FileContains "eng/inspect-release-candidate.ps1" "verify-ci-workflow-actions.ps1" "Release candidate inspection must require CI workflow action verification in the source archive."
Test-FileContains "eng/inspect-release-candidate.ps1" "verify-staged-agent-bundle-e2e.ps1" "Release candidate inspection must require the staged Agent bundle E2E gate in the source archive."
Test-FileContains "eng/inspect-release-candidate.ps1" "verify-studio-two-agent-production-closure.ps1" "Release candidate inspection must require the Studio two-Agent gate in the source archive."
Test-FileContains "eng/inspect-release-candidate.ps1" "verify-runner-staged-agent-e2e.ps1" "Release candidate inspection must require the staged Runner gate in the source archive."
Test-FileContains "eng/inspect-release-candidate.ps1" "inspect-ci-release-artifact.ps1" "Release candidate inspection must require CI artifact bundle inspection in the source archive."
Test-FileContains "eng/inspect-release-candidate.ps1" "prepare-final-publication.ps1" "Release candidate inspection must require the final publication preparation script in the source archive."
Test-FileContains "eng/inspect-release-candidate.ps1" "verify-final-publication-preflight.ps1" "Release candidate inspection must require the final publication preflight verification script in the source archive."
Test-FileContains "eng/inspect-release-candidate.ps1" "write-publication-evidence.ps1" "Release candidate inspection must require the publication evidence script in the source archive."
Test-FileContains "eng/inspect-release-candidate.ps1" "verify-publication-evidence.ps1" "Release candidate inspection must require the publication evidence verification script in the source archive."
Test-FileContains "eng/inspect-release-candidate.ps1" "verify-release-candidate-inspection.ps1" "Release candidate inspection must require its behavior verification script in the source archive."
Test-FileContains "eng/prepare-final-publication.ps1" "ConfirmMitLicense is required" "Final publication preparation must require explicit MIT confirmation."
Test-FileContains "eng/prepare-final-publication.ps1" "SignWindowsPackages" "Final publication preparation must require signing every Windows deliverable."
Test-FileContains "eng/prepare-final-publication.ps1" "RequireSignedWindowsArtifacts" "Final publication preparation must inspect all signed Windows deliverables."
Test-FileContains "eng/prepare-final-publication.ps1" "RequirePublishable" "Final publication preparation must require publishable evidence."
Test-FileContains "eng/prepare-final-publication.ps1" "ProductionIntegrationEvidencePath" "Final publication preparation must consume a bound production integration evidence file."
Test-FileContains "eng/verify-final-publication-preflight.ps1" "production-integration-evidence" "Final publication preflight verification must cover missing or invalid production integration evidence."
Test-FileContains "eng/verify-final-publication-preflight.ps1" "missing-signing-selector" "Final publication preflight verification must cover missing code-signing selectors."
Test-FileContains "eng/verify-final-publication-preflight.ps1" "publication-preflight.json" "Final publication preflight verification must write JSON evidence."
Test-FileContains "eng/verify-final-publication-preflight.ps1" "publication-preflight.md" "Final publication preflight verification must write Markdown evidence."
Test-FileContains "eng/write-publication-evidence.ps1" "ProductionIntegrationEvidencePath" "Publication evidence must be derived from a bound production integration evidence file."
Test-FileContains "eng/write-publication-evidence.ps1" "ConfirmMitLicense" "Publication evidence must record final MIT license confirmation."
Test-FileContains "eng/write-publication-evidence.ps1" "RequirePublishable" "Publication evidence must support a final publishable assertion."
Test-FileContains "eng/write-publication-evidence.ps1" "release-candidate-inspection-verification" "Publication evidence must isolate child gate work directories."
Test-FileContains "eng/write-publication-evidence.ps1" "publication-readiness-strict" "Publication evidence must isolate strict readiness work directories."
Test-FileContains "eng/write-publication-evidence.ps1" "verify-staged-agent-evidence.ps1" "Publication evidence generation must require strict staged Agent evidence."
Test-FileContains "eng/write-publication-evidence.ps1" "verify-production-closure-evidence.ps1" "Publication evidence generation must require strict production closure evidence."
Test-FileContains "eng/write-publication-evidence.ps1" "verify-studio-two-agent-production-evidence.ps1" "Publication evidence generation must require strict Studio two-Agent evidence."
Test-FileContains "eng/write-publication-evidence.ps1" "verify-runner-staged-agent-evidence.ps1" "Publication evidence generation must require strict Runner evidence."
Test-FileContains "eng/verify-staged-agent-evidence.ps1" "serviceTokenConnected" "Staged Agent evidence must require the service-token material-arrival connection fact."
Test-FileContains "eng/verify-staged-agent-evidence.ps1" "pipeExactAclVerified" "Staged Agent evidence must require the exact material-arrival pipe ACL fact."
Test-FileContains "eng/verify-staged-agent-evidence.ps1" "durablePublicationVerified" "Staged Agent evidence must require the durable material-arrival publication fact."
Test-FileContains "eng/verify-staged-agent-evidence.ps1" "ordinaryCiTokenExplicitAccessDenied" "Staged Agent evidence must require explicit denial of the ordinary CI token."
Test-FileContains "eng/verify-evidence-validation.tests.ps1" "staged Agent material-arrival proof uses a truthy integer" "Staged Agent evidence regression must reject truthy non-boolean material-arrival facts."
Test-FileContains "eng/verify-studio-two-agent-production-evidence.ps1" "entryServiceTokenConnected" "Studio two-Agent evidence must require the entry service-token connection fact."
Test-FileContains "eng/verify-studio-two-agent-production-evidence.ps1" "entryPipeExactAclVerified" "Studio two-Agent evidence must require the entry pipe exact-ACL fact."
Test-FileContains "eng/verify-studio-two-agent-production-evidence.ps1" "downstreamServiceTokenExplicitAccessDenied" "Studio two-Agent evidence must require explicit denial of the downstream service token."
Test-FileContains "eng/verify-studio-two-agent-production-evidence.ps1" "bothServicesRunningOnOriginalPids" "Studio two-Agent evidence must require both services to remain on their original PIDs."
Test-FileContains "eng/verify-studio-two-agent-production-evidence.tests.ps1" "entry service token proof uses a truthy integer" "Studio two-Agent evidence regression must reject a truthy integer identity fact."
Test-FileContains "eng/verify-studio-two-agent-production-evidence.tests.ps1" "entry pipe ACL proof uses a truthy string" "Studio two-Agent evidence regression must reject a truthy string identity fact."
Test-FileContains "eng/verify-ci-release-artifact-inspection.ps1" "fully-rebound-staged-material-arrival-downgrade" "CI artifact inspection regression must reject fully rebound staged material-arrival semantic downgrade."
Test-FileContains "eng/verify-ci-release-artifact-inspection.ps1" "fully-rebound-studio-windows-identity-downgrade" "CI artifact inspection regression must reject fully rebound Studio identity semantic downgrade."
Test-FileContains "eng/verify-evidence-validation.tests.ps1" "station package contains zip traversal" "Evidence regression must reject Station package traversal."
Test-FileContains "eng/verify-evidence-validation.tests.ps1" "production summary missing recovery scenario" "Evidence regression must reject reduced production scenarios."
Test-FileContains "eng/verify-evidence-validation.tests.ps1" "staged Agent once-only proof reduced" "Evidence regression must reject reduced once-only delivery proof."
Test-FileContains "eng/verify-evidence-validation.tests.ps1" "staged Agent Windows service name missing" "Evidence regression must reject missing staged Agent SCM service identity."
Test-FileContains "eng/verify-evidence-validation.tests.ps1" "staged Agent Windows service lifecycle not verified" "Evidence regression must reject an unverified staged Agent SCM lifecycle."
Test-FileContains "eng/verify-evidence-validation.tests.ps1" "staged Agent legacy identity strategy rejected" "Evidence regression must reject staged Agent identity compatibility branches."
Test-FileContains "eng/verify-evidence-validation.tests.ps1" "staged Agent administrator group token presence rejected" "Evidence regression must reject staged Agent administrator-group token presence."
Test-FileContains "eng/verify-evidence-validation.tests.ps1" "staged Agent unrestricted token rejected" "Evidence regression must reject an unrestricted Agent service token."
Test-FileContains "eng/verify-evidence-validation.tests.ps1" "staged Agent missing restricted SID membership rejected" "Evidence regression must reject missing exact restricted service-SID membership."
Test-FileContains "eng/verify-evidence-validation.tests.ps1" "staged Agent restart service SID mismatch rejected" "Evidence regression must reject service identity drift across restart."
Test-FileContains "eng/verify-evidence-validation.tests.ps1" "staged Agent service SID not derived from service name rejected" "Evidence regression must reject a service SID unrelated to the SCM service name."
Test-FileContains "eng/verify-staged-agent-evidence.ps1" "local-service-restricted-service-sid" "Staged Agent evidence must require the sole LocalService restricted service-SID identity strategy."
Test-FileContains "eng/verify-staged-agent-evidence.ps1" "exactServiceSidRestricted" "Staged Agent evidence must prove exact service-SID membership in the restricted SID list."
Test-FileContains "eng/verify-staged-agent-evidence.ps1" "Get-ServiceSidFromName" "Staged Agent evidence must independently derive the exact SID from the SCM service name."
Test-FileContains "eng/verify-staged-agent-evidence.ps1" "windowsServiceLifecycleVerified" "Staged Agent evidence must require the Windows SCM lifecycle proof."
Test-FileContains "eng/verify-publication-evidence.ps1" "invalid-production-integration-evidence" "Publication evidence verification must reject invalid production integration evidence."
Test-FileContains "eng/verify-publication-evidence.ps1" "RequirePublishable" "Publication evidence verification must cover final publishable assertions."
Test-FileContains "eng/write-production-integration-evidence.ps1" "trx =" "Production integration evidence must bind the deterministic TRX."
Test-FileContains "eng/write-production-integration-evidence.ps1" "CommitSha" "Production integration evidence must bind the tested commit."
Test-FileContains "eng/verify-production-integration-evidence.ps1" "missing-required-test" "Production integration evidence regression must reject incomplete test suites."
Test-FileForbiddenPattern "eng/prepare-final-publication.ps1" "GitHubActionsRunUrl" "Final publication must not retain the arbitrary GitHub Actions URL contract."
Test-FileForbiddenPattern "eng/write-publication-evidence.ps1" "GitHubActionsRunUrl" "Publication evidence must not accept an arbitrary GitHub Actions URL."
Test-FileForbiddenPattern "docs/release-packaging.md" "GitHubActionsRunUrl" "Release documentation must not teach the removed arbitrary URL proof contract."
Test-FileContains "eng/verify-release-candidate-inspection.ps1" "unsafe-path" "Release candidate inspection verification must cover unsafe archive paths."
Test-FileContains "eng/verify-release-candidate-inspection.ps1" "sensitive-source" "Release candidate inspection verification must cover sensitive source archive entries."
Test-FileContains "eng/verify-release-candidate-inspection.ps1" "bad-provenance" "Release candidate inspection verification must cover bad provenance metadata."
Test-FileContains "eng/verify-release-candidate-inspection.ps1" "missing-provenance" "Release candidate inspection verification must cover missing provenance metadata."
Test-FileContains "eng/verify-release-candidate-inspection.ps1" "missing-dependency-inventory" "Release candidate inspection verification must cover missing dependency inventory metadata."
Test-FileContains "eng/verify-release-candidate-inspection.ps1" "bad-dependency-inventory" "Release candidate inspection verification must cover bad dependency inventory metadata."
Test-FileContains "eng/verify-release-candidate-inspection.ps1" "missing-metadata-checksums" "Release candidate inspection verification must cover missing metadata checksums."
Test-FileContains "eng/verify-release-candidate-inspection.ps1" "bad-metadata-checksums" "Release candidate inspection verification must cover bad metadata checksums."
Test-FileContains "eng/verify-release-candidate-inspection.ps1" "tampered-agent-bundle" "Release candidate inspection verification must reject tampered Agent payloads."
Test-FileContains "eng/verify-release-candidate-inspection.ps1" "missing-agent-safety-executable-path" "Release candidate inspection verification must reject a missing safety actuator template field."
Test-FileContains "eng/verify-release-candidate-inspection.ps1" "configured-agent-safety-executable-path" "Release candidate inspection verification must reject a preconfigured release safety actuator path."
Test-FileContains "eng/verify-release-candidate-inspection.ps1" "missing-agent-package-cache-directory" "Release candidate inspection verification must reject a missing content-cache template field."
Test-FileContains "eng/verify-release-candidate-inspection.ps1" "configured-agent-package-cache-directory" "Release candidate inspection verification must reject a preconfigured machine content-cache path."
Test-FileContains "eng/verify-release-candidate-inspection.ps1" "missing-agent-windows-service-name" "Release candidate inspection verification must reject a missing deployment-time service-name template field."
Test-FileContains "eng/verify-release-candidate-inspection.ps1" "configured-agent-windows-service-name" "Release candidate inspection verification must reject a prebound release service name."
Test-FileContains "eng/stage-release-artifacts.ps1" "release-provenance.json" "Release staging must generate release provenance metadata."
Test-FileContains "eng/stage-release-artifacts.ps1" "Publish-WindowsSelfContainedProject" "Release staging must publish self-contained Windows Agent and Runner hosts."
Test-FileContains "eng/stage-release-artifacts.ps1" "OpenLineOps.StationRuntime.exe" "Release staging must bind Agent configuration to the co-packaged Station Runtime."
Test-FileContains "eng/stage-release-artifacts.ps1" "plugin-host" "Release staging must record the co-packaged Plugin Host entry point."
Test-FileContains "eng/stage-release-artifacts.ps1" "python-script-worker" "Release staging must record the co-packaged Python Script Worker entry point."
Test-FileContains "eng/stage-release-artifacts.ps1" "OpenLineOps.LeastPrivilegeLauncher.exe" "Release staging must bind the fixed co-packaged Least Privilege Launcher."
Test-FileContains "eng/stage-release-artifacts.ps1" "SafetyExecutablePath must be empty in the release template" "Release staging must preserve a machine-specific empty safety actuator template."
Test-FileContains "eng/stage-release-artifacts.ps1" "PackageCacheDirectory must be present and empty" "Release staging must preserve an explicit empty content-cache path for administrator provisioning."
Test-FileContains "eng/stage-release-artifacts.ps1" "WindowsServiceName must be present and empty" "Release staging must preserve an explicit empty Windows service name for deployment-time binding."
Test-FileContains "eng/inspect-release-candidate.ps1" "DEPLOYMENT.md is missing content-cache provisioning contract" "Release inspection must verify the packaged Station content-cache provisioning command."
Test-FileContains "docs/station-agent-deployment.md" "--provision-content-cache" "Station deployment docs must contain the administrator content-cache provisioning command."
Test-FileContains "docs/station-agent-deployment.md" "--remove-content-cache-package" "Station deployment docs must contain the protected-package removal command."
Test-FileContains "tests/OpenLineOps.Agent.Tests/StationAgentExecutableContractTests.cs" "AdministrativeContentCacheModesAreExposedByAgentExecutable" "The built Agent must expose both administrative content-cache modes."
Test-FileContains "eng/verify-staged-agent-bundle-e2e.ps1" "OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT" "Staged Agent E2E must force signed package execution through extracted release executables."
Test-FileContains "eng/verify-staged-agent-bundle-e2e.ps1" "OpenLineOps:WindowsServiceName must contain 1-80 ASCII letters, digits, periods, underscores, or hyphens" "The packaged Agent normal-startup probe must fail first on the undeployed Windows service identity."
Test-FileForbiddenPattern "eng/verify-staged-agent-bundle-e2e.ps1" "BrokerUri must include a dedicated non-guest username" "The packaged Agent normal-startup probe must not retain the pre-service-identity broker failure contract."
Test-FileContains "eng/verify-staged-agent-bundle-e2e.ps1" "Agent application layer -> staged StationRuntime -> staged PluginHost" "Staged Agent E2E must prove the signed plugin process chain."
Test-FileContains "eng/verify-staged-agent-bundle-e2e.ps1" "childTokenIsAppContainer" "Staged Agent E2E evidence must prove the Python child uses an AppContainer token."
Test-FileContains "eng/verify-staged-agent-bundle-e2e.ps1" "childAppContainerSid" "Staged Agent E2E evidence must record the Python child package SID."
Test-FileContains "eng/verify-staged-agent-bundle-e2e.ps1" "childIntegrityRid" "Staged Agent E2E evidence must prove the Python child uses Low Integrity."
Test-FileContains "eng/verify-staged-agent-rabbitmq-e2e.ps1" "invoke-run-scoped-agent-service-cleanup.ps1" "Staged Agent RabbitMQ wrapper must unconditionally invoke the shared bounded service scavenger."
Test-FileContains "eng/verify-studio-two-agent-production-closure.ps1" "invoke-run-scoped-agent-service-cleanup.ps1" "Studio two-Agent wrapper must invoke the shared bounded service scavenger before compensation."
Test-FileContains "eng/verify-runner-staged-agent-e2e.ps1" "invoke-run-scoped-agent-service-cleanup.ps1" "Runner staged-Agent wrapper must invoke the shared bounded service scavenger before compensation."
Test-FileContains "eng/invoke-run-scoped-agent-service-cleanup.ps1" "openlineops-agent-service-cleanup" "Agent service scavenger must use the single strict cleanup manifest schema."
Test-FileContains "eng/invoke-run-scoped-agent-service-cleanup.ps1" "serviceSidType" "Agent service cleanup manifest must retain the exact restricted service-SID contract."
Test-FileContains "eng/invoke-run-scoped-agent-service-cleanup.ps1" "Get-ServiceSidFromName" "Agent service cleanup manifest must derive its exact service SID independently from the service name."
Test-FileContains "eng/invoke-run-scoped-agent-service-cleanup.ps1" "Assert-RunScopeAbsent" "Absent cleanup manifests must still prove the exact run scope has no residue."
Test-FileContains "eng/invoke-run-scoped-agent-service-cleanup.ps1" "Get-ManifestPathKind" "Agent service cleanup must distinguish true absence from invalid manifest node types."
Test-FileContains "eng/invoke-run-scoped-agent-service-cleanup.ps1" "ordinary non-reparse file" "Agent service cleanup must fail closed on directory and reparse-point manifest paths."
Test-FileContains "eng/verify-release-staging-security.ps1" "cleanup-manifest-reparse-target" "Release security regression must behavior-test a reparse-point cleanup manifest mutation."
Test-FileContains "eng/invoke-run-scoped-agent-service-cleanup.ps1" "SourceExists" "Agent service cleanup preflight must reject a same-name EventLog source under any log."
Test-FileContains "eng/verify-runner-staged-agent-evidence.ps1" "serviceLifecycleVerified" "Runner staged-Agent evidence must prove the Agent SCM lifecycle."
Test-FileContains "eng/verify-runner-staged-agent-evidence.ps1" "serviceSidSha256" "Runner staged-Agent evidence must bind the restricted service SID without publishing the raw SID."
Test-FileContains "eng/verify-runner-staged-agent-evidence.ps1" "exactServiceSidRestricted" "Runner staged-Agent evidence must prove its exact service SID is restricted."
Test-FileContains "eng/verify-runner-staged-agent-evidence.ps1" "Get-ServiceSidFromName" "Runner staged-Agent evidence must independently derive the service SID hash from the SCM service name."
Test-FileContains "eng/verify-runner-staged-agent-evidence.tests.ps1" "agent-service-sid-hash-invalid" "Runner evidence regression must reject a well-formed service SID hash unrelated to the SCM service name."
Test-FileContains "eng/verify-agent-service-external-abort-cleanup.ps1" "OPENLINEOPS_AGENT_SERVICE_EXTERNAL_ABORT_GATE" "External-abort cleanup must behaviorally stop the testhost outside its normal finally path."
Test-FileContains "eng/verify-agent-service-external-abort-cleanup.ps1" "/T /F" "External-abort cleanup must terminate the complete testhost child tree before scavenging the SCM service."
Test-FileContains "apps/desktop/scripts/production-closure-e2e.mjs" "resetProductionClosureEvidence" "Packaged production closure E2E must remove stale run evidence before creating its single current summary."
Test-FileContains "eng/stage-release-artifacts.ps1" "ZipArchive\]::new" "Release staging must create ZIP archives with explicit canonical entry names."
Test-FileContains "eng/stage-release-artifacts.ps1" "non-canonical or out-of-order entry" "Release staging must verify every emitted ZIP entry path and order."
Test-FileContains "eng/verify-publication-readiness.ps1" "Get-AuthenticodeSignature" "Publication readiness must enforce shipped Windows executable signatures."
Test-FileContains "apps/desktop/package.json" "package:win:ci" "Desktop package.json must define a CI desktop packaging script."
Test-FileContains ".github/workflows/build.yml" "Verify Windows package signing readiness" "CI must verify shared Windows package signing readiness."
Test-FileContains "eng/stage-release-artifacts.ps1" "SignWindowsPackages" "Release staging must expose one formal Windows package signing switch."
Test-FileContains "eng/sign-windows-package.ps1" "signtool.exe" "Windows package signing script must use Windows signtool."
Test-FileContains "eng/sign-windows-package.ps1" "Update-DesktopPackageContentManifest" "Windows package signing must regenerate the Desktop content manifest after signing."
Test-FileContains "eng/verify-windows-signing-readiness.ps1" "missing certificate selector" "Windows package signing readiness verification must reject missing certificate selectors."
Test-FileContains "eng/verify-windows-signing-readiness.ps1" "removed post-signing manifest regeneration" "Windows package signing readiness must mutation-test mandatory manifest regeneration."
Test-FileContains "docs/release-packaging.md" "manifest regeneration failure fails the signing command" "Release docs must state that post-signing Desktop manifest regeneration is mandatory."
Test-FileContains "eng/finalize-publication-metadata.ps1" "SecurityContact" "Publication finalization script must accept a security contact."
Test-FileContains "eng/finalize-publication-metadata.ps1" "ConductContact" "Publication finalization script must accept a conduct contact."
Test-FileContains ".github/workflows/build.yml" "Verify publication metadata finalization" "CI must verify publication metadata finalization."
Test-FileContains "eng/verify-publication-metadata-finalization.ps1" "placeholder security contact" "Publication finalization verification must reject placeholder contacts."
Test-FileContains "docs/release-packaging.md" "release manifest/checksum verification" "Release packaging docs must describe manifest/checksum verification."
Test-FileContains "docs/release-packaging.md" "release-dependency-inventory.json" "Release packaging docs must describe dependency inventory metadata."
Test-FileContains "docs/release-packaging.md" "release-metadata-checksums.sha256" "Release packaging docs must describe metadata checksums."
Test-FileContains "docs/plugin-authoring.md" "manifest.json" "Plugin authoring docs must describe the package manifest."

Test-FileDoesNotContain "SECURITY.md" "When the GitHub repository is created|Until then" "SECURITY.md still references pre-publication vulnerability reporting."
Test-FileDoesNotContain "CODE_OF_CONDUCT.md" "Until the public GitHub project defines" "CODE_OF_CONDUCT.md still references pre-publication reporting fallback."
Test-FileDoesNotContain ".github/ISSUE_TEMPLATE/config.yml" "when the public repository is available" "Issue-template security contact still references future public repository availability."

Test-ReleaseArtifacts

foreach ($warning in $Warnings) {
    Write-Warning $warning
}

if ($Failures.Count -gt 0) {
    Write-Host "Publication readiness failed:" -ForegroundColor Red
    foreach ($failure in $Failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }

    exit 1
}

Write-Host "Publication readiness checks passed."
if ($Warnings.Count -gt 0) {
    Write-Host "Warnings: $($Warnings.Count)"
}
