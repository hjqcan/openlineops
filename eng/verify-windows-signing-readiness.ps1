param(
    [string] $WorkRoot = "output/windows-signing-readiness",

    [switch] $SkipClean
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

function Invoke-SigningScript {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $SigningScript @Arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Invoke-ExpectedFailure {
    param(
        [Parameter(Mandatory = $true)][string] $Description,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][string] $ExpectedPattern
    )

    $result = Invoke-SigningScript -Arguments $Arguments
    if ($result.ExitCode -eq 0) {
        throw "Expected signing readiness failure for $Description, but the command succeeded."
    }

    if ($result.Output -notmatch $ExpectedPattern) {
        throw "Expected failure for $Description to match '$ExpectedPattern', but got: $($result.Output)"
    }
}

function Assert-OutputContains {
    param(
        [Parameter(Mandatory = $true)][string] $Output,
        [Parameter(Mandatory = $true)][string] $Expected,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if (-not $Output.Contains($Expected)) {
        throw "$Description did not contain expected text: $Expected"
    }
}

function Assert-OutputDoesNotContain {
    param(
        [Parameter(Mandatory = $true)][string] $Output,
        [Parameter(Mandatory = $true)][string] $Forbidden,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if ($Output.Contains($Forbidden)) {
        throw "$Description contained forbidden text: $Forbidden"
    }
}

function Get-ManifestContractFailure {
    param([Parameter(Mandatory = $true)][string] $Content)

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput(
        $Content,
        [ref] $tokens,
        [ref] $parseErrors)
    if ($parseErrors.Count -ne 0) {
        return "Signing script does not parse: $($parseErrors[0].Message)"
    }

    $manifestFunctions = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -ceq "Update-DesktopPackageContentManifest"
    }, $true))
    if ($manifestFunctions.Count -ne 1) {
        return "Signing script must define exactly one Desktop manifest updater."
    }

    $manifestFunction = $manifestFunctions[0]
    $manifestFunctionText = $manifestFunction.Extent.Text
    foreach ($requiredText in @(
        'resources/app',
        'write-package-content-manifest.mjs',
        '-PathType Container',
        '$LASTEXITCODE -ne 0',
        'Desktop package content manifest regeneration failed')) {
        if (-not $manifestFunctionText.Contains($requiredText)) {
            return "Desktop manifest updater is missing required contract text: $requiredText"
        }
    }

    $manifestConditionals = @($manifestFunction.Body.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.IfStatementAst]
    }, $true))
    if ($manifestConditionals.Count -ne 3) {
        return "Desktop manifest updater must not add a bypass conditional."
    }
    $manifestReturns = @($manifestFunction.Body.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.ReturnStatementAst]
    }, $true))
    if ($manifestReturns.Count -ne 1) {
        return "Desktop manifest updater may return only for a non-Desktop package root."
    }
    $returnAncestor = $manifestReturns[0].Parent
    while ($returnAncestor -ne $null -and
        $returnAncestor -isnot [System.Management.Automation.Language.IfStatementAst] -and
        $returnAncestor -isnot [System.Management.Automation.Language.ScriptBlockAst]) {
        $returnAncestor = $returnAncestor.Parent
    }
    if ($returnAncestor -isnot [System.Management.Automation.Language.IfStatementAst] -or
        -not $returnAncestor.Extent.Text.Contains('$resourcesApp') -or
        -not $returnAncestor.Extent.Text.Contains('-PathType Container')) {
        return "Desktop manifest updater return must be limited to a missing resources/app root."
    }

    $nodeCommands = @($manifestFunction.Body.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -ceq "node"
    }, $true))
    if ($nodeCommands.Count -ne 1 -or
        -not $nodeCommands[0].Extent.Text.Contains('$manifestWriter') -or
        -not $nodeCommands[0].Extent.Text.Contains('--package-root') -or
        -not $nodeCommands[0].Extent.Text.Contains('$PackageRoot')) {
        return "Desktop manifest updater must invoke the unique writer with --package-root."
    }

    $manifestInvocations = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -ceq "Update-DesktopPackageContentManifest"
    }, $true))
    if ($manifestInvocations.Count -ne 1) {
        return "Signing script must invoke the Desktop manifest updater exactly once."
    }
    $manifestInvocation = $manifestInvocations[0]
    if (-not $manifestInvocation.Extent.Text.Contains('-PackageRoot') -or
        -not $manifestInvocation.Extent.Text.Contains('$ResolvedPackageRoot')) {
        return "Desktop manifest updater must receive the resolved signed package root."
    }

    $ancestor = $manifestInvocation.Parent
    while ($ancestor -ne $null -and
        $ancestor -isnot [System.Management.Automation.Language.ScriptBlockAst]) {
        if ($ancestor -is [System.Management.Automation.Language.IfStatementAst] -or
            $ancestor -is [System.Management.Automation.Language.LoopStatementAst] -or
            $ancestor -is [System.Management.Automation.Language.TryStatementAst] -or
            $ancestor -is [System.Management.Automation.Language.TrapStatementAst] -or
            $ancestor -is [System.Management.Automation.Language.FunctionDefinitionAst]) {
            return "Desktop manifest regeneration must be one unconditional top-level post-signing command."
        }
        $ancestor = $ancestor.Parent
    }

    $signToolInvocations = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -ceq "Invoke-SignTool"
    }, $true))
    if ($signToolInvocations.Count -ne 2) {
        return "Signing script must keep distinct signing and verification invocations."
    }
    $lastSignToolOffset = ($signToolInvocations |
        ForEach-Object { $_.Extent.StartOffset } |
        Measure-Object -Maximum).Maximum
    if ($manifestInvocation.Extent.StartOffset -le $lastSignToolOffset) {
        return "Desktop manifest regeneration must run after signing and signature verification."
    }

    $planOnlyBlocks = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.IfStatementAst] -and
            $node.Extent.Text.Contains('Windows package signing plan only.')
    }, $true))
    if ($planOnlyBlocks.Count -ne 1 -or
        -not $planOnlyBlocks[0].Extent.Text.Contains('return') -or
        $planOnlyBlocks[0].Extent.EndOffset -ge $manifestInvocation.Extent.StartOffset) {
        return "PlanOnly must return before Desktop manifest regeneration."
    }

    $completionCommands = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -ceq "Write-Host" -and
            $node.Extent.Text.Contains('Windows package signing completed.')
    }, $true))
    if ($completionCommands.Count -ne 1 -or
        $completionCommands[0].Extent.StartOffset -le $manifestInvocation.Extent.StartOffset) {
        return "Signing completion must be reported only after Desktop manifest regeneration."
    }

    return $null
}

