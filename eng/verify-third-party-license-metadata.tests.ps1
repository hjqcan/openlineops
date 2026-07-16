param(
    [string] $WorkRoot = "output/third-party-license-metadata-tests"
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RepoRootPrefix = $RepoRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
$Verifier = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "verify-third-party-license-metadata.ps1"))
$ResolvedWorkRoot = if ([System.IO.Path]::IsPathRooted($WorkRoot)) {
    [System.IO.Path]::GetFullPath($WorkRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $WorkRoot))
}

if (-not $ResolvedWorkRoot.StartsWith(
        $RepoRootPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Third-party license metadata test output must remain inside the repository."
}
if (-not (Test-Path -LiteralPath $Verifier -PathType Leaf)) {
    throw "Third-party license metadata verifier does not exist: $Verifier"
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Content
    )

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        [System.Text.UTF8Encoding]::new($false))
}

function Write-PackageMetadata {
    param(
        [Parameter(Mandatory = $true)][string] $PackagesRoot,
        [Parameter(Mandatory = $true)][string] $PackageId
    )

    $normalizedId = $PackageId.ToLowerInvariant()
    $packageDirectory = Join-Path $PackagesRoot (Join-Path $normalizedId "1.0.0")
    $nuspecPath = Join-Path $packageDirectory "$normalizedId.nuspec"
    $nuspec = @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>$PackageId</id>
    <version>1.0.0</version>
    <authors>OpenLineOps fixture</authors>
    <license type="expression">MIT</license>
    <description>Deterministic third-party metadata verifier fixture.</description>
  </metadata>
</package>
"@
    Write-Utf8NoBom -Path $nuspecPath -Content $nuspec
}

function Write-ProjectAssets {
    param(
        [Parameter(Mandatory = $true)][string] $ProjectRoot,
        [Parameter(Mandatory = $true)][string] $PackagesRoot,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Frameworks
    )

    $allPackageIds = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    $targetDocuments = [ordered]@{}
    $frameworkDocuments = [ordered]@{}

    foreach ($framework in $Frameworks.GetEnumerator()) {
        $dependencies = [ordered]@{}
        $targetPackages = [ordered]@{}
        foreach ($dependency in $framework.Value.GetEnumerator()) {
            $allPackageIds.Add([string] $dependency.Key) | Out-Null
            if ($dependency.Value -cne "transitive") {
                $dependencyDocument = [ordered]@{
                    target = "Package"
                    version = "[1.0.0, )"
                }
                if ($dependency.Value -ceq "auto") {
                    $dependencyDocument.autoReferenced = $true
                }

                $dependencies[[string] $dependency.Key] = $dependencyDocument
            }

            $targetPackages["$($dependency.Key)/1.0.0"] = [ordered]@{
                type = "package"
            }
        }

        $targetName = [string] $framework.Key
        $projectFrameworkName = $targetName.Split("/")[0]
        $targetDocuments[$targetName] = $targetPackages
        $frameworkDocuments[$projectFrameworkName] = [ordered]@{
            targetAlias = $projectFrameworkName
            dependencies = $dependencies
        }
    }

    $libraries = [ordered]@{}
    foreach ($packageId in @($allPackageIds | Sort-Object)) {
        $normalizedId = $packageId.ToLowerInvariant()
        Write-PackageMetadata -PackagesRoot $PackagesRoot -PackageId $packageId
        $libraries["$packageId/1.0.0"] = [ordered]@{
            type = "package"
            path = "$normalizedId/1.0.0"
            files = @("$normalizedId.nuspec")
        }
    }

    $assets = [ordered]@{
        version = 3
        targets = $targetDocuments
        libraries = $libraries
        project = [ordered]@{
            restore = [ordered]@{
                packagesPath = $PackagesRoot
            }
            frameworks = $frameworkDocuments
        }
    }

    Write-Utf8NoBom `
        -Path (Join-Path $ProjectRoot "obj/project.assets.json") `
        -Content (($assets | ConvertTo-Json -Depth 20) + "`n")
    Write-Utf8NoBom `
        -Path (Join-Path $ProjectRoot "$(Split-Path -Leaf $ProjectRoot).csproj") `
        -Content '<Project Sdk="Microsoft.NET.Sdk" />'
}

