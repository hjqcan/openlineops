$script:PublicationEvidenceCaseContract = @(
    [pscustomobject][ordered]@{
        Name = "default"
        RelativeDirectory = "c/d"
    },
    [pscustomobject][ordered]@{
        Name = "confirmed-proof"
        RelativeDirectory = "c/c"
    },
    [pscustomobject][ordered]@{
        Name = "invalid-production-integration-evidence"
        RelativeDirectory = "c/i"
    },
    [pscustomobject][ordered]@{
        Name = "invalid-production-integration-trx"
        RelativeDirectory = "c/t"
    },
    [pscustomobject][ordered]@{
        Name = "require-publishable"
        RelativeDirectory = "c/p"
    }
)

function Get-PublicationEvidenceCaseContract {
    $names = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    $directories = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)

    foreach ($case in $script:PublicationEvidenceCaseContract) {
        if ([string]::IsNullOrWhiteSpace([string] $case.Name) `
            -or [string]::IsNullOrWhiteSpace([string] $case.RelativeDirectory) `
            -or $case.RelativeDirectory -cnotmatch '^[a-z0-9]+/[a-z0-9]+$' `
            -or -not $names.Add([string] $case.Name) `
            -or -not $directories.Add([string] $case.RelativeDirectory)) {
            throw "Publication evidence case contract contains an invalid or duplicate entry."
        }
    }

    return @($script:PublicationEvidenceCaseContract)
}

function Get-PublicationEvidenceCaseRelativeDirectory {
    param([Parameter(Mandatory = $true)][string] $Name)

    $matches = @(Get-PublicationEvidenceCaseContract | Where-Object {
            $_.Name -ceq $Name
        })
    if ($matches.Count -ne 1) {
        throw "Publication evidence case '$Name' is not part of the formal case contract."
    }

    return [string] $matches[0].RelativeDirectory
}
