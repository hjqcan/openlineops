param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("rabbitmq", "studio-two-agent")]
    [string] $Kind,

    [Parameter(Mandatory = $true)]
    [string] $Scope,

    [Parameter(Mandatory = $true)]
    [string] $AgentBundleRoot,

    [Parameter(Mandatory = $true)]
    [string] $ManifestPath,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $DotNetPath = "dotnet",

    [switch] $PrepareManifest,

    [switch] $PreserveManifest,

    [switch] $NoBuild,

    [switch] $NoRestore,

    [ValidateRange(30, 180)]
    [int] $CleanupTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$TestProject = Join-Path $RepoRoot "tests/OpenLineOps.Agent.Tests/OpenLineOps.Agent.Tests.csproj"
$CleanupExactTest = "OpenLineOps.Agent.Tests.StagedAgentRabbitMqProcessE2ETests.CleanupRunScopedWindowsAgentServicesAndIdentities"

function Assert-LowerHex {
    param(
        [Parameter(Mandatory = $true)][string] $Value,
        [Parameter(Mandatory = $true)][int] $Length,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ($Value.Length -ne $Length -or $Value -cnotmatch "^[0-9a-f]{$Length}$") {
        throw "$Name must contain exactly $Length lowercase hexadecimal characters."
    }
}

function Resolve-CanonicalDirectory {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ([string]::IsNullOrWhiteSpace($Path) `
        -or [char]::IsWhiteSpace($Path[0]) `
        -or [char]::IsWhiteSpace($Path[$Path.Length - 1]) `
        -or -not [System.IO.Path]::IsPathRooted($Path)) {
        throw "$Name must be a canonical absolute directory path."
    }
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if ($resolved -cne $Path -or -not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "$Name must identify an existing canonical absolute directory."
    }
    return $resolved
}

function Resolve-PrivateManifestPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path) `
        -or [char]::IsWhiteSpace($Path[0]) `
        -or [char]::IsWhiteSpace($Path[$Path.Length - 1]) `
        -or -not [System.IO.Path]::IsPathRooted($Path)) {
        throw "ManifestPath must be a canonical absolute file path."
    }
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if ($resolved -cne $Path -or [System.IO.Path]::GetFileName($resolved) -cne "$Kind-$Scope.json") {
        throw "ManifestPath must use the canonical run-scoped cleanup filename."
    }

    $allowedRoots = @([System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/'))
    if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
        $allowedRoots += [System.IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\', '/')
    }
    $allowedBases = @($allowedRoots | ForEach-Object {
            [System.IO.Path]::GetFullPath((Join-Path $_ "openlineops-agent-service-cleanup"))
        })
    $insideAllowedBase = @($allowedBases | Where-Object {
            [string]::Equals(
                [System.IO.Path]::GetDirectoryName($resolved),
                $_,
                [System.StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0
    if (-not $insideAllowedBase) {
        throw "ManifestPath must be a direct child of the deterministic private cleanup base."
    }
    return $resolved
}

function Assert-NoReparseAncestors {
    param([Parameter(Mandatory = $true)][string] $Path)

    $current = if (Test-Path -LiteralPath $Path) {
        Get-Item -LiteralPath $Path -Force
    }
    else {
        [System.IO.DirectoryInfo]::new([System.IO.Path]::GetDirectoryName($Path))
    }
    while ($null -ne $current) {
        if ($current.Exists `
            -and (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Run-scoped Agent cleanup path traverses a reparse point: $($current.FullName)"
        }
        $current = $current.Parent
    }
}

function Assert-NoReparseTree {
    param([Parameter(Mandatory = $true)][string] $Root)

    if (-not (Test-Path -LiteralPath $Root)) { return }
    $pending = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Run-scoped Agent cleanup refuses a reparse-point root."
    }
    if ($rootItem -isnot [System.IO.DirectoryInfo]) { return }
    $pending.Push($rootItem)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($item in @(Get-ChildItem -LiteralPath $directory.FullName -Force)) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Run-scoped Agent cleanup refuses a reparse point: $($item.FullName)"
            }
            if ($item -is [System.IO.DirectoryInfo]) {
                $pending.Push($item)
            }
        }
    }
}

function Remove-PrivateTree {
    param([Parameter(Mandatory = $true)][string] $Root)

    if (-not (Test-Path -LiteralPath $Root)) { return }
    Assert-NoReparseAncestors $Root
    Assert-NoReparseTree $Root
    Remove-Item -LiteralPath $Root -Recurse -Force
}

function Get-TextSha256 {
    param([Parameter(Mandatory = $true)][string] $Value)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-ExpectedManifest {
    param([Parameter(Mandatory = $true)][string] $ResolvedAgentBundleRoot)

    $agentPath = [System.IO.Path]::GetFullPath((Join-Path $ResolvedAgentBundleRoot "OpenLineOps.Agent.exe"))
    if ([System.IO.Path]::GetDirectoryName($agentPath) -cne $ResolvedAgentBundleRoot `
        -or -not (Test-Path -LiteralPath $agentPath -PathType Leaf)) {
        throw "AgentBundleRoot is missing its direct OpenLineOps.Agent.exe payload."
    }
    $agentSha256 = (Get-FileHash -LiteralPath $agentPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-LowerHex -Value $agentSha256 -Length 64 -Name "Agent executable SHA-256"

    $windowsTemp = [System.IO.Path]::GetFullPath((Join-Path $env:SystemRoot "Temp"))
    $ownedRoot = [System.IO.Path]::GetFullPath((Join-Path $windowsTemp $(
                if ($Kind -ceq "rabbitmq") {
                    "olo-staged-agent-rmq-$Scope"
                }
                else {
                    "olo-studio-two-agent-$Scope"
                })))
    $roles = if ($Kind -ceq "rabbitmq") { @("rabbitmq") } else { @("entry", "downstream") }
    $entries = @($roles | ForEach-Object {
            $role = $_
            $serviceSuffix = if ($Kind -ceq "rabbitmq") {
                $Scope
            }
            else {
                (Get-TextSha256 "$role-service`:$Scope").Substring(0, 32)
            }
            $accountSuffix = if ($Kind -ceq "rabbitmq") {
                $Scope
            }
            else {
                (Get-TextSha256 "$role-account`:$Scope").Substring(0, 32)
            }
            [ordered]@{
                role = $role
                serviceSuffix = $serviceSuffix
                accountSuffix = $accountSuffix
                serviceName = "OpenLineOpsAgentE2E-$serviceSuffix"
                accountName = "oloe2e$($accountSuffix.Substring(0, 10))"
                accountSid = $null
                executablePath = [System.IO.Path]::GetFullPath(
                    (Join-Path $ownedRoot "agent-bundle/OpenLineOps.Agent.exe"))
                executableSha256 = $agentSha256
                ownedRoot = $ownedRoot
            }
        })
    return [ordered]@{
        schema = "openlineops-agent-service-cleanup"
        schemaVersion = 1
        kind = $Kind
        scope = $Scope
        entries = $entries
    }
}

function Set-PrivateAcl {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][bool] $Directory
    )

    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentSid) {
        throw "The cleanup wrapper identity has no Windows SID."
    }
    $sids = @(
        [System.Security.Principal.SecurityIdentifier]::new("S-1-5-18"),
        [System.Security.Principal.SecurityIdentifier]::new("S-1-5-32-544"),
        $currentSid) | Sort-Object -Property Value -Unique
    $security = if ($Directory) {
        [System.Security.AccessControl.DirectorySecurity]::new()
    }
    else {
        [System.Security.AccessControl.FileSecurity]::new()
    }
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($currentSid)
    foreach ($sid in $sids) {
        $inheritance = if ($Directory) {
            [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor `
                [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
        }
        else {
            [System.Security.AccessControl.InheritanceFlags]::None
        }
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $security

    $readBack = Get-Acl -LiteralPath $Path
    $ownerSid = ([System.Security.Principal.NTAccount]$readBack.Owner).Translate(
        [System.Security.Principal.SecurityIdentifier])
    $allowedSidValues = @($sids | ForEach-Object { $_.Value })
    $rules = @($readBack.GetAccessRules(
            $true,
            $true,
            [System.Security.Principal.SecurityIdentifier]))
    if (-not $readBack.AreAccessRulesProtected `
        -or $ownerSid.Value -cne $currentSid.Value `
        -or @($rules | Where-Object {
                $_.IsInherited `
                    -or $_.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow `
                    -or $allowedSidValues -cnotcontains $_.IdentityReference.Value
            }).Count -ne 0) {
        throw "Run-scoped Agent cleanup ACL is not protected or contains an unexpected principal."
    }
    foreach ($sid in $sids) {
        if (@($rules | Where-Object {
                    $_.IdentityReference.Value -ceq $sid.Value `
                        -and ($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::FullControl) `
                            -eq [System.Security.AccessControl.FileSystemRights]::FullControl
                }).Count -eq 0) {
            throw "Run-scoped Agent cleanup ACL lacks FullControl for '$($sid.Value)'."
        }
    }
}

function Assert-RunScopeAbsent {
    param([Parameter(Mandatory = $true)] $Expected)

    $profileListPath = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"
    foreach ($entry in @($Expected.entries)) {
        if ($null -ne (Get-Service -Name $entry.serviceName -ErrorAction SilentlyContinue)) {
            throw "Run-scoped Agent service remains without a cleanup manifest: $($entry.serviceName)"
        }
        if ($null -ne (Get-LocalUser -Name $entry.accountName -ErrorAction SilentlyContinue)) {
            throw "Run-scoped Agent account remains without a cleanup manifest: $($entry.accountName)"
        }
        if (Test-Path -LiteralPath $entry.ownedRoot) {
            throw "Run-scoped Agent owned root remains without a cleanup manifest: $($entry.ownedRoot)"
        }
        $serviceRegistryPath = "Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\$($entry.serviceName)"
        if (Test-Path -LiteralPath $serviceRegistryPath) {
            throw "Run-scoped Agent service registry key remains without a cleanup manifest."
        }
        $eventSourcePath = "Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\Application\$($entry.serviceName)"
        if (Test-Path -LiteralPath $eventSourcePath) {
            throw "Run-scoped Agent EventLog source remains without a cleanup manifest."
        }
        if ([System.Diagnostics.EventLog]::SourceExists($entry.serviceName)) {
            throw "Run-scoped Agent EventLog source exists under a non-canonical log and cannot be adopted."
        }
        $defaultProfilePath = Join-Path (Join-Path $env:SystemDrive "Users") $entry.accountName
        if (Test-Path -LiteralPath $defaultProfilePath) {
            throw "Run-scoped Agent account profile remains without a cleanup manifest."
        }
        if (Test-Path -LiteralPath $profileListPath) {
            foreach ($profileKey in @(Get-ChildItem -LiteralPath $profileListPath -ErrorAction Stop)) {
                $profileImagePath = [string](Get-ItemProperty `
                        -LiteralPath $profileKey.PSPath `
                        -Name ProfileImagePath `
                        -ErrorAction SilentlyContinue).ProfileImagePath
                if (-not [string]::IsNullOrWhiteSpace($profileImagePath) `
                    -and [System.IO.Path]::GetFileName(
                        [System.Environment]::ExpandEnvironmentVariables($profileImagePath)) -ceq $entry.accountName) {
                    throw "Run-scoped Agent profile registry entry remains without a cleanup manifest."
                }
            }
        }
    }
}

function Assert-ManifestMatches {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Expected,
        [switch] $AllowRecordedAccountSid
    )

    $raw = Get-Content -LiteralPath $Path -Raw -Encoding utf8
    if ($raw -match '(?i)(?:amqp|amqps|postgres|http|https)://' `
        -or $raw -match '(?i)"(?:password|secret|token|credential)"\s*:') {
        throw "Agent service cleanup manifest contains forbidden secret-bearing data."
    }
    $actual = $raw | ConvertFrom-Json
    foreach ($entry in @($actual.entries)) {
        if ($AllowRecordedAccountSid) {
            if ($null -ne $entry.accountSid `
                -and (([string]$entry.accountSid -cnotmatch '^S-1-(?:[0-9]+-)+[0-9]+$') `
                    -or [System.Security.Principal.SecurityIdentifier]::new(
                        [string]$entry.accountSid).Value -cne [string]$entry.accountSid)) {
                throw "Agent service cleanup manifest contains a non-canonical recorded account SID."
            }
            $entry.accountSid = $null
        }
        elseif ($null -ne $entry.accountSid) {
            throw "Fresh Agent service cleanup manifest must not pre-authorize an account SID."
        }
    }
    $actualCompressed = ConvertTo-Json $actual -Depth 8 -Compress
    $expectedCompressed = ConvertTo-Json $Expected -Depth 8 -Compress
    if ($actualCompressed -cne $expectedCompressed) {
        throw "Agent service cleanup manifest differs from its deterministic strict contract."
    }
}

function Stop-ProcessTree {
    param([Parameter(Mandatory = $true)][int] $ProcessId)

    if ($null -eq (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { return }
    $taskKill = Join-Path $env:SystemRoot "System32/taskkill.exe"
    $output = @(& $taskKill /PID $ProcessId /T /F 2>&1)
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0 -and $null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
        throw "Run-scoped Agent cleanup process tree could not be terminated."
    }
}

function ConvertTo-NativeArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    if ($Value.Length -gt 0 -and $Value -cnotmatch '[\s"]') { return $Value }
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Assert-ExactPassedTrx {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Run-scoped Agent cleanup did not produce its private TRX."
    }
    [xml]$trx = Get-Content -LiteralPath $Path -Raw
    $definitions = @($trx.TestRun.TestDefinitions.UnitTest)
    $results = @($trx.TestRun.Results.UnitTestResult)
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($definitions.Count -ne 1 `
        -or $results.Count -ne 1 `
        -or $results[0].outcome -cne "Passed" `
        -or "$($definitions[0].TestMethod.className).$($definitions[0].TestMethod.name)" -cne $CleanupExactTest `
        -or [int]$counters.total -ne 1 `
        -or [int]$counters.executed -ne 1 `
        -or [int]$counters.passed -ne 1 `
        -or [int]$counters.failed -ne 0 `
        -or [int]$counters.notExecuted -ne 0) {
        throw "Run-scoped Agent cleanup TRX does not prove exactly one Passed and zero skipped tests."
    }
}

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "Run-scoped Agent Windows service cleanup requires Windows."
}
Assert-LowerHex -Value $Scope -Length 32 -Name "Scope"
$resolvedManifestPath = Resolve-PrivateManifestPath $ManifestPath
$resolvedAgentBundleRoot = Resolve-CanonicalDirectory $AgentBundleRoot "AgentBundleRoot"
$expectedManifest = Get-ExpectedManifest -ResolvedAgentBundleRoot $resolvedAgentBundleRoot
Assert-NoReparseAncestors $resolvedManifestPath

if (-not $PrepareManifest -and -not (Test-Path -LiteralPath $resolvedManifestPath -PathType Leaf)) {
    Assert-RunScopeAbsent $expectedManifest
    Write-Host "Run-scoped Agent cleanup manifest is absent and its exact service scope is clean."
    return
}

if ($PrepareManifest) {
    if (Test-Path -LiteralPath $resolvedManifestPath) {
        throw "Run-scoped Agent cleanup manifest already exists."
    }
    foreach ($entry in @($expectedManifest.entries)) {
        Assert-RunScopeAbsent ([ordered]@{ entries = @($entry) })
    }
    $manifestDirectory = Split-Path -Parent $resolvedManifestPath
    New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
    Assert-NoReparseAncestors $manifestDirectory
    Set-PrivateAcl -Path $manifestDirectory -Directory $true
    $temporaryManifestPath = Join-Path `
        $manifestDirectory `
        ".$Kind-$Scope-$([System.Guid]::NewGuid().ToString('N')).tmp"
    try {
        [System.IO.File]::WriteAllText(
            $temporaryManifestPath,
            ((ConvertTo-Json $expectedManifest -Depth 8) + "`r`n"),
            [System.Text.UTF8Encoding]::new($false))
        Set-PrivateAcl -Path $temporaryManifestPath -Directory $false
        Assert-ManifestMatches -Path $temporaryManifestPath -Expected $expectedManifest
        [System.IO.File]::Move($temporaryManifestPath, $resolvedManifestPath)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryManifestPath) {
            Remove-Item -LiteralPath $temporaryManifestPath -Force
        }
    }
    Assert-NoReparseAncestors $resolvedManifestPath
    Assert-ManifestMatches -Path $resolvedManifestPath -Expected $expectedManifest
    Write-Host "Prepared strict run-scoped Agent service cleanup manifest: $resolvedManifestPath"
    return
}

Assert-ManifestMatches `
    -Path $resolvedManifestPath `
    -Expected $expectedManifest `
    -AllowRecordedAccountSid
Assert-NoReparseAncestors $resolvedManifestPath
$cleanupRoot = Join-Path `
    (Split-Path -Parent $resolvedManifestPath) `
    "cleanup-$Kind-$Scope-$([System.Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $cleanupRoot | Out-Null
Set-PrivateAcl -Path $cleanupRoot -Directory $true
$stdoutPath = Join-Path $cleanupRoot "cleanup.stdout.log"
$stderrPath = Join-Path $cleanupRoot "cleanup.stderr.log"
$trxName = "cleanup.trx"
$trxPath = Join-Path $cleanupRoot $trxName
$arguments = @(
    "test",
    $TestProject,
    "--configuration",
    $Configuration,
    "--filter",
    "FullyQualifiedName=$CleanupExactTest",
    "--results-directory",
    $cleanupRoot,
    "--logger",
    "trx;LogFileName=$trxName",
    "--logger",
    "console;verbosity=minimal")
if ($NoBuild) { $arguments += "--no-build" }
if ($NoRestore) { $arguments += "--no-restore" }
$argumentText = (($arguments | ForEach-Object {
            ConvertTo-NativeArgument ([string]$_)
        }) -join ' ')

$previousGate = $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_GATE
$previousManifest = $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_MANIFEST_PATH
$previousScope = $env:OPENLINEOPS_STAGED_AGENT_SERVICE_SCOPE
$previousStudioScope = $env:OPENLINEOPS_STUDIO_TWO_AGENT_ACCOUNT_SUFFIX
$previousCliLanguage = $env:DOTNET_CLI_UI_LANGUAGE
$previousVsLanguage = $env:VSLANG
$process = $null
try {
    $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_GATE = "true"
    $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_MANIFEST_PATH = $resolvedManifestPath
    $env:OPENLINEOPS_STAGED_AGENT_SERVICE_SCOPE = $Scope
    if ($Kind -ceq "studio-two-agent") {
        $env:OPENLINEOPS_STUDIO_TWO_AGENT_ACCOUNT_SUFFIX = $Scope
    }
    $env:DOTNET_CLI_UI_LANGUAGE = "en-US"
    $env:VSLANG = "1033"
    $process = Start-Process `
        -FilePath $DotNetPath `
        -ArgumentList $argumentText `
        -WorkingDirectory $RepoRoot `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru
    [void]$process.Handle
    if (-not $process.WaitForExit([int]($CleanupTimeoutSeconds * 1000))) {
        Stop-ProcessTree $process.Id
        throw "Run-scoped Agent cleanup exceeded its bounded timeout."
    }
    $process.WaitForExit()
    $process.Refresh()
    if ([int]$process.ExitCode -ne 0) {
        throw "Run-scoped Agent cleanup Fact failed with exit code $($process.ExitCode); private logs remain at $cleanupRoot."
    }
    Assert-ExactPassedTrx $trxPath
    if (-not (Test-Path -LiteralPath $resolvedManifestPath -PathType Leaf)) {
        throw "Run-scoped Agent cleanup Fact removed its authorization manifest prematurely."
    }
    if (-not $PreserveManifest) {
        Remove-Item -LiteralPath $resolvedManifestPath -Force
    }
    Remove-PrivateTree $cleanupRoot
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            Stop-ProcessTree $process.Id
            [void]$process.WaitForExit(15000)
        }
        $process.Dispose()
    }
    $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_GATE = $previousGate
    $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_MANIFEST_PATH = $previousManifest
    $env:OPENLINEOPS_STAGED_AGENT_SERVICE_SCOPE = $previousScope
    $env:OPENLINEOPS_STUDIO_TWO_AGENT_ACCOUNT_SUFFIX = $previousStudioScope
    $env:DOTNET_CLI_UI_LANGUAGE = $previousCliLanguage
    $env:VSLANG = $previousVsLanguage
}

Write-Host "Run-scoped Agent Windows service cleanup passed for $Kind scope $Scope."