function Assert-ManifestContract {
    param(
        [Parameter(Mandatory = $true)][string] $Content,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $failure = Get-ManifestContractFailure -Content $Content
    if ($failure -ne $null) {
        throw "$Description failed: $failure"
    }
}

function Assert-ManifestContractMutationRejected {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Original,
        [Parameter(Mandatory = $true)][string] $Mutated
    )

    if ($Mutated -ceq $Original) {
        throw "Signing manifest mutation '$Name' did not change the source fixture."
    }
    $failure = Get-ManifestContractFailure -Content $Mutated
    if ($failure -eq $null) {
        throw "Signing manifest mutation '$Name' bypassed the static contract."
    }
}

$ResolvedWorkRoot = Resolve-RepoPath $WorkRoot
Assert-UnderRepoRoot $ResolvedWorkRoot
New-CleanDirectory $ResolvedWorkRoot

$SigningScript = Resolve-RepoPath "eng/sign-windows-package.ps1"
if (-not (Test-Path -LiteralPath $SigningScript -PathType Leaf)) {
    throw "Missing Windows package signing script: $SigningScript"
}

$signingScriptContent = Get-Content -LiteralPath $SigningScript -Raw
Assert-ManifestContract `
    -Content $signingScriptContent `
    -Description "Windows signing manifest contract"

$manifestInvocation = 'Update-DesktopPackageContentManifest -PackageRoot $ResolvedPackageRoot'
Assert-ManifestContractMutationRejected `
    -Name "removed post-signing manifest regeneration" `
    -Original $signingScriptContent `
    -Mutated $signingScriptContent.Replace(
        $manifestInvocation,
        'Write-Host "Desktop manifest regeneration removed"')
Assert-ManifestContractMutationRejected `
    -Name "manifest regeneration conditional on signature verification" `
    -Original $signingScriptContent `
    -Mutated $signingScriptContent.Replace(
        $manifestInvocation,
        "if (-not `$SkipVerify) {`r`n    $manifestInvocation`r`n}")
