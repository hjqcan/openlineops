param(
    [string[]] $NuGetAssetRoots = @("src", "modules", "shared", "tools", "samples", "tests"),

    [string] $DesktopPackageLock = "apps/desktop/package-lock.json",

    [string] $NoticePath = "THIRD-PARTY-NOTICES.md",

    [string] $InventoryPath = "",

    [string] $InventoryVersion = "0.0.0-local",

    [switch] $SkipNuGet,

    [switch] $SkipNpm,

    [switch] $UpdateNotice,

    [switch] $SkipNoticeCheck,

    [switch] $UpdateInventory,

    [switch] $SkipInventoryCheck
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Failures = New-Object System.Collections.Generic.List[string]
$ReviewPatterns = @(
    "(?i)\bAGPL\b",
    "(?i)\bGPL\b",
    "(?i)\bLGPL\b",
    "(?i)\bSSPL\b"
)

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

    if ($fullPath.Equals($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside the repository root: $fullPath"
    }
}

function Add-Failure {
    param([Parameter(Mandatory = $true)][string] $Message)
    $Failures.Add($Message) | Out-Null
}

function Test-LicenseRequiresReview {
    param([Parameter(Mandatory = $true)][string] $License)

    foreach ($pattern in $ReviewPatterns) {
        if ($License -match $pattern) {
            return $true
        }
    }

    return $false
}

function Compare-OrdinalPropertyValues {
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right,
        [Parameter(Mandatory = $true)][string[]] $Properties
    )

    foreach ($property in $Properties) {
        $comparison = [string]::Compare(
            [string] $Left.$property,
            [string] $Right.$property,
            [System.StringComparison]::Ordinal)
        if ($comparison -ne 0) {
            return $comparison
        }
    }

    return 0
}

function Sort-PackageMetadata {
    param(
        [Parameter(Mandatory = $true)][object[]] $Packages,
        [Parameter(Mandatory = $true)][string[]] $Properties
    )

    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($package in $Packages) {
        $items.Add($package)
    }

    $items.Sort([System.Comparison[object]] {
        param($left, $right)
        Compare-OrdinalPropertyValues -Left $left -Right $right -Properties $Properties
    })

    return $items.ToArray()
}

function Sort-OrdinalStrings {
    param([Parameter(Mandatory = $true)][string[]] $Values)

    $items = [System.Collections.Generic.List[string]]::new()
    foreach ($value in $Values) {
        $items.Add($value)
    }

    $items.Sort([System.Comparison[string]] {
        param($left, $right)
        [string]::Compare($left, $right, [System.StringComparison]::Ordinal)
    })

    return $items.ToArray()
}

