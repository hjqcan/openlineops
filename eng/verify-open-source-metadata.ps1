param()

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Failures = New-Object System.Collections.Generic.List[string]

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Add-Failure {
    param([Parameter(Mandatory = $true)][string] $Message)
    $Failures.Add($Message) | Out-Null
}

function Test-RequiredFile {
    param([Parameter(Mandatory = $true)][string] $Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        Add-Failure "Missing required metadata file: $Path"
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

function Test-FileRawMatches {
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

    $content = Get-Content -LiteralPath $resolved -Raw
    if ($content -notmatch $Pattern) {
        Add-Failure $Message
    }
}

function Test-JsonPropertyEquals {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]] $PropertyPath,
        [Parameter(Mandatory = $true)][string] $ExpectedValue
    )

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        Add-Failure "Cannot inspect missing JSON file: $Path"
        return
    }

    $node = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json
    foreach ($property in $PropertyPath) {
        if (-not ($node.PSObject.Properties.Name -contains $property)) {
            Add-Failure "$Path is missing JSON property '$($PropertyPath -join ".")'."
            return
        }

        $node = $node.$property
    }

    if ($node -ne $ExpectedValue) {
        Add-Failure "$Path property '$($PropertyPath -join ".")' must be '$ExpectedValue', found '$node'."
    }
}

function Test-DirectoryBuildProperty {
    param(
        [Parameter(Mandatory = $true)][xml] $Xml,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $ExpectedValue
    )

    $values = @(
        $Xml.Project.PropertyGroup |
            ForEach-Object { $_.$Name } |
            Where-Object { $_ -ne $null } |
            ForEach-Object { $_.InnerText } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($values.Count -eq 0) {
        Add-Failure "Directory.Build.props is missing <$Name>."
        return
    }

    if ($values[0] -ne $ExpectedValue) {
        Add-Failure "Directory.Build.props <$Name> must be '$ExpectedValue', found '$($values[0])'."
    }
}

foreach ($requiredFile in @(
    "Directory.Build.props",
    "LICENSE",
    "README.md",
    "apps/desktop/package.json",
    "apps/desktop/package-lock.json")) {
    Test-RequiredFile $requiredFile | Out-Null
}

$trackedOrUntrackedPackageLocks = @(
    & git -C $RepoRoot ls-files --cached --others --exclude-standard -- "*package-lock.json" |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Where-Object { Test-Path -LiteralPath (Resolve-RepoPath $_) -PathType Leaf } |
        ForEach-Object { $_.Replace('\', '/') } |
        Sort-Object -Unique
)
if ($LASTEXITCODE -ne 0) {
    Add-Failure "Unable to enumerate package-lock.json files with git."
}
$expectedPackageLocks = @("apps/desktop/package-lock.json")
if (($trackedOrUntrackedPackageLocks -join "`n") -cne ($expectedPackageLocks -join "`n")) {
    Add-Failure (
        "Every package-lock.json must have an explicit CI audit; expected " +
        "'$($expectedPackageLocks -join ', ')', found '$($trackedOrUntrackedPackageLocks -join ', ')'.")
}

$directoryBuildPropsPath = Resolve-RepoPath "Directory.Build.props"
if (Test-Path -LiteralPath $directoryBuildPropsPath -PathType Leaf) {
    [xml] $directoryBuildProps = Get-Content -LiteralPath $directoryBuildPropsPath -Raw
    Test-DirectoryBuildProperty -Xml $directoryBuildProps -Name "Product" -ExpectedValue "OpenLineOps"
    Test-DirectoryBuildProperty -Xml $directoryBuildProps -Name "Authors" -ExpectedValue "OpenLineOps contributors"
    Test-DirectoryBuildProperty -Xml $directoryBuildProps -Name "Company" -ExpectedValue "OpenLineOps"
    Test-DirectoryBuildProperty -Xml $directoryBuildProps -Name "PackageLicenseExpression" -ExpectedValue "MIT"
    Test-DirectoryBuildProperty -Xml $directoryBuildProps -Name "PackageRequireLicenseAcceptance" -ExpectedValue "false"
    Test-DirectoryBuildProperty -Xml $directoryBuildProps -Name "RepositoryType" -ExpectedValue "git"
}

Test-FileContains "LICENSE" "^MIT License" "Root LICENSE must contain MIT License text."
Test-FileContains "README.md" "OpenLineOps is licensed under the MIT License" "README.md must state the MIT license."
Test-JsonPropertyEquals -Path "apps/desktop/package.json" -PropertyPath @("license") -ExpectedValue "MIT"
Test-FileRawMatches `
    -Path "apps/desktop/package-lock.json" `
    -Pattern '(?s)"packages"\s*:\s*\{\s*""\s*:\s*\{\s*"name"\s*:\s*"@openlineops/desktop",\s*"version"\s*:\s*"0\.1\.0",\s*"license"\s*:\s*"MIT"' `
    -Message "apps/desktop/package-lock.json root package must declare MIT license."

if ($Failures.Count -gt 0) {
    Write-Host "Open-source metadata verification failed:" -ForegroundColor Red
    foreach ($failure in $Failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }

    exit 1
}

Write-Host "Open-source metadata verification passed."