Assert-ManifestContractMutationRejected `
    -Name "manifest regeneration before signing verification" `
    -Original $signingScriptContent `
    -Mutated $signingScriptContent.Replace(
        $manifestInvocation,
        'Write-Host "Desktop manifest regeneration moved"').Replace(
            'if (-not $SkipVerify) {',
            "$manifestInvocation`r`n`r`nif (-not `$SkipVerify) {")
Assert-ManifestContractMutationRejected `
    -Name "PlanOnly mutates the Desktop manifest" `
    -Original $signingScriptContent `
    -Mutated $signingScriptContent.Replace(
        'Write-Host "Windows package signing plan only."',
        "$manifestInvocation`r`n    Write-Host `"Windows package signing plan only.`"")
Assert-ManifestContractMutationRejected `
    -Name "manifest writer loses canonical package-root argument" `
    -Original $signingScriptContent `
    -Mutated $signingScriptContent.Replace(
        '& node $manifestWriter --package-root $PackageRoot',
        '& node $manifestWriter $PackageRoot')
Assert-ManifestContractMutationRejected `
    -Name "manifest updater gains a SkipVerify bypass" `
    -Original $signingScriptContent `
    -Mutated $signingScriptContent.Replace(
        '& node $manifestWriter --package-root $PackageRoot',
        "if (`$SkipVerify) { return }`r`n    & node `$manifestWriter --package-root `$PackageRoot")

$packageRoot = Join-Path $ResolvedWorkRoot "win-unpacked"
$fixtureFiles = [ordered]@{
    "OpenLineOps.exe" = "desktop-executable"
    "resources/app/dist/index.html" = "desktop-renderer"
    "resources/app/dist-electron/main/main.js" = "desktop-main"
    "resources/app/dist-electron/preload/preload.cjs" = "desktop-preload"
    "resources/app/package.json" = '{"name":"@openlineops/desktop"}'
    "resources/app/runtime/api/OpenLineOps.Api.exe" = "api-executable"
    "resources/app/runtime/api/OpenLineOps.Api.dll" = "api-assembly"
    "resources/app/runtime/api/OpenLineOps.Api.deps.json" = "api-dependencies"
    "resources/app/runtime/api/OpenLineOps.Api.runtimeconfig.json" = "api-runtime-configuration"
    "resources/app/runtime/api/appsettings.json" = "api-settings"
    "resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.exe" = "worker-executable"
    "resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.dll" = "worker-assembly"
    "resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.deps.json" = "worker-dependencies"
    "resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.runtimeconfig.json" = "worker-runtime-configuration"
    "resources/app/runtime/plugin-host/OpenLineOps.PluginHost.exe" = "plugin-host-executable"
    "resources/app/runtime/plugin-host/OpenLineOps.PluginHost.dll" = "plugin-host-assembly"
    "resources/app/runtime/plugin-host/OpenLineOps.PluginHost.deps.json" = "plugin-host-dependencies"
    "resources/app/runtime/plugin-host/OpenLineOps.PluginHost.runtimeconfig.json" = "plugin-host-runtime-configuration"
    "resources/app/native/device.node" = "native-device"
    "resources/app/lib/helper.dll" = "native-helper"
    "resources/app/README.md" = "Not signable"
}
foreach ($fixtureFile in $fixtureFiles.GetEnumerator()) {
    $fixturePath = Join-Path $packageRoot $fixtureFile.Key
    New-Item -ItemType Directory -Path (Split-Path -Parent $fixturePath) -Force | Out-Null
    [System.IO.File]::WriteAllBytes(
        $fixturePath,
        [System.Text.Encoding]::UTF8.GetBytes($fixtureFile.Value))
}
$planOnlyManifestPath = Join-Path $packageRoot "openlineops-package-content.json"
$planOnlyManifestBytes = [System.Text.Encoding]::UTF8.GetBytes("plan-only-must-not-rewrite")
[System.IO.File]::WriteAllBytes($planOnlyManifestPath, $planOnlyManifestBytes)

Invoke-ExpectedFailure `
    -Description "missing certificate selector" `
    -Arguments @(
        "-PackageRoot",
        $packageRoot,
        "-PlanOnly") `
    -ExpectedPattern "Provide exactly one certificate selector"

Invoke-ExpectedFailure `
    -Description "multiple certificate selectors" `
    -Arguments @(
        "-PackageRoot",
        $packageRoot,
        "-CertificateThumbprint",
        "0123456789ABCDEF0123456789ABCDEF01234567",
        "-AutoSelectCertificate",
        "-PlanOnly") `
    -ExpectedPattern "Provide exactly one certificate selector"

$plan = Invoke-SigningScript -Arguments @(
    "-PackageRoot",
    $packageRoot,
    "-CertificateThumbprint",
    "0123456789ABCDEF0123456789ABCDEF01234567",
    "-PlanOnly")

if ($plan.ExitCode -ne 0) {
    throw "Expected Windows package signing plan to pass, but got exit code $($plan.ExitCode): $($plan.Output)"
}

Assert-OutputContains -Output $plan.Output -Expected "Windows package signing plan only." -Description "Signing plan"
Assert-OutputContains -Output $plan.Output -Expected "Files: 9" -Description "Signing plan"
Assert-OutputContains -Output $plan.Output -Expected "OpenLineOps.exe" -Description "Signing plan"
Assert-OutputContains -Output $plan.Output -Expected "resources/app/lib/helper.dll" -Description "Signing plan"
Assert-OutputContains -Output $plan.Output -Expected "resources/app/native/device.node" -Description "Signing plan"
Assert-OutputDoesNotContain -Output $plan.Output -Forbidden "README.md" -Description "Signing plan"
if (-not [System.Linq.Enumerable]::SequenceEqual(
    [byte[]] $planOnlyManifestBytes,
    [byte[]] [System.IO.File]::ReadAllBytes($planOnlyManifestPath))) {
    throw "PlanOnly changed the Desktop package content manifest."
}

$fakeSignTool = Join-Path $ResolvedWorkRoot "signtool.cmd"
Set-Content -LiteralPath $fakeSignTool -Value "@echo off`r`nexit /b 0" -Encoding ASCII
$signed = Invoke-SigningScript -Arguments @(
    "-PackageRoot",
    $packageRoot,
    "-SignToolPath",
    $fakeSignTool,
    "-CertificateThumbprint",
    "0123456789ABCDEF0123456789ABCDEF01234567")
if ($signed.ExitCode -ne 0) {
    throw "Expected simulated Desktop signing to pass: $($signed.Output)"
}
Assert-OutputContains `
    -Output $signed.Output `
    -Expected "Desktop package content manifest regenerated after signing." `
    -Description "Simulated Desktop signing"
$lastVerificationOffset = $signed.Output.LastIndexOf("Verifying signature ")
$manifestOffset = $signed.Output.IndexOf("Package content manifest written:")
$completionOffset = $signed.Output.IndexOf("Windows package signing completed.")
if ($lastVerificationOffset -lt 0 -or
    $manifestOffset -le $lastVerificationOffset -or
    $completionOffset -le $manifestOffset) {
    throw "Desktop manifest was not regenerated after successful signature verification."
}

function Assert-DesktopManifestMatchesPackage {
    param([Parameter(Mandatory = $true)][string] $Root)

    $manifestPath = Join-Path $Root "openlineops-package-content.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schema -cne "openlineops.desktop-package-content") {
        throw "Simulated signing emitted the wrong Desktop package manifest schema."
    }
    $expectedPaths = @(
        Get-ChildItem -LiteralPath $Root -Recurse -File |
            Where-Object { $_.FullName -cne $manifestPath } |
            ForEach-Object {
                $_.FullName.Substring($Root.TrimEnd('\', '/').Length + 1).Replace('\', '/')
            }
    )
    $manifestPaths = @($manifest.files | ForEach-Object { [string] $_.path })
    $remainingPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($expectedPath in $expectedPaths) {
        [void] $remainingPaths.Add($expectedPath)
    }
    foreach ($manifestEntryPath in $manifestPaths) {
        if (-not $remainingPaths.Remove($manifestEntryPath)) {
            throw "Simulated signing manifest contains an unexpected or duplicate path: $manifestEntryPath"
        }
    }
    if ($remainingPaths.Count -ne 0 -or $expectedPaths.Count -ne $manifestPaths.Count) {
        throw "Simulated signing manifest does not inventory the exact Desktop package."
    }
    foreach ($entry in $manifest.files) {
        $entryPath = Join-Path $Root ([string] $entry.path).Replace(
            '/',
            [System.IO.Path]::DirectorySeparatorChar)
        $file = Get-Item -LiteralPath $entryPath
        $sha256 = (Get-FileHash -LiteralPath $entryPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ([long] $entry.size -ne $file.Length -or [string] $entry.sha256 -cne $sha256) {
            throw "Simulated signing manifest hash or size mismatch: $($entry.path)"
        }
    }
}

Assert-DesktopManifestMatchesPackage -Root $packageRoot

[System.IO.File]::AppendAllText(
    (Join-Path $packageRoot "resources/app/lib/helper.dll"),
    "signed-again",
    [System.Text.Encoding]::UTF8)
$skipVerify = Invoke-SigningScript -Arguments @(
    "-PackageRoot",
    $packageRoot,
    "-SignToolPath",
    $fakeSignTool,
    "-CertificateThumbprint",
    "0123456789ABCDEF0123456789ABCDEF01234567",
    "-SkipVerify")
if ($skipVerify.ExitCode -ne 0) {
    throw "Expected simulated Desktop signing with SkipVerify to pass: $($skipVerify.Output)"
}
Assert-OutputDoesNotContain `
    -Output $skipVerify.Output `
    -Forbidden "Verifying signature " `
    -Description "SkipVerify signing"
Assert-OutputContains `
    -Output $skipVerify.Output `
    -Expected "Desktop package content manifest regenerated after signing." `
    -Description "SkipVerify signing"
Assert-DesktopManifestMatchesPackage -Root $packageRoot

Write-Host "Windows package signing readiness verification passed."
Write-Host "Package fixture: $packageRoot"