function Escape-MarkdownTableCell {
    param([AllowNull()][string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    return $Value.Trim().Replace([char]13, " ").Replace([char]10, " ").Replace("|", "\|")
}

function Add-PackageTable {
    param(
        [Parameter(Mandatory = $true)]$Lines,
        [Parameter(Mandatory = $true)][string] $Title,
        [Parameter(Mandatory = $true)][object[]] $Packages
    )

    $Lines.Add("## $Title") | Out-Null
    $Lines.Add("") | Out-Null

    if ($Packages.Count -eq 0) {
        $Lines.Add("_No packages inspected._") | Out-Null
        $Lines.Add("") | Out-Null
        return
    }

    $Lines.Add("| Package | Version | License | Metadata Source |") | Out-Null
    $Lines.Add("| --- | --- | --- | --- |") | Out-Null
    foreach ($package in $Packages) {
        $source = if ([string]::IsNullOrWhiteSpace($package.LicenseSource)) { $package.Ecosystem } else { "$($package.Ecosystem):$($package.LicenseSource)" }
        $Lines.Add("| $(Escape-MarkdownTableCell $package.Name) | $(Escape-MarkdownTableCell $package.Version) | $(Escape-MarkdownTableCell $package.License) | $(Escape-MarkdownTableCell $source) |") | Out-Null
    }

    $Lines.Add("") | Out-Null
}

function Get-ThirdPartyNoticeContent {
    param([Parameter(Mandatory = $true)][object[]] $Packages)

    $nugetPackages = @(Sort-PackageMetadata `
        -Packages @($Packages | Where-Object { $_.Ecosystem -eq "nuget" }) `
        -Properties @("Name", "Version"))
    $npmPackages = @(Sort-PackageMetadata `
        -Packages @($Packages | Where-Object { $_.Ecosystem -eq "npm" }) `
        -Properties @("Name", "Version"))
    $licenseCount = @($Packages | ForEach-Object { $_.License } | Sort-Object -Unique).Count

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Third-Party Notices") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("This file is generated from local NuGet restore metadata and the Electron desktop package lock.") | Out-Null
    $lines.Add('Run `powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-third-party-license-metadata.ps1 -UpdateNotice` after dependency changes.') | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("Counts: NuGet $($nugetPackages.Count), NPM $($npmPackages.Count), unique license values $licenseCount.") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("## Review Policy") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("This notice is a release engineering aid, not legal advice. The verification gate fails on missing license metadata and on license values that require explicit release review, including GPL, LGPL, AGPL, and SSPL patterns.") | Out-Null
    $lines.Add("") | Out-Null

    Add-PackageTable -Lines $lines -Title "NuGet Packages" -Packages $nugetPackages
    Add-PackageTable -Lines $lines -Title "NPM Packages" -Packages $npmPackages

    return ($lines -join "`n") + "`n"
}

function Normalize-LineEndings {
    param([AllowNull()][string] $Value)

    if ($null -eq $Value) {
        return ""
    }

    return $Value -replace "`r`n|`r|`n", "`n"
}

function Test-IsNoticePackageRow {
    param([AllowNull()][string] $Line)

    return $Line -match "^\| " `
        -and $Line -notmatch "^\| Package \|" `
        -and $Line -notmatch "^\| --- \|"
}

function Normalize-ThirdPartyNoticeForComparison {
    param([AllowNull()][string] $Value)

    $lines = @((Normalize-LineEndings $Value).TrimEnd("`n").Split("`n"))
    $result = [System.Collections.Generic.List[string]]::new()
    $packageRows = [System.Collections.Generic.List[string]]::new()

    foreach ($line in $lines) {
        if (Test-IsNoticePackageRow $line) {
            $packageRows.Add($line)
            continue
        }

        if ($packageRows.Count -gt 0) {
            foreach ($packageRow in Sort-OrdinalStrings -Values $packageRows.ToArray()) {
                $result.Add($packageRow)
            }

            $packageRows.Clear()
        }

        $result.Add($line)
    }

    if ($packageRows.Count -gt 0) {
        foreach ($packageRow in Sort-OrdinalStrings -Values $packageRows.ToArray()) {
            $result.Add($packageRow)
        }
    }

    return ($result.ToArray() -join "`n") + "`n"
}

function Get-FirstContentDifference {
    param(
        [Parameter(Mandatory = $true)][string] $Actual,
        [Parameter(Mandatory = $true)][string] $Expected
    )

    $actualLines = @((Normalize-LineEndings $Actual).Split("`n"))
    $expectedLines = @((Normalize-LineEndings $Expected).Split("`n"))
    $maxLineCount = [Math]::Max($actualLines.Count, $expectedLines.Count)

    for ($index = 0; $index -lt $maxLineCount; $index++) {
        $actualLine = if ($index -lt $actualLines.Count) { $actualLines[$index] } else { "<missing>" }
        $expectedLine = if ($index -lt $expectedLines.Count) { $expectedLines[$index] } else { "<missing>" }
        if ($actualLine -ne $expectedLine) {
            return "First difference at line $($index + 1). Expected '$expectedLine' but found '$actualLine'."
        }
    }

    return "No line-level difference found."
}

function New-DependencyInventory {
    param([Parameter(Mandatory = $true)][object[]] $Packages)

    $nugetPackages = @($Packages | Where-Object { $_.Ecosystem -eq "nuget" })
    $npmPackages = @($Packages | Where-Object { $_.Ecosystem -eq "npm" })
    $licenseCount = @($Packages | ForEach-Object { $_.License } | Sort-Object -Unique).Count
    $inventoryPackages = @(Sort-PackageMetadata `
        -Packages $Packages `
        -Properties @("Ecosystem", "Name", "Version") |
        ForEach-Object {
            [ordered]@{
                ecosystem = $_.Ecosystem
                name = $_.Name
                version = $_.Version
                license = $_.License
                licenseSource = $_.LicenseSource
            }
        })

    return [ordered]@{
        schemaVersion = 1
        product = "OpenLineOps"
        version = $InventoryVersion
        generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
        packageCounts = [ordered]@{
            total = @($Packages).Count
            nuget = $nugetPackages.Count
            npm = $npmPackages.Count
            uniqueLicenseValues = $licenseCount
        }
        reviewPolicy = [ordered]@{
            blockedLicensePatterns = @("AGPL", "GPL", "LGPL", "SSPL")
        }
        packages = $inventoryPackages
    }
}

function Test-InventoryMatchesPackages {
    param(
        [Parameter(Mandatory = $true)]$Inventory,
        [Parameter(Mandatory = $true)][object[]] $Packages
    )

    if ($Inventory.schemaVersion -ne 1) {
        Add-Failure "Dependency inventory schemaVersion must be 1."
    }

    if ($Inventory.product -ne "OpenLineOps") {
        Add-Failure "Dependency inventory product must be OpenLineOps."
    }

    if ($Inventory.version -ne $InventoryVersion) {
        Add-Failure "Dependency inventory version '$($Inventory.version)' does not match expected '$InventoryVersion'."
    }

    $expectedPackages = @(Sort-PackageMetadata `
        -Packages $Packages `
        -Properties @("Ecosystem", "Name", "Version"))
    $actualPackages = @(Sort-PackageMetadata `
        -Packages $Inventory.packages `
        -Properties @("ecosystem", "name", "version"))
    if ($actualPackages.Count -ne $expectedPackages.Count) {
        Add-Failure "Dependency inventory package count $($actualPackages.Count) does not match inspected package count $($expectedPackages.Count)."
        return
    }

    for ($index = 0; $index -lt $expectedPackages.Count; $index++) {
        $expected = $expectedPackages[$index]
        $actual = $actualPackages[$index]
        foreach ($property in @(
                @("Ecosystem", "ecosystem"),
                @("Name", "name"),
                @("Version", "version"),
                @("License", "license"),
                @("LicenseSource", "licenseSource"))) {
            $expectedPropertyName = $property[0]
            $actualPropertyName = $property[1]
            $expectedValue = $expected.$expectedPropertyName
            $actualValue = $actual.$actualPropertyName
            if ($expectedValue -ne $actualValue) {
                Add-Failure "Dependency inventory package mismatch at index $index for $($property[1])."
                return
            }
        }
    }
}

function Update-Or-Test-DependencyInventory {
    param([Parameter(Mandatory = $true)][object[]] $Packages)

    if ($SkipInventoryCheck -or [string]::IsNullOrWhiteSpace($InventoryPath)) {
        return
    }

    if ($SkipNuGet -or $SkipNpm) {
        if ($UpdateInventory) {
            Add-Failure "Refusing to update $InventoryPath while NuGet or NPM package inspection is skipped."
        }
        else {
            Write-Host "Dependency inventory synchronization check skipped because one package ecosystem was skipped."
        }

        return
    }

    $resolvedInventoryPath = Resolve-RepoPath $InventoryPath
    $inventory = New-DependencyInventory -Packages $Packages

    if ($UpdateInventory) {
        Assert-UnderRepoRoot $resolvedInventoryPath
        $directory = Split-Path -Parent $resolvedInventoryPath
        if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory -PathType Container)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }

        [System.IO.File]::WriteAllText(
            $resolvedInventoryPath,
            (($inventory | ConvertTo-Json -Depth 8) + "`r`n"),
            [System.Text.UTF8Encoding]::new($false))
        Write-Host "Updated dependency inventory: $InventoryPath"
        return
    }

    if (-not (Test-Path -LiteralPath $resolvedInventoryPath -PathType Leaf)) {
        Add-Failure "Missing dependency inventory file: $InventoryPath. Run eng/verify-third-party-license-metadata.ps1 -InventoryPath $InventoryPath -UpdateInventory."
        return
    }

    $actualInventory = Get-Content -LiteralPath $resolvedInventoryPath -Raw | ConvertFrom-Json
    Test-InventoryMatchesPackages -Inventory $actualInventory -Packages $Packages
}

