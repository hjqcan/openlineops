param(
    [string] $AgentBundleRoot = $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT,

    [string] $SamplePluginRoot = $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT,

    [string] $ApiBundleRoot = $env:OPENLINEOPS_STAGED_API_BUNDLE_ROOT,

    [string] $BrokerUri = $env:OPENLINEOPS_RABBITMQ_URI,

    [string] $Scope = $env:OPENLINEOPS_STAGED_AGENT_SERVICE_SCOPE,

    [string] $ManifestPath = $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_MANIFEST_PATH,

    [string] $ReadyPath = $env:OPENLINEOPS_AGENT_SERVICE_EXTERNAL_ABORT_READY_PATH,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $DotNetPath = "dotnet",

    [switch] $NoBuild,

    [switch] $NoRestore,

    [ValidateRange(60, 300)]
    [int] $ReadyTimeoutSeconds = 180,

    [ValidateRange(30, 180)]
    [int] $CleanupTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$TestProject = Join-Path $RepoRoot "tests/OpenLineOps.Agent.Tests/OpenLineOps.Agent.Tests.csproj"
$ExactTest = "OpenLineOps.Agent.Tests.StagedAgentRabbitMqProcessE2ETests.StagedAgentBuffersSignedVendorResultDuringBrokerOutageAndDeduplicatesAcrossRestart"
$CleanupScript = Join-Path $PSScriptRoot "invoke-run-scoped-agent-service-cleanup.ps1"

function Resolve-CanonicalDirectory {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ([string]::IsNullOrWhiteSpace($Path) `
        -or -not [System.IO.Path]::IsPathRooted($Path)) {
        throw "$Name must be a canonical absolute directory."
    }
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if ($resolved -cne $Path -or -not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "$Name must identify an existing canonical absolute directory."
    }
    return $resolved
}

function Resolve-PrivatePath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $FileName
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or -not [System.IO.Path]::IsPathRooted($Path)) {
        throw "Private external-abort path must be canonical and absolute."
    }
    $resolved = [System.IO.Path]::GetFullPath($Path)
    $tempRoots = @([System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/'))
    if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
        $tempRoots += [System.IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\', '/')
    }
    $allowedParents = @($tempRoots | ForEach-Object {
            [System.IO.Path]::GetFullPath((Join-Path $_ "openlineops-agent-service-cleanup"))
        })
    if ($resolved -cne $Path `
        -or [System.IO.Path]::GetFileName($resolved) -cne $FileName `
        -or @($allowedParents | Where-Object {
                [string]::Equals(
                    [System.IO.Path]::GetDirectoryName($resolved),
                    $_,
                    [System.StringComparison]::OrdinalIgnoreCase)
            }).Count -eq 0) {
        throw "Private external-abort path is outside its deterministic cleanup base."
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
            throw "External-abort path traverses a reparse point: $($current.FullName)"
        }
        $current = $current.Parent
    }
}

function Assert-NoReparseTree {
    param([Parameter(Mandatory = $true)][string] $Root)

    if (-not (Test-Path -LiteralPath $Root)) { return }
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "External-abort cleanup refuses a reparse-point root."
    }
    if ($rootItem -isnot [System.IO.DirectoryInfo]) { return }
    $pending = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
    $pending.Push($rootItem)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($item in @(Get-ChildItem -LiteralPath $directory.FullName -Force)) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "External-abort cleanup refuses a reparse point: $($item.FullName)"
            }
            if ($item -is [System.IO.DirectoryInfo]) {
                $pending.Push($item)
            }
        }
    }
}

function Set-PrivateDirectoryAcl {
    param([Parameter(Mandatory = $true)][string] $Path)

    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentSid) { throw "External-abort wrapper identity has no SID." }
    $sids = @(
        [System.Security.Principal.SecurityIdentifier]::new("S-1-5-18"),
        [System.Security.Principal.SecurityIdentifier]::new("S-1-5-32-544"),
        $currentSid) | Sort-Object -Property Value -Unique
    $security = [System.Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($currentSid)
    foreach ($sid in $sids) {
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor `
                [System.Security.AccessControl.InheritanceFlags]::ObjectInherit,
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $security
    Assert-PrivateFileAcl $Path
}

function Remove-PrivateTree {
    param([Parameter(Mandatory = $true)][string] $Root)

    if (-not (Test-Path -LiteralPath $Root)) { return }
    Assert-NoReparseAncestors $Root
    Assert-NoReparseTree $Root
    Remove-Item -LiteralPath $Root -Recurse -Force
}

function Assert-PrivateFileAcl {
    param([Parameter(Mandatory = $true)][string] $Path)

    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentSid) { throw "External-abort wrapper identity has no SID." }
    $allowed = @(
        "S-1-5-18",
        "S-1-5-32-544",
        $currentSid.Value) | Sort-Object -Unique
    $security = Get-Acl -LiteralPath $Path
    $ownerSid = ([System.Security.Principal.NTAccount]$security.Owner).Translate(
        [System.Security.Principal.SecurityIdentifier])
    $rules = @($security.GetAccessRules(
            $true,
            $true,
            [System.Security.Principal.SecurityIdentifier]))
    if (-not $security.AreAccessRulesProtected `
        -or $ownerSid.Value -cne $currentSid.Value `
        -or @($rules | Where-Object {
                $_.IsInherited `
                    -or $_.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow `
                    -or $allowed -cnotcontains $_.IdentityReference.Value
            }).Count -ne 0) {
        throw "External-abort marker ACL is not protected or contains an unexpected principal."
    }
    foreach ($sid in $allowed) {
        if (@($rules | Where-Object {
                    $_.IdentityReference.Value -ceq $sid `
                        -and ($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::FullControl) `
                            -eq [System.Security.AccessControl.FileSystemRights]::FullControl
                }).Count -eq 0) {
            throw "External-abort marker ACL lacks FullControl for '$sid'."
        }
    }
}

function Stop-ProcessTree {
    param([Parameter(Mandatory = $true)][int] $ProcessId)

    if ($null -eq (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { return }
    $taskKill = Join-Path $env:SystemRoot "System32/taskkill.exe"
    @(& $taskKill /PID $ProcessId /T /F 2>&1) | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0 `
        -and $null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
        throw "External-abort dotnet process tree could not be terminated."
    }
}

function ConvertTo-NativeArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    if ($Value.Length -gt 0 -and $Value -cnotmatch '[\s"]') { return $Value }
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Wait-ProcessAbsent {
    param(
        [Parameter(Mandatory = $true)][int] $ProcessId,
        [Parameter(Mandatory = $true)][int] $TimeoutSeconds
    )

    $deadline = [System.DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) `
        -and [System.DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
    if ($null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
        throw "Process $ProcessId remained alive after its bounded external-abort wait."
    }
}

function Get-DescendantProcessIds {
    param([Parameter(Mandatory = $true)][int] $RootProcessId)

    $processes = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop)
    $descendants = [System.Collections.Generic.List[int]]::new()
    $pending = [System.Collections.Generic.Queue[int]]::new()
    $pending.Enqueue($RootProcessId)
    while ($pending.Count -gt 0) {
        $parentId = $pending.Dequeue()
        foreach ($child in @($processes | Where-Object {
                    [int]$_.ParentProcessId -eq $parentId
                })) {
            $childId = [int]$child.ProcessId
            if (-not $descendants.Contains($childId)) {
                $descendants.Add($childId)
                $pending.Enqueue($childId)
            }
        }
    }
    return @($descendants)
}

function Assert-ExactMarker {
    param(
        [Parameter(Mandatory = $true)] $Marker,
        [Parameter(Mandatory = $true)] $Entry
    )

    $properties = @($Marker.PSObject.Properties.Name)
    $expectedProperties = @(
        "schema",
        "schemaVersion",
        "scope",
        "serviceName",
        "accountName",
        "testHostProcessId",
        "agentProcessId",
        "executablePath",
        "executableSha256")
    if (($properties -join '|') -cne ($expectedProperties -join '|') `
        -or $Marker.schema -cne "openlineops-agent-service-external-abort-ready" `
        -or $Marker.schemaVersion -ne 1 `
        -or $Marker.scope -cne $Scope `
        -or $Marker.serviceName -cne $Entry.serviceName `
        -or $Marker.accountName -cne $Entry.accountName `
        -or $Marker.testHostProcessId -isnot [int] `
        -or $Marker.testHostProcessId -le 0 `
        -or $Marker.agentProcessId -isnot [int] `
        -or $Marker.agentProcessId -le 0 `
        -or $Marker.executablePath -cne $Entry.executablePath `
        -or $Marker.executableSha256 -cne $Entry.executableSha256) {
        throw "External-abort ready marker differs from its strict run-scoped manifest binding."
    }
}

function Assert-RunScopeGone {
    param(
        [Parameter(Mandatory = $true)] $Entry,
        [Parameter(Mandatory = $true)][int] $AgentProcessId,
        [Parameter(Mandatory = $true)][string] $AccountSid,
        [string] $ProfilePath
    )

    Wait-ProcessAbsent -ProcessId $AgentProcessId -TimeoutSeconds 15
    if ($null -ne (Get-Service -Name $Entry.serviceName -ErrorAction SilentlyContinue) `
        -or (Test-Path -LiteralPath "Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\$($Entry.serviceName)") `
        -or (Test-Path -LiteralPath "Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\Application\$($Entry.serviceName)") `
        -or [System.Diagnostics.EventLog]::SourceExists([string]$Entry.serviceName) `
        -or $null -ne (Get-LocalUser -Name $Entry.accountName -ErrorAction SilentlyContinue) `
        -or (Test-Path -LiteralPath "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$AccountSid") `
        -or (-not [string]::IsNullOrWhiteSpace($ProfilePath) -and (Test-Path -LiteralPath $ProfilePath)) `
        -or (Test-Path -LiteralPath $Entry.ownedRoot)) {
        throw "External-abort cleanup left a process, service, registry key, EventLog source, account, profile, or owned root."
    }

    $rightsPath = Join-Path `
        ([System.IO.Path]::GetDirectoryName($ReadyPath)) `
        "rights-$Scope-$([System.Guid]::NewGuid().ToString('N')).inf"
    try {
        $seceditOutput = @(& secedit.exe /export /cfg $rightsPath /areas USER_RIGHTS /quiet 2>&1)
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $rightsPath -PathType Leaf)) {
            throw "Could not export Local Security Policy for external-abort cleanup verification: $($seceditOutput -join ' ')"
        }
        $rightsText = Get-Content -LiteralPath $rightsPath -Raw
        if ($rightsText -match [regex]::Escape($AccountSid)) {
            throw "External-abort cleanup left the service account SID in Local Security Policy."
        }
    }
    finally {
        if (Test-Path -LiteralPath $rightsPath) {
            Remove-Item -LiteralPath $rightsPath -Force
        }
    }
}

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "External-abort Agent service cleanup verification requires Windows."
}
if ($Scope -cnotmatch '^[0-9a-f]{32}$') {
    throw "External-abort scope must contain exactly 32 lowercase hexadecimal characters."
}
$resolvedAgentRoot = Resolve-CanonicalDirectory $AgentBundleRoot "AgentBundleRoot"
$resolvedPluginRoot = Resolve-CanonicalDirectory $SamplePluginRoot "SamplePluginRoot"
$resolvedApiRoot = Resolve-CanonicalDirectory $ApiBundleRoot "ApiBundleRoot"
$broker = $null
if (-not [System.Uri]::TryCreate($BrokerUri, [System.UriKind]::Absolute, [ref]$broker) `
    -or $broker.Scheme -cnotin @("amqp", "amqps")) {
    throw "BrokerUri must be an absolute amqp or amqps URI."
}
$resolvedManifestPath = Resolve-PrivatePath $ManifestPath "rabbitmq-$Scope.json"
$resolvedReadyPath = Resolve-PrivatePath $ReadyPath "external-abort-ready-$Scope.json"
if (Test-Path -LiteralPath $resolvedReadyPath) {
    throw "External-abort ready marker must be reserved and absent before test launch."
}
Assert-NoReparseAncestors $resolvedManifestPath
Assert-NoReparseAncestors $resolvedReadyPath

$privateBase = [System.IO.Path]::GetDirectoryName($resolvedReadyPath)
$privateRoot = Join-Path $privateBase "external-abort-$Scope"
if (Test-Path -LiteralPath $privateRoot) {
    throw "External-abort private work root is not fresh."
}
New-Item -ItemType Directory -Path $privateRoot | Out-Null
Set-PrivateDirectoryAcl $privateRoot
$evidencePath = Join-Path $privateRoot "unexpected-evidence.json"
$stdoutPath = Join-Path $privateRoot "test.stdout.log"
$stderrPath = Join-Path $privateRoot "test.stderr.log"

$gateVariables = @(
    "OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT",
    "OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT",
    "OPENLINEOPS_STAGED_API_BUNDLE_ROOT",
    "OPENLINEOPS_RABBITMQ_URI",
    "OPENLINEOPS_STAGED_AGENT_RABBITMQ_EVIDENCE_PATH",
    "OPENLINEOPS_RABBITMQ_OUTAGE_CONTROL",
    "OPENLINEOPS_STAGED_AGENT_SERVICE_SCOPE",
    "OPENLINEOPS_AGENT_SERVICE_CLEANUP_GATE",
    "OPENLINEOPS_AGENT_SERVICE_CLEANUP_MANIFEST_PATH",
    "OPENLINEOPS_AGENT_SERVICE_EXTERNAL_ABORT_GATE",
    "OPENLINEOPS_AGENT_SERVICE_EXTERNAL_ABORT_READY_PATH",
    "DOTNET_CLI_UI_LANGUAGE",
    "VSLANG")
$previous = @{}
foreach ($name in $gateVariables) {
    $previous[$name] = [System.Environment]::GetEnvironmentVariable($name)
}

$testArguments = @(
    "test",
    $TestProject,
    "--configuration",
    $Configuration,
    "--filter",
    "FullyQualifiedName=$ExactTest",
    "--logger",
    "console;verbosity=minimal")
if ($NoBuild) { $testArguments += "--no-build" }
if ($NoRestore) { $testArguments += "--no-restore" }
$testArgumentText = (($testArguments | ForEach-Object {
            ConvertTo-NativeArgument ([string]$_)
        }) -join ' ')

$testProcess = $null
$primaryFailure = $null
$cleanupFailures = [System.Collections.Generic.List[System.Exception]]::new()
try {
    & $CleanupScript `
        -Kind rabbitmq `
        -Scope $Scope `
        -AgentBundleRoot $resolvedAgentRoot `
        -ManifestPath $resolvedManifestPath `
        -Configuration $Configuration `
        -DotNetPath $DotNetPath `
        -PrepareManifest `
        -NoBuild:$NoBuild `
        -NoRestore:$NoRestore `
        -CleanupTimeoutSeconds $CleanupTimeoutSeconds
    $manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json
    $entry = $manifest.entries[0]

    $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT = $resolvedAgentRoot
    $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT = $resolvedPluginRoot
    $env:OPENLINEOPS_STAGED_API_BUNDLE_ROOT = $resolvedApiRoot
    $env:OPENLINEOPS_RABBITMQ_URI = $broker.AbsoluteUri
    $env:OPENLINEOPS_STAGED_AGENT_RABBITMQ_EVIDENCE_PATH = $evidencePath
    $env:OPENLINEOPS_RABBITMQ_OUTAGE_CONTROL = "windows-service:RabbitMQ"
    $env:OPENLINEOPS_STAGED_AGENT_SERVICE_SCOPE = $Scope
    $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_GATE = $null
    $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_MANIFEST_PATH = $resolvedManifestPath
    $env:OPENLINEOPS_AGENT_SERVICE_EXTERNAL_ABORT_GATE = "true"
    $env:OPENLINEOPS_AGENT_SERVICE_EXTERNAL_ABORT_READY_PATH = $resolvedReadyPath
    $env:DOTNET_CLI_UI_LANGUAGE = "en-US"
    $env:VSLANG = "1033"

    $testProcess = Start-Process `
        -FilePath $DotNetPath `
        -ArgumentList $testArgumentText `
        -WorkingDirectory $RepoRoot `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru
    [void]$testProcess.Handle

    $deadline = [System.DateTimeOffset]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
    while (-not (Test-Path -LiteralPath $resolvedReadyPath -PathType Leaf) `
        -and -not $testProcess.HasExited `
        -and [System.DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path -LiteralPath $resolvedReadyPath -PathType Leaf)) {
        throw "External-abort Agent service did not produce its ready marker before the bounded deadline."
    }
    Assert-NoReparseAncestors $resolvedReadyPath
    Assert-PrivateFileAcl $resolvedReadyPath
    $marker = Get-Content -LiteralPath $resolvedReadyPath -Raw | ConvertFrom-Json
    Assert-ExactMarker -Marker $marker -Entry $entry
    if ([int]$marker.testHostProcessId -eq $testProcess.Id) {
        throw "External-abort marker must identify the child testhost, not the dotnet driver process."
    }

    $account = Get-LocalUser -Name $entry.accountName -ErrorAction Stop
    $accountSid = $account.SID.Value
    Assert-PrivateFileAcl $resolvedManifestPath
    $updatedManifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json
    if ($updatedManifest.entries.Count -ne 1 `
        -or $updatedManifest.entries[0].accountSid -cne $accountSid) {
        throw "External-abort cleanup manifest did not atomically bind the created account SID."
    }
    $profileRegistryPath = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$accountSid"
    $profilePath = if (Test-Path -LiteralPath $profileRegistryPath) {
        [System.Environment]::ExpandEnvironmentVariables(
            [string](Get-ItemProperty -LiteralPath $profileRegistryPath -Name ProfileImagePath).ProfileImagePath)
    }
    else {
        $null
    }
    $service = Get-CimInstance `
        -ClassName Win32_Service `
        -Filter "Name='$($entry.serviceName)'" `
        -ErrorAction Stop
    if ($service.State -cne "Running" `
        -or [int]$service.ProcessId -ne [int]$marker.agentProcessId `
        -or $null -eq (Get-Process -Id ([int]$marker.testHostProcessId) -ErrorAction SilentlyContinue) `
        -or $null -eq (Get-Process -Id ([int]$marker.agentProcessId) -ErrorAction SilentlyContinue)) {
        throw "External-abort ready marker does not bind a live Running SCM service and child testhost."
    }

    $driverTreeProcessIds = @($testProcess.Id) + @(
        Get-DescendantProcessIds -RootProcessId $testProcess.Id)
    if ($driverTreeProcessIds -notcontains [int]$marker.testHostProcessId) {
        throw "External-abort marker testhost is not a descendant of the dotnet test driver."
    }
    if ($driverTreeProcessIds -contains [int]$marker.agentProcessId) {
        throw "The SCM-hosted Agent must not be a descendant of the externally aborted dotnet test driver tree."
    }
    Stop-ProcessTree $testProcess.Id
    foreach ($abortedProcessId in $driverTreeProcessIds) {
        Wait-ProcessAbsent -ProcessId $abortedProcessId -TimeoutSeconds 15
    }
    [void]$testProcess.WaitForExit(15000)
    $serviceAfterAbort = Get-CimInstance `
        -ClassName Win32_Service `
        -Filter "Name='$($entry.serviceName)'" `
        -ErrorAction Stop
    if ($serviceAfterAbort.State -cne "Running" `
        -or [int]$serviceAfterAbort.ProcessId -ne [int]$marker.agentProcessId `
        -or $null -eq (Get-Process -Id ([int]$marker.agentProcessId) -ErrorAction SilentlyContinue)) {
        throw "External-abort did not leave the independently hosted Agent service running for scavenger proof."
    }

    foreach ($name in @(
            "OPENLINEOPS_AGENT_SERVICE_EXTERNAL_ABORT_GATE",
            "OPENLINEOPS_AGENT_SERVICE_EXTERNAL_ABORT_READY_PATH")) {
        [System.Environment]::SetEnvironmentVariable($name, $null)
    }
    & $CleanupScript `
        -Kind rabbitmq `
        -Scope $Scope `
        -AgentBundleRoot $resolvedAgentRoot `
        -ManifestPath $resolvedManifestPath `
        -Configuration $Configuration `
        -DotNetPath $DotNetPath `
        -PreserveManifest `
        -NoBuild:$NoBuild `
        -NoRestore:$NoRestore `
        -CleanupTimeoutSeconds $CleanupTimeoutSeconds
    Assert-RunScopeGone `
        -Entry $entry `
        -AgentProcessId ([int]$marker.agentProcessId) `
        -AccountSid $accountSid `
        -ProfilePath $profilePath
    & $CleanupScript `
        -Kind rabbitmq `
        -Scope $Scope `
        -AgentBundleRoot $resolvedAgentRoot `
        -ManifestPath $resolvedManifestPath `
        -Configuration $Configuration `
        -DotNetPath $DotNetPath `
        -NoBuild:$NoBuild `
        -NoRestore:$NoRestore `
        -CleanupTimeoutSeconds $CleanupTimeoutSeconds
}
catch {
    $primaryFailure = $_.Exception
}
finally {
    if ($null -ne $testProcess) {
        try {
            if (-not $testProcess.HasExited) {
                Stop-ProcessTree $testProcess.Id
                [void]$testProcess.WaitForExit(15000)
            }
        }
        catch { $cleanupFailures.Add($_.Exception) }
        $testProcess.Dispose()
    }
    foreach ($name in $gateVariables) {
        try { [System.Environment]::SetEnvironmentVariable($name, $previous[$name]) } catch { $cleanupFailures.Add($_.Exception) }
    }
    try {
        & $CleanupScript `
            -Kind rabbitmq `
            -Scope $Scope `
            -AgentBundleRoot $resolvedAgentRoot `
            -ManifestPath $resolvedManifestPath `
            -Configuration $Configuration `
            -DotNetPath $DotNetPath `
            -NoBuild:$NoBuild `
            -NoRestore:$NoRestore `
            -CleanupTimeoutSeconds $CleanupTimeoutSeconds
    }
    catch { $cleanupFailures.Add($_.Exception) }
    if ($null -eq $primaryFailure -and $cleanupFailures.Count -eq 0) {
        try {
            if (Test-Path -LiteralPath $resolvedReadyPath -PathType Leaf) {
                Assert-NoReparseAncestors $resolvedReadyPath
                Remove-Item -LiteralPath $resolvedReadyPath -Force
            }
        }
        catch { $cleanupFailures.Add($_.Exception) }
        try { Remove-PrivateTree $privateRoot } catch { $cleanupFailures.Add($_.Exception) }
    }
}

if ($null -ne $primaryFailure) {
    if ($cleanupFailures.Count -eq 0) { throw $primaryFailure }
    $failures = [System.Collections.Generic.List[System.Exception]]::new()
    $failures.Add($primaryFailure)
    foreach ($failure in $cleanupFailures) { $failures.Add($failure) }
    throw [System.AggregateException]::new(
        "External-abort Agent service proof failed and bounded cleanup was incomplete.",
        $failures.ToArray())
}
if ($cleanupFailures.Count -gt 0) {
    throw [System.AggregateException]::new(
        "External-abort Agent service proof cleanup was incomplete.",
        $cleanupFailures.ToArray())
}

Write-Host "External-abort Agent Windows service cleanup E2E passed for scope $Scope."