function Write-Solution {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][object[]] $Projects
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("Microsoft Visual Studio Solution File, Format Version 12.00") | Out-Null
    foreach ($project in $Projects) {
        $projectGuid = [Guid]::NewGuid().ToString("B").ToUpperInvariant()
        $relativePath = "$($project.Name)\$($project.Name).csproj"
        $lines.Add(
            "Project(`"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}`") = `"$($project.Name)`", `"$relativePath`", `"$projectGuid`")") | Out-Null
        $lines.Add("EndProject") | Out-Null
    }
    $lines.Add("Global") | Out-Null
    $lines.Add("EndGlobal") | Out-Null

    Write-Utf8NoBom -Path $Path -Content (($lines.ToArray() -join "`r`n") + "`r`n")
}

function Invoke-Case {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][object[]] $Projects,
        [Parameter(Mandatory = $true)][string[]] $ExpectedNuGetPackages
    )

    $caseRoot = Join-Path $ResolvedWorkRoot $Name
    $packagesRoot = Join-Path $caseRoot "packages"
    $solutionPath = Join-Path $caseRoot "Fixture.sln"
    $packageLockPath = Join-Path $caseRoot "package-lock.json"
    $inventoryPath = Join-Path $caseRoot "dependency-inventory.json"

    New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
    foreach ($project in $Projects) {
        Write-ProjectAssets `
            -ProjectRoot (Join-Path $caseRoot $project.Name) `
            -PackagesRoot $packagesRoot `
            -Frameworks $project.Frameworks
    }
    Write-Solution -Path $solutionPath -Projects $Projects
    Write-Utf8NoBom -Path $packageLockPath -Content @'
{
  "name": "openlineops-license-fixture",
  "version": "1.0.0",
  "lockfileVersion": 3,
  "requires": true,
  "packages": {
    "": {
      "name": "openlineops-license-fixture",
      "version": "1.0.0"
    }
  }
}
'@

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & powershell `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $Verifier `
            -SolutionPath $solutionPath `
            -DesktopPackageLock $packageLockPath `
            -SkipNoticeCheck `
            -InventoryPath $inventoryPath `
            -InventoryVersion "fixture" `
            -UpdateInventory 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $outputText = $output | Out-String
    if ($exitCode -ne 0) {
        Write-Host $outputText
        throw "Case '$Name' exited with $exitCode; expected 0."
    }
    if (-not (Test-Path -LiteralPath $inventoryPath -PathType Leaf)) {
        throw "Case '$Name' did not produce its dependency inventory."
    }

    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    $actualNuGetPackages = @(
        $inventory.packages |
            Where-Object { $_.ecosystem -ceq "nuget" } |
            ForEach-Object { $_.name } |
            Sort-Object)
    $expectedPackages = @($ExpectedNuGetPackages | Sort-Object)
    if (($actualNuGetPackages -join "`n") -cne ($expectedPackages -join "`n")) {
        throw "Case '$Name' produced NuGet packages '$($actualNuGetPackages -join ', ')'; expected '$($expectedPackages -join ', ')'."
    }
    if ($inventory.packageCounts.nuget -ne $expectedPackages.Count) {
        throw "Case '$Name' reported NuGet count $($inventory.packageCounts.nuget); expected $($expectedPackages.Count)."
    }
    if ($inventory.packageCounts.npm -ne 0) {
        throw "Case '$Name' unexpectedly reported NPM dependencies."
    }

    Write-Host "Case '$Name' passed: $($actualNuGetPackages -join ', ')"
}

$normalProjects = @(
    [pscustomobject]@{
        Name = "NormalApp"
        Frameworks = [ordered]@{
            "net10.0" = [ordered]@{
                "Fixture.Product" = "declared"
            }
        }
    })

$autoOnlyProjects = @(
    [pscustomobject]@{
        Name = "RidPublishApp"
        Frameworks = [ordered]@{
            "net10.0/win-x64" = [ordered]@{
                "Fixture.Product" = "declared"
                "Fixture.SdkTool" = "auto"
            }
        }
    })

$mixedFrameworkProjects = @(
    [pscustomobject]@{
        Name = "MultiTargetApp"
        Frameworks = [ordered]@{
            "net10.0" = [ordered]@{
                "Fixture.Product" = "declared"
                "Fixture.SdkTool" = "auto"
            }
            "net10.0-windows" = [ordered]@{
                "Fixture.Product" = "declared"
                "Fixture.SdkTool" = "declared"
            }
        }
    })

$crossProjectMixedProjects = @(
    [pscustomobject]@{
        Name = "SdkInjectedApp"
        Frameworks = [ordered]@{
            "net10.0/win-x64" = [ordered]@{
                "Fixture.Product" = "declared"
                "Fixture.SdkTool" = "auto"
            }
        }
    },
    [pscustomobject]@{
        Name = "DeclaredToolApp"
        Frameworks = [ordered]@{
            "net10.0" = [ordered]@{
                "Fixture.Product" = "declared"
                "Fixture.SdkTool" = "declared"
            }
        }
    })

$mixedTransitiveProjects = @(
    [pscustomobject]@{
        Name = "TransitiveTargetApp"
        Frameworks = [ordered]@{
            "net10.0" = [ordered]@{
                "Fixture.Product" = "declared"
                "Fixture.SdkTool" = "auto"
            }
            "net10.0-windows" = [ordered]@{
                "Fixture.Product" = "declared"
                "Fixture.SdkTool" = "transitive"
            }
        }
    })

try {
    if (Test-Path -LiteralPath $ResolvedWorkRoot) {
        Remove-Item -LiteralPath $ResolvedWorkRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $ResolvedWorkRoot -Force | Out-Null

    Invoke-Case `
        -Name "normal-restore" `
        -Projects $normalProjects `
        -ExpectedNuGetPackages @("Fixture.Product")
    Invoke-Case `
        -Name "rid-sdk-auto-only" `
        -Projects $autoOnlyProjects `
        -ExpectedNuGetPackages @("Fixture.Product")
    Invoke-Case `
        -Name "mixed-framework-reference" `
        -Projects $mixedFrameworkProjects `
        -ExpectedNuGetPackages @("Fixture.Product", "Fixture.SdkTool")
    Invoke-Case `
        -Name "mixed-project-reference" `
        -Projects $crossProjectMixedProjects `
        -ExpectedNuGetPackages @("Fixture.Product", "Fixture.SdkTool")
    Invoke-Case `
        -Name "mixed-transitive-reference" `
        -Projects $mixedTransitiveProjects `
        -ExpectedNuGetPackages @("Fixture.Product", "Fixture.SdkTool")
}
finally {
    if (Test-Path -LiteralPath $ResolvedWorkRoot) {
        Remove-Item -LiteralPath $ResolvedWorkRoot -Recurse -Force
    }
}

Write-Host "Third-party license metadata determinism regression tests passed."