function Update-Or-Test-Notice {
    param([Parameter(Mandatory = $true)][object[]] $Packages)

    if ($SkipNoticeCheck) {
        return
    }

    if ($SkipNuGet -or $SkipNpm) {
        if ($UpdateNotice) {
            Add-Failure "Refusing to update $NoticePath while NuGet or NPM package inspection is skipped."
        }
        else {
            Write-Host "Third-party notice synchronization check skipped because one package ecosystem was skipped."
        }

        return
    }

    $resolvedNoticePath = Resolve-RepoPath $NoticePath
    $expectedContent = Get-ThirdPartyNoticeContent -Packages $Packages

    if ($UpdateNotice) {
        Assert-UnderRepoRoot $resolvedNoticePath
        $directory = Split-Path -Parent $resolvedNoticePath
        if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory -PathType Container)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }

        [System.IO.File]::WriteAllText($resolvedNoticePath, $expectedContent, [System.Text.UTF8Encoding]::new($false))
        Write-Host "Updated third-party notice: $NoticePath"
        return
    }

    if (-not (Test-Path -LiteralPath $resolvedNoticePath -PathType Leaf)) {
        Add-Failure "Missing third-party notice file: $NoticePath. Run eng/verify-third-party-license-metadata.ps1 -UpdateNotice."
        return
    }

    $actualContent = Get-Content -LiteralPath $resolvedNoticePath -Raw
    $actualComparableContent = Normalize-ThirdPartyNoticeForComparison $actualContent
    $expectedComparableContent = Normalize-ThirdPartyNoticeForComparison $expectedContent
    if ($actualComparableContent -ne $expectedComparableContent) {
        $difference = Get-FirstContentDifference -Actual $actualComparableContent -Expected $expectedComparableContent
        Add-Failure "Third-party notice file is out of date: $NoticePath. Run eng/verify-third-party-license-metadata.ps1 -UpdateNotice. $difference"
    }
}

