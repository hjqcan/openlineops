param(
    [string[]] $TargetPaths = @(
        "OpenLineOps.sln",
        "lib/pythonscript/PythonScript/PythonScript.csproj",
        "lib/NetDevPack/src/NetDevPack/NetDevPack.csproj"),

    [string] $DotNetCommand = "dotnet"
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Resolve-TargetPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path) `
        -or [char]::IsWhiteSpace($Path[0]) `
        -or [char]::IsWhiteSpace($Path[$Path.Length - 1])) {
        throw "Package audit target must be canonical non-empty text."
    }

    $resolved = if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
    }
    $rootPrefix = $RepoRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) `
        -or -not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Package audit target must be an existing repository file: $resolved"
    }

    return $resolved
}

function Find-VulnerabilityCollections {
    param(
        [Parameter(Mandatory = $true)] $Node,
        [Parameter(Mandatory = $true)][string] $JsonPath
    )

    if ($null -eq $Node -or $Node -is [string] -or $Node.GetType().IsPrimitive) {
        return @()
    }

    if ($Node -is [System.Collections.IDictionary]) {
        $records = @()
        foreach ($key in $Node.Keys) {
            $value = $Node[$key]
            $childPath = "$JsonPath.$key"
            if ([string] $key -ceq "vulnerabilities" -and @($value).Count -gt 0) {
                $records += "$childPath ($(@($value).Count))"
            }
            elseif ($null -ne $value) {
                $records += @(Find-VulnerabilityCollections -Node $value -JsonPath $childPath)
            }
        }

        return $records
    }

    if ($Node -is [System.Collections.IEnumerable]) {
        $records = @()
        $index = 0
        foreach ($item in $Node) {
            if ($null -ne $item) {
                $records += @(Find-VulnerabilityCollections -Node $item -JsonPath "$JsonPath[$index]")
            }
            $index++
        }

        return $records
    }

    $objectRecords = @()
    foreach ($property in $Node.PSObject.Properties) {
        $childPath = "$JsonPath.$($property.Name)"
        if ($property.Name -ceq "vulnerabilities" -and @($property.Value).Count -gt 0) {
            $objectRecords += "$childPath ($(@($property.Value).Count))"
        }
        elseif ($null -ne $property.Value) {
            $objectRecords += @(
                Find-VulnerabilityCollections -Node $property.Value -JsonPath $childPath)
        }
    }

    return $objectRecords
}

if ([string]::IsNullOrWhiteSpace($DotNetCommand)) {
    throw "DotNetCommand is required."
}

$targets = @($TargetPaths | ForEach-Object { Resolve-TargetPath $_ })
if ($targets.Count -eq 0) {
    throw "At least one .NET package audit target is required."
}

$findings = New-Object System.Collections.Generic.List[string]
foreach ($target in $targets) {
    $arguments = @(
        "list",
        $target,
        "package",
        "--vulnerable",
        "--include-transitive",
        "--no-restore",
        "--format",
        "json")
    $output = & $DotNetCommand @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $text = (($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()
    if ($exitCode -ne 0) {
        throw "Package vulnerability audit command failed for '$target' with exit code ${exitCode}: $text"
    }

    try {
        $document = $text | ConvertFrom-Json
    }
    catch {
        throw "Package vulnerability audit returned invalid JSON for '$target': $($_.Exception.Message)"
    }

    if ($document.version -ne 1 -or $null -eq $document.projects) {
        throw "Package vulnerability audit returned an unsupported JSON contract for '$target'."
    }

    foreach ($record in @(Find-VulnerabilityCollections -Node $document -JsonPath '$')) {
        $findings.Add("$target -> $record") | Out-Null
    }
}

if ($findings.Count -gt 0) {
    throw "Vulnerable .NET packages were reported:`n - $($findings -join "`n - ")"
}

Write-Host ".NET package vulnerability audit passed for $($targets.Count) target(s)."