function Get-NuGetPackages {
    $packages = @{}

    foreach ($root in $NuGetAssetRoots) {
        $resolvedRoot = Resolve-RepoPath $root
        if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
            continue
        }

        $assetFiles = @(Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Filter "project.assets.json" -File -ErrorAction SilentlyContinue)
        foreach ($assetFile in $assetFiles) {
            $assets = Get-Content -LiteralPath $assetFile.FullName -Raw | ConvertFrom-Json
            $packagesPath = $assets.project.restore.packagesPath
            if ([string]::IsNullOrWhiteSpace($packagesPath)) {
                continue
            }

            foreach ($library in @($assets.libraries.PSObject.Properties)) {
                if ($library.Value.type -ne "package") {
                    continue
                }

                $parts = $library.Name.Split("/")
                if ($parts.Length -ne 2) {
                    continue
                }

                $id = $parts[0]
                $version = $parts[1]
                $key = "$id@$version"
                if ($packages.ContainsKey($key)) {
                    continue
                }

                $nuspecName = @($library.Value.files | Where-Object { $_ -like "*.nuspec" } | Select-Object -First 1)
                $nuspecPath = $null
                if ($nuspecName.Count -gt 0) {
                    $nuspecPath = Join-Path $packagesPath (Join-Path $library.Value.path $nuspecName[0])
                }

                $packages[$key] = [pscustomobject]@{
                    Name = $id
                    Version = $version
                    NuspecPath = $nuspecPath
                    License = ""
                    LicenseSource = ""
                    Ecosystem = "nuget"
                }
            }
        }
    }

    foreach ($package in @($packages.Values)) {
        if ([string]::IsNullOrWhiteSpace($package.NuspecPath) -or -not (Test-Path -LiteralPath $package.NuspecPath -PathType Leaf)) {
            Add-Failure "NuGet package $($package.Name) $($package.Version) is missing local nuspec metadata."
            continue
        }

        [xml] $nuspec = Get-Content -LiteralPath $package.NuspecPath -Raw
        $metadata = @($nuspec.package.ChildNodes | Where-Object { $_.LocalName -eq "metadata" } | Select-Object -First 1)
        $licenseNode = @($metadata.ChildNodes | Where-Object { $_.LocalName -eq "license" } | Select-Object -First 1)
        $licenseUrlNode = @($metadata.ChildNodes | Where-Object { $_.LocalName -eq "licenseUrl" } | Select-Object -First 1)
        if ($licenseNode.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($licenseNode[0].InnerText)) {
            $package.License = $licenseNode[0].InnerText.Trim()
            $licenseType = $licenseNode[0].Attributes["type"]
            $package.LicenseSource = if ($licenseType -ne $null) { "license:$($licenseType.Value)" } else { "license" }
        }
        elseif ($licenseUrlNode.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($licenseUrlNode[0].InnerText)) {
            $package.License = $licenseUrlNode[0].InnerText.Trim()
            $package.LicenseSource = "licenseUrl"
        }
    }

    return @(Sort-PackageMetadata -Packages @($packages.Values) -Properties @("Name", "Version"))
}

function Get-NpmPackages {
    $resolvedPackageLock = Resolve-RepoPath $DesktopPackageLock
    if (-not (Test-Path -LiteralPath $resolvedPackageLock -PathType Leaf)) {
        Add-Failure "Missing desktop package lock: $DesktopPackageLock"
        return @()
    }

    $nodeCommand = Get-Command "node" -ErrorAction SilentlyContinue
    if ($nodeCommand -eq $null) {
        Add-Failure "Cannot verify NPM license metadata because node is not available."
        return @()
    }

    $script = @"
const fs = require('fs');
const lockPath = process.argv[1];
const lock = JSON.parse(fs.readFileSync(lockPath, 'utf8'));
const packages = Object.entries(lock.packages || {})
  .filter(([path]) => path.startsWith('node_modules/'))
  .map(([path, value]) => ({
    ecosystem: 'npm',
    name: path.replace(/^node_modules\//, ''),
    version: value.version || '',
    license: value.license || '',
    licenseSource: value.license ? 'package-lock' : '',
    path
  }))
  .sort((a, b) => {
    const left = a.name + '\u0000' + a.version;
    const right = b.name + '\u0000' + b.version;
    return left < right ? -1 : left > right ? 1 : 0;
  });
process.stdout.write(JSON.stringify(packages));
"@

    $output = & node -e $script $resolvedPackageLock
    if ($LASTEXITCODE -ne 0) {
        Add-Failure "Node failed while reading NPM license metadata."
        return @()
    }

    if ([string]::IsNullOrWhiteSpace($output)) {
        return @()
    }

    return @(($output | ConvertFrom-Json) | ForEach-Object {
        [pscustomobject]@{
            Name = $_.name
            Version = $_.version
            License = $_.license
            LicenseSource = $_.licenseSource
            Ecosystem = "npm"
        }
    })
}

$allPackages = @()
if (-not $SkipNuGet) {
    $allPackages += Get-NuGetPackages
}

if (-not $SkipNpm) {
    $allPackages += Get-NpmPackages
}

foreach ($package in $allPackages) {
    if ([string]::IsNullOrWhiteSpace($package.License)) {
        Add-Failure "$($package.Ecosystem) package $($package.Name) $($package.Version) is missing license metadata."
        continue
    }

    if (Test-LicenseRequiresReview -License $package.License) {
        Add-Failure "$($package.Ecosystem) package $($package.Name) $($package.Version) uses license '$($package.License)', which requires release review."
    }
}

if ($Failures.Count -eq 0) {
    Update-Or-Test-Notice -Packages $allPackages
    Update-Or-Test-DependencyInventory -Packages $allPackages
}

if ($Failures.Count -gt 0) {
    Write-Host "Third-party license metadata verification failed:" -ForegroundColor Red
    foreach ($failure in $Failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }

    exit 1
}

$nugetCount = @($allPackages | Where-Object { $_.Ecosystem -eq "nuget" }).Count
$npmCount = @($allPackages | Where-Object { $_.Ecosystem -eq "npm" }).Count
$licenseCount = @($allPackages | ForEach-Object { $_.License } | Sort-Object -Unique).Count

Write-Host "Third-party license metadata verification passed."
Write-Host "NuGet packages: $nugetCount"
Write-Host "NPM packages: $npmCount"
Write-Host "Unique license values: $licenseCount"
