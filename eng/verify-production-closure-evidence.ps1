param(
    [string] $EvidenceRoot = "artifacts/production-closure-e2e",

    [switch] $RequirePassed
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$MaximumArchiveEntryCount = 4096
$MaximumArchiveEntryBytes = 64MB
$MaximumArchiveTotalBytes = 512MB
$MaximumPublicContentBytes = 64MB

function Resolve-EvidenceRoot {
    param([Parameter(Mandatory = $true)][string] $Path)

    $resolved = if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
    }
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "Production closure evidence root does not exist: $resolved"
    }
    return $resolved
}

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)][bool] $Condition,
        [Parameter(Mandatory = $true)][string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)][AllowNull()] $Value,
        [Parameter(Mandatory = $true)][string[]] $Expected,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if ($null -eq $Value) {
        throw "$Description is missing."
    }
    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Expected.Count) {
        throw "$Description must contain exactly: $($Expected -join ', ')."
    }
    foreach ($name in $Expected) {
        if ($actual -cnotcontains $name) {
            throw "$Description is missing '$name'."
        }
    }
}

function Get-SafeTree {
    param([Parameter(Mandatory = $true)][string] $Root)

    $files = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
    $directories = [System.Collections.Generic.List[System.IO.DirectoryInfo]]::new()
    $pending = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
    $pending.Push([System.IO.DirectoryInfo]::new($Root))
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        if (($directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Production closure evidence contains a reparse directory: $($directory.FullName)"
        }
        $directories.Add($directory)
        foreach ($entry in $directory.GetFileSystemInfos()) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Production closure evidence contains a reparse entry: $($entry.FullName)"
            }
            if ($entry -is [System.IO.DirectoryInfo]) {
                $pending.Push($entry)
            }
            elseif ($entry -is [System.IO.FileInfo]) {
                $files.Add($entry)
            }
            else {
                throw "Production closure evidence contains an unsupported filesystem entry: $($entry.FullName)"
            }
        }
    }
    return [pscustomobject]@{
        Files = @($files)
        Directories = @($directories)
    }
}

function Get-CanonicalRelativePath {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $resolvedRoot + [System.IO.Path]::DirectorySeparatorChar
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Production closure evidence path is outside its run root: $Path"
    }
    $relative = $resolvedPath.Substring($rootPrefix.Length).Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($relative) `
        -or [System.IO.Path]::IsPathRooted($relative) `
        -or $relative -eq ".." `
        -or $relative.StartsWith("../", [System.StringComparison]::Ordinal) `
        -or $relative.Split('/').Count -eq 0 `
        -or @($relative.Split('/') | Where-Object { $_ -in @("", ".", "..") }).Count -ne 0) {
        throw "Production closure evidence path is not canonical: $Path"
    }
    return $relative
}

function Test-AllowedEvidencePath {
    param([Parameter(Mandatory = $true)][string] $RelativePath)

    return $RelativePath -ceq "summary.json" `
        -or $RelativePath -cmatch '^screenshots/[A-Za-z0-9._-]+\.png$' `
        -or $RelativePath -cmatch '^verified-trace-artifact-saves/[A-Za-z0-9._-]+$' `
        -or $RelativePath -ceq "public-release/frozen-manifest.json" `
        -or $RelativePath -ceq "public-release/release-signing-public.pem" `
        -or $RelativePath -cmatch '^public-release/station-packages/[0-9a-f]{64}\.olopkg$' `
        -or $RelativePath -cmatch '^public-release/deployment-catalog/[0-9a-f]{64}\.json$'
}

function Assert-NoCredentialText {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Text,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $forbiddenPatterns = @(
        '-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----',
        '(?i)"(?:apiAccessToken|artifactUploadBearerToken|bearerToken|authorization|clientSecret|password)"\s*:',
        '(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}',
        '(?i)\bOPENLINEOPS_[A-Z0-9_]*(?:TOKEN|PASSWORD|SECRET)\s*[=:]',
        '(?im)(?:^|[;,])\s*(?:password|passphrase|client[_-]?secret|api[_-]?(?:key|token)|access[_-]?token|bearer[_-]?token|authorization)\s*[:=]\s*(?!(?:none|null|false|true|redacted|placeholder|changeme|example|sample|test|os\.getenv|process\.env|\$\{|\{\{|%[A-Z]))(?:["''][^"''\r\n]{4,}["'']|[A-Za-z0-9_~+/=-]{8,})(?:\s*(?:[;,#]|$))',
        '(?im)^\s*[A-Z0-9_]*(?:PASSWORD|PASSPHRASE|SECRET|TOKEN|API_KEY)\s*=\s*(?!(?:none|null|redacted|placeholder|changeme|example|sample|test|\$\{|\{\{|%[A-Z]))[A-Za-z0-9_~+/=-]{8,}\s*$',
        '(?i)(?:[A-Z]:)?[\\/][^\r\n"'']*(?:user-data|browser[-_ ]?profile)[\\/]',
        '(?i)[\\/](?:security|keys)[\\/][^\r\n"'']+\.(?:token|pem|pfx|p12)\b',
        '(?i)(?:amqp|amqps|http|https)://[^\s/:@]+:[^\s/@]+@',
        '(?i)(?<![A-Za-z0-9])[A-Z]:[\\/]',
        '(?i)(?<![\\])\\\\(?!u[0-9a-f]{4})(?:\?\\|\.\\)?[^\\/\s"'']+[\\/]',
        '(?i)\bfile:(?://|\\\\|[A-Z]:)',
        '(?i)(?<![A-Za-z0-9._-])/(?:applications|etc|home|media|mnt|opt|private|root|srv|tmp|usr/local|var(?:/tmp)?|volumes|workspace|workspaces|users)/'
    )
    foreach ($pattern in $forbiddenPatterns) {
        if ($Text -match $pattern) {
            throw "$Description contains forbidden credential, private-key, or absolute-path material."
        }
    }
}

function Test-DeclaredPublicText {
    param(
        [AllowNull()][string] $Path,
        [AllowNull()][string] $MediaType
    )

    if (-not [string]::IsNullOrWhiteSpace($MediaType) `
        -and ($MediaType -imatch '^text/' `
            -or $MediaType -imatch '^application/(?:[A-Za-z0-9.+-]+\+)?json(?:\s*;|$)' `
            -or $MediaType -imatch '^application/(?:[A-Za-z0-9.+-]+\+)?xml(?:\s*;|$)' `
            -or $MediaType -imatch '^application/(?:javascript|ecmascript|sql|toml|x-yaml|yaml)(?:\s*;|$)')) {
        return $true
    }
    return -not [string]::IsNullOrWhiteSpace($Path) `
        -and $Path -imatch '\.(?:bat|cfg|cmd|config|cs|css|csv|htm|html|ini|java|js|jsx|json|log|md|oloapp|ps1|py|rb|rs|sh|sql|toml|ts|tsx|txt|xml|yaml|yml)$'
}

function Test-PublicJsonContent {
    param(
        [AllowNull()][string] $Path,
        [AllowNull()][string] $MediaType
    )

    return (-not [string]::IsNullOrWhiteSpace($MediaType) `
            -and $MediaType -imatch '^application/(?:[A-Za-z0-9.+-]+\+)?json(?:\s*;|$)') `
        -or (-not [string]::IsNullOrWhiteSpace($Path) `
            -and $Path -imatch '\.(?:json|oloapp)$')
}

function Get-DecodablePublicText {
    param(
        [Parameter(Mandatory = $true)][byte[]] $Bytes,
        [Parameter(Mandatory = $true)][string] $Description,
        [bool] $DeclaredText = $false
    )

    $encoding = $null
    $offset = 0
    if ($Bytes.Length -ge 4 `
        -and $Bytes[0] -eq 0x00 -and $Bytes[1] -eq 0x00 `
        -and $Bytes[2] -eq 0xfe -and $Bytes[3] -eq 0xff) {
        $encoding = [System.Text.UTF32Encoding]::new($true, $true, $true)
        $offset = 4
    }
    elseif ($Bytes.Length -ge 4 `
        -and $Bytes[0] -eq 0xff -and $Bytes[1] -eq 0xfe `
        -and $Bytes[2] -eq 0x00 -and $Bytes[3] -eq 0x00) {
        $encoding = [System.Text.UTF32Encoding]::new($false, $true, $true)
        $offset = 4
    }
    elseif ($Bytes.Length -ge 3 `
        -and $Bytes[0] -eq 0xef -and $Bytes[1] -eq 0xbb -and $Bytes[2] -eq 0xbf) {
        $encoding = [System.Text.UTF8Encoding]::new($false, $true)
        $offset = 3
    }
    elseif ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xfe -and $Bytes[1] -eq 0xff) {
        $encoding = [System.Text.UnicodeEncoding]::new($true, $true, $true)
        $offset = 2
    }
    elseif ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xff -and $Bytes[1] -eq 0xfe) {
        $encoding = [System.Text.UnicodeEncoding]::new($false, $true, $true)
        $offset = 2
    }
    else {
        $encoding = [System.Text.UTF8Encoding]::new($false, $true)
    }

    try {
        $text = $encoding.GetString($Bytes, $offset, $Bytes.Length - $offset)
    }
    catch [System.Text.DecoderFallbackException] {
        if ($DeclaredText) {
            throw "$Description is declared as text but is not valid UTF-8/UTF-16/UTF-32."
        }
        return [pscustomobject]@{ IsText = $false; Text = $null }
    }

    foreach ($character in $text.ToCharArray()) {
        $codePoint = [int][char]$character
        if (($codePoint -lt 0x20 -and $character -notin @("`t", "`n", "`r", "`f")) `
            -or $codePoint -eq 0x7f) {
            if ($DeclaredText) {
                throw "$Description is declared as text but contains binary control characters."
            }
            return [pscustomobject]@{ IsText = $false; Text = $null }
        }
    }
    return [pscustomobject]@{ IsText = $true; Text = $text }
}

function Assert-PublicContentSafety {
    param(
        [Parameter(Mandatory = $true)][byte[]] $Bytes,
        [Parameter(Mandatory = $true)][string] $Description,
        [bool] $DeclaredText = $false,
        [bool] $Json = $false
    )

    $decoded = Get-DecodablePublicText `
        -Bytes $Bytes `
        -Description $Description `
        -DeclaredText $DeclaredText
    if (-not $decoded.IsText) {
        return $decoded
    }
    Assert-NoCredentialText -Text $decoded.Text -Description $Description
    if ($Json) {
        Assert-PublicJsonTextSafety -Text $decoded.Text -Description $Description | Out-Null
    }
    return $decoded
}

function Read-PublicFileBytes {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo] $File,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if ($File.Length -gt $MaximumPublicContentBytes) {
        throw "$Description exceeds the maximum inspectable public content size."
    }
    return ,([System.IO.File]::ReadAllBytes($File.FullName))
}

function Get-StreamSha256 {
    param([Parameter(Mandatory = $true)][System.IO.Stream] $Stream)

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString(
                $algorithm.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Read-ZipEntryBytes {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchiveEntry] $Entry,
        [Parameter(Mandatory = $true)][long] $MaximumBytes
    )

    if ($Entry.Length -gt $MaximumBytes) {
        throw "Station package entry '$($Entry.FullName)' exceeds the allowed size."
    }
    $stream = $Entry.Open()
    try {
        $memory = [System.IO.MemoryStream]::new()
        try {
            $stream.CopyTo($memory)
            return $memory.ToArray()
        }
        finally {
            $memory.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Read-DerLength {
    param(
        [Parameter(Mandatory = $true)][byte[]] $Bytes,
        [Parameter(Mandatory = $true)][ref] $Offset
    )

    if ($Offset.Value -ge $Bytes.Length) { throw "Release signing public key DER is truncated." }
    $first = [int]$Bytes[$Offset.Value]
    $Offset.Value++
    if (($first -band 0x80) -eq 0) { return $first }
    $count = $first -band 0x7f
    if ($count -lt 1 -or $count -gt 4 -or $Offset.Value + $count -gt $Bytes.Length) {
        throw "Release signing public key DER length is invalid."
    }
    [long]$length = 0
    for ($index = 0; $index -lt $count; $index++) {
        $length = ($length -shl 8) -bor [int]$Bytes[$Offset.Value]
        $Offset.Value++
    }
    if ($length -lt 128 -or $length -gt [int]::MaxValue) {
        throw "Release signing public key DER length is non-canonical."
    }
    return [int]$length
}

function Read-DerElement {
    param(
        [Parameter(Mandatory = $true)][byte[]] $Bytes,
        [Parameter(Mandatory = $true)][ref] $Offset,
        [Parameter(Mandatory = $true)][byte] $ExpectedTag
    )

    if ($Offset.Value -ge $Bytes.Length -or $Bytes[$Offset.Value] -ne $ExpectedTag) {
        throw "Release signing public key DER tag is invalid."
    }
    $Offset.Value++
    $length = Read-DerLength -Bytes $Bytes -Offset $Offset
    if ($Offset.Value + $length -gt $Bytes.Length) {
        throw "Release signing public key DER element is truncated."
    }
    $content = [byte[]]::new($length)
    [System.Array]::Copy($Bytes, $Offset.Value, $content, 0, $length)
    $Offset.Value += $length
    return ,$content
}

function Remove-DerIntegerSignPadding {
    param([Parameter(Mandatory = $true)][byte[]] $Bytes)
    if ($Bytes.Length -eq 0 -or ($Bytes[0] -band 0x80) -ne 0) {
        throw "Release signing public key RSA integer is invalid."
    }
    if ($Bytes.Length -gt 1 -and $Bytes[0] -eq 0) {
        if (($Bytes[1] -band 0x80) -eq 0) {
            throw "Release signing public key RSA integer is non-canonical."
        }
        $trimmed = [byte[]]::new($Bytes.Length - 1)
        [System.Array]::Copy($Bytes, 1, $trimmed, 0, $trimmed.Length)
        return ,$trimmed
    }
    return ,$Bytes
}

function Import-RsaPublicKeyPem {
    param([Parameter(Mandatory = $true)][string] $Path)

    $pem = [System.IO.File]::ReadAllText($Path)
    Assert-NoCredentialText -Text $pem -Description "Release signing public key"
    if (-not $pem.StartsWith("-----BEGIN PUBLIC KEY-----", [System.StringComparison]::Ordinal) `
        -or $pem.IndexOf("-----END PUBLIC KEY-----", [System.StringComparison]::Ordinal) -lt 0 `
        -or $pem.IndexOf("-----BEGIN PUBLIC KEY-----", 1, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Release signing public key must be one canonical PUBLIC KEY PEM block."
    }
    $base64 = ($pem.Replace("-----BEGIN PUBLIC KEY-----", "").Replace(
            "-----END PUBLIC KEY-----", "")) -replace '\s', ''
    try { [byte[]]$der = [System.Convert]::FromBase64String($base64) }
    catch { throw "Release signing public key PEM payload is invalid Base64." }

    $offset = 0
    [byte[]]$subjectPublicKeyInfo = Read-DerElement -Bytes $der -Offset ([ref]$offset) -ExpectedTag 0x30
    if ($offset -ne $der.Length) { throw "Release signing public key DER has trailing data." }
    $offset = 0
    [byte[]]$algorithm = Read-DerElement -Bytes $subjectPublicKeyInfo -Offset ([ref]$offset) -ExpectedTag 0x30
    if ([System.BitConverter]::ToString($algorithm).Replace('-', '').ToLowerInvariant() `
        -cne "06092a864886f70d0101010500") {
        throw "Release signing public key algorithm is not rsaEncryption."
    }
    [byte[]]$bitString = Read-DerElement -Bytes $subjectPublicKeyInfo -Offset ([ref]$offset) -ExpectedTag 0x03
    if ($offset -ne $subjectPublicKeyInfo.Length -or $bitString.Length -lt 2 -or $bitString[0] -ne 0) {
        throw "Release signing public key BIT STRING is invalid."
    }
    $rsaDer = [byte[]]::new($bitString.Length - 1)
    [System.Array]::Copy($bitString, 1, $rsaDer, 0, $rsaDer.Length)
    $offset = 0
    [byte[]]$rsaSequence = Read-DerElement -Bytes $rsaDer -Offset ([ref]$offset) -ExpectedTag 0x30
    if ($offset -ne $rsaDer.Length) { throw "Release signing public key RSA payload has trailing data." }
    $offset = 0
    [byte[]]$modulus = Remove-DerIntegerSignPadding `
        (Read-DerElement -Bytes $rsaSequence -Offset ([ref]$offset) -ExpectedTag 0x02)
    [byte[]]$exponent = Remove-DerIntegerSignPadding `
        (Read-DerElement -Bytes $rsaSequence -Offset ([ref]$offset) -ExpectedTag 0x02)
    if ($offset -ne $rsaSequence.Length -or $modulus.Length -lt 384 -or $exponent.Length -lt 1) {
        throw "Release signing public key RSA parameters are invalid."
    }
    $parameters = [System.Security.Cryptography.RSAParameters]::new()
    $parameters.Modulus = $modulus
    $parameters.Exponent = $exponent
    $rsa = [System.Security.Cryptography.RSACng]::new()
    try { $rsa.ImportParameters($parameters) }
    catch { $rsa.Dispose(); throw "Release signing public key RSA parameters could not be imported." }
    return $rsa
}

function Assert-StationPackageSignature {
    param(
        [Parameter(Mandatory = $true)][byte[]] $ManifestBytes,
        [Parameter(Mandatory = $true)][string] $SignatureText,
        [Parameter(Mandatory = $true)][string] $PublicKeyPath,
        [Parameter(Mandatory = $true)][string] $PublicKeySha256
    )

    try { $signatureDocument = $SignatureText | ConvertFrom-Json }
    catch { throw "Station package signature is invalid JSON." }
    Assert-ExactProperties $signatureDocument @("algorithm", "keyId", "signature") `
        "Station package signature"
    $expectedKeyId = "studio-" + $PublicKeySha256.Substring(0, 24)
    if ($signatureDocument.algorithm -cne "RSA-PSS-SHA256" `
        -or $signatureDocument.keyId -cne $expectedKeyId) {
        throw "Station package signature algorithm or public key identity is invalid."
    }
    try { [byte[]]$signature = [System.Convert]::FromBase64String([string]$signatureDocument.signature) }
    catch { throw "Station package signature value is invalid Base64." }
    $rsa = Import-RsaPublicKeyPem -Path $PublicKeyPath
    try {
        $valid = $rsa.VerifyData(
            $ManifestBytes,
            $signature,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pss)
    }
    finally { $rsa.Dispose() }
    if (-not $valid) { throw "Station package RSA-PSS signature verification failed." }
}

function Test-StationPackageReservedSegment {
    param([Parameter(Mandatory = $true)][string] $Segment)
    $dotIndex = $Segment.IndexOf('.')
    $stem = if ($dotIndex -ge 0) { $Segment.Substring(0, $dotIndex) } else { $Segment }
    if ($stem -ieq 'CON' -or $stem -ieq 'PRN' -or $stem -ieq 'AUX' -or $stem -ieq 'NUL') {
        return $true
    }
    if ($stem.Length -ne 4) {
        return $false
    }
    $prefix = $stem.Substring(0, 3)
    $suffix = $stem[3]
    return ($prefix -ieq 'COM' -or $prefix -ieq 'LPT') `
        -and ($suffix -in @('1', '2', '3', '4', '5', '6', '7', '8', '9',
                [char]0x00b9, [char]0x00b2, [char]0x00b3))
}

function Assert-CanonicalPackagePath {
    param([Parameter(Mandatory = $true)][string] $Path, [string] $Description)
    $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $invalid = [string]::IsNullOrWhiteSpace($Path) `
        -or $Path.Contains('\') `
        -or $Path.StartsWith('/', [System.StringComparison]::Ordinal) `
        -or $Path.EndsWith('/', [System.StringComparison]::Ordinal) `
        -or -not $Path.IsNormalized([System.Text.NormalizationForm]::FormC)
    if (-not $invalid) {
        try {
            $invalid = $strictUtf8.GetByteCount($Path) -gt 1024
        }
        catch [System.Text.EncoderFallbackException] {
            $invalid = $true
        }
    }
    if (-not $invalid) {
        foreach ($segment in $Path.Split('/')) {
            $invalid = $segment.Length -eq 0 `
                -or $segment -in @('.', '..') `
                -or [char]::IsWhiteSpace($segment[0]) `
                -or [char]::IsWhiteSpace($segment[$segment.Length - 1]) `
                -or $segment.EndsWith('.', [System.StringComparison]::Ordinal) `
                -or $strictUtf8.GetByteCount($segment) -gt 255 `
                -or $segment.IndexOfAny(':<>"|?*'.ToCharArray()) -ge 0 `
                -or @($segment.ToCharArray() | Where-Object { [char]::IsControl($_) }).Count -ne 0 `
                -or (Test-StationPackageReservedSegment $segment)
            if ($invalid) {
                break
            }
        }
    }
    if ($invalid) {
        throw "$Description is not a canonical Station package path."
    }
}

function Assert-CanonicalPackageText {
    param([AllowNull()][string] $Value, [string] $Description)
    if ([string]::IsNullOrWhiteSpace($Value) `
        -or $Value -cne $Value.Trim()) {
        throw "$Description must be canonical non-empty text."
    }
}

function Write-StationPackageCanonicalInt32 {
    param(
        [Parameter(Mandatory = $true)][System.IO.Stream] $Stream,
        [Parameter(Mandatory = $true)][int] $Value)
    [byte[]]$bytes = [System.BitConverter]::GetBytes($Value)
    if ([System.BitConverter]::IsLittleEndian) {
        [System.Array]::Reverse($bytes)
    }
    $Stream.Write($bytes, 0, $bytes.Length)
}

function Write-StationPackageCanonicalInt64 {
    param(
        [Parameter(Mandatory = $true)][System.IO.Stream] $Stream,
        [Parameter(Mandatory = $true)][long] $Value)
    [byte[]]$bytes = [System.BitConverter]::GetBytes($Value)
    if ([System.BitConverter]::IsLittleEndian) {
        [System.Array]::Reverse($bytes)
    }
    $Stream.Write($bytes, 0, $bytes.Length)
}

function Write-StationPackageCanonicalText {
    param(
        [Parameter(Mandatory = $true)][System.IO.Stream] $Stream,
        [Parameter(Mandatory = $true)][string] $Value)
    $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
    [byte[]]$bytes = $strictUtf8.GetBytes($Value)
    Write-StationPackageCanonicalInt32 -Stream $Stream -Value $bytes.Length
    $Stream.Write($bytes, 0, $bytes.Length)
}

function Get-StationPackageCanonicalContentSha256 {
    param([Parameter(Mandatory = $true)] $Manifest)
    [object[]]$entries = @($Manifest.entries)
    $entryComparer = [System.Collections.Generic.Comparer[object]]::Create(
        [System.Comparison[object]]{
            param($left, $right)
            return [System.StringComparer]::Ordinal.Compare(
                [string]$left.path,
                [string]$right.path)
        })
    [System.Array]::Sort($entries, $entryComparer)
    $stream = [System.IO.MemoryStream]::new()
    try {
        Write-StationPackageCanonicalText $stream "openlineops.station-package-content"
        foreach ($value in @(
                [string]$Manifest.projectId,
                [string]$Manifest.applicationId,
                [string]$Manifest.projectSnapshotId,
                [string]$Manifest.productionLineDefinitionId,
                [string]$Manifest.stationSystemId)) {
            Write-StationPackageCanonicalText $stream $value
        }
        Write-StationPackageCanonicalInt32 $stream $entries.Count
        foreach ($entry in $entries) {
            Write-StationPackageCanonicalText $stream ([string]$entry.path)
            Write-StationPackageCanonicalInt64 $stream ([long]$entry.length)
            Write-StationPackageCanonicalText $stream ([string]$entry.sha256)
            Write-StationPackageCanonicalText $stream ([string]$entry.mediaType)
        }
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([System.BitConverter]::ToString(
                    $algorithm.ComputeHash($stream.ToArray()))).Replace('-', '').ToLowerInvariant()
        }
        finally { $algorithm.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-DeploymentCatalogFileName {
    param(
        [string] $ProjectId,
        [string] $ApplicationId,
        [string] $ProjectSnapshotId,
        [string] $StationSystemId
    )
    $stream = [System.IO.MemoryStream]::new()
    try {
        Write-StationPackageCanonicalText $stream `
            "openlineops.station-package-deployment-catalog"
        foreach ($value in @($ProjectId, $ApplicationId, $ProjectSnapshotId, $StationSystemId)) {
            Write-StationPackageCanonicalText $stream $value
        }
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = $algorithm.ComputeHash($stream.ToArray())
            return ([System.BitConverter]::ToString($hash)).Replace(
                '-', '').ToLowerInvariant() + ".json"
        }
        finally { $algorithm.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Test-StationPackage {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $ExpectedPackage,
        [Parameter(Mandatory = $true)] $Summary,
        [Parameter(Mandatory = $true)][long] $FrozenManifestSizeBytes,
        [Parameter(Mandatory = $true)][string] $FrozenManifestSha256,
        [Parameter(Mandatory = $true)][System.DateTimeOffset] $FrozenPublishedAtUtc,
        [Parameter(Mandatory = $true)][string] $SigningPublicKeyPath,
        [Parameter(Mandatory = $true)][string] $SigningPublicKeySha256
    )

    Add-Type -AssemblyName System.IO.Compression
    $fileStream = [System.IO.File]::OpenRead($Path)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $fileStream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $false)
        try {
            $entries = @($archive.Entries)
            if ($entries.Count -lt 3 -or $entries.Count -gt $MaximumArchiveEntryCount) {
                throw "Station package must contain between 3 and $MaximumArchiveEntryCount entries: $Path"
            }
            $entryMap = [System.Collections.Generic.Dictionary[string,System.IO.Compression.ZipArchiveEntry]]::new(
                [System.StringComparer]::Ordinal)
            $archiveWindowsPaths = [System.Collections.Generic.HashSet[string]]::new(
                [System.StringComparer]::OrdinalIgnoreCase)
            [long] $totalBytes = 0
            foreach ($entry in $entries) {
                $entryPath = $entry.FullName
                Assert-CanonicalPackagePath $entryPath "Station package archive entry"
                if ([string]::IsNullOrWhiteSpace($entryPath) `
                    -or $entryPath.Contains('\') `
                    -or $entryPath.StartsWith('/', [System.StringComparison]::Ordinal) `
                    -or $entryPath.EndsWith('/', [System.StringComparison]::Ordinal) `
                    -or @($entryPath.Split('/') | Where-Object { $_ -in @("", ".", "..") }).Count -ne 0 `
                    -or $entryMap.ContainsKey($entryPath) `
                    -or -not $archiveWindowsPaths.Add($entryPath)) {
                    throw "Station package contains a duplicate, traversal, directory, or non-canonical entry: $entryPath"
                }
                $entryMap.Add($entryPath, $entry)
                if ($entry.Length -lt 0 -or $entry.Length -gt $MaximumArchiveEntryBytes) {
                    throw "Station package entry '$entryPath' exceeds the allowed size."
                }
                $totalBytes += [long]$entry.Length
                if ($totalBytes -gt $MaximumArchiveTotalBytes) {
                    throw "Station package uncompressed content exceeds the allowed total size."
                }
                if ($entryPath -imatch '(^|/)(user-data|security|keys)(/|$)' `
                    -or $entryPath -imatch '\.token$' `
                    -or $entryPath -imatch 'private[^/]*\.pem$' `
                    -or $entryPath -imatch '\.(pfx|p12)$') {
                    throw "Station package contains a forbidden sensitive path: $entryPath"
                }
            }

            foreach ($required in @("release.json", "package.manifest.json", "package.signature.json")) {
                if (-not $entryMap.ContainsKey($required)) {
                    throw "Station package is missing '$required'."
                }
            }
            $manifestBytes = Read-ZipEntryBytes `
                -Entry $entryMap["package.manifest.json"] `
                -MaximumBytes 1MB
            $manifestDecoded = Assert-PublicContentSafety `
                -Bytes $manifestBytes `
                -Description "Station package manifest" `
                -DeclaredText $true `
                -Json $true
            $manifestText = $manifestDecoded.Text
            $manifest = $manifestText | ConvertFrom-Json
            Assert-ExactProperties $manifest @(
                "format", "packageId", "projectId", "applicationId", "projectSnapshotId",
                "productionLineDefinitionId", "stationSystemId", "contentSha256",
                "createdAtUtc", "entries") "Station package manifest"
            $expectedContentSha256 = [string]$ExpectedPackage.packageContentSha256
            $expectedFileName = "$expectedContentSha256.olopkg"
            $expectedPackageId = @(
                [string]$Summary.projectId,
                [string]$Summary.applicationId,
                [string]$Summary.projectSnapshotId,
                [string]$ExpectedPackage.stationSystemId) -join '/'
            $createdAt = [System.DateTimeOffset]::MinValue
            if ($manifest.format -cne "openlineops.station-package" `
                -or $manifest.contentSha256 -cne $expectedContentSha256 `
                -or [System.IO.Path]::GetFileName($Path) -cne $expectedFileName `
                -or $manifest.packageId -cne $expectedPackageId `
                -or $manifest.projectId -cne $Summary.projectId `
                -or $manifest.applicationId -cne $Summary.applicationId `
                -or $manifest.projectSnapshotId -cne $Summary.projectSnapshotId `
                -or $manifest.productionLineDefinitionId -cne $Summary.productionLineDefinitionId `
                -or $manifest.stationSystemId -cne $ExpectedPackage.stationSystemId `
                -or -not [System.DateTimeOffset]::TryParse([string]$manifest.createdAtUtc, [ref]$createdAt) `
                -or $createdAt.Offset -ne [System.TimeSpan]::Zero `
                -or $createdAt -ne $FrozenPublishedAtUtc) {
                throw "Station package manifest identity is invalid."
            }
            $expectedMembers = [System.Collections.Generic.HashSet[string]]::new(
                [System.StringComparer]::Ordinal)
            $manifestWindowsPaths = [System.Collections.Generic.HashSet[string]]::new(
                [System.StringComparer]::OrdinalIgnoreCase)
            $previousManifestPath = $null
            $expectedMembers.Add("package.manifest.json") | Out-Null
            $expectedMembers.Add("package.signature.json") | Out-Null
            foreach ($manifestEntry in @($manifest.entries)) {
                Assert-ExactProperties $manifestEntry @("path", "length", "sha256", "mediaType") `
                    "Station package manifest entry"
                $entryPath = [string]$manifestEntry.path
                Assert-CanonicalPackagePath $entryPath "Station package manifest entry"
                Assert-CanonicalPackageText ([string]$manifestEntry.mediaType) `
                    "Station package manifest mediaType"
                if (-not $expectedMembers.Add($entryPath) `
                    -or -not $manifestWindowsPaths.Add($entryPath) `
                    -or ($null -ne $previousManifestPath `
                        -and [System.StringComparer]::Ordinal.Compare(
                            $previousManifestPath,
                            $entryPath) -ge 0) `
                    -or -not $entryMap.ContainsKey($entryPath) `
                    -or $manifestEntry.length -lt 0 `
                    -or $manifestEntry.length -ne $entryMap[$entryPath].Length `
                    -or $manifestEntry.sha256 -notmatch '^[0-9a-f]{64}$') {
                    throw "Station package manifest entry '$entryPath' is missing, duplicated, or invalid."
                }
                $stream = $entryMap[$entryPath].Open()
                try {
                    $actualSha256 = Get-StreamSha256 -Stream $stream
                }
                finally {
                    $stream.Dispose()
                }
                if ($actualSha256 -cne [string]$manifestEntry.sha256) {
                    throw "Station package entry '$entryPath' does not match its manifest SHA-256."
                }
                if ($entryPath -ceq "release.json" `
                    -and ($manifestEntry.length -ne $FrozenManifestSizeBytes `
                        -or $manifestEntry.sha256 -cne $FrozenManifestSha256)) {
                    throw "Station package release.json does not bind the public frozen manifest."
                }
                $entryBytes = Read-ZipEntryBytes `
                    -Entry $entryMap[$entryPath] `
                    -MaximumBytes $MaximumArchiveEntryBytes
                Assert-PublicContentSafety `
                    -Bytes $entryBytes `
                    -Description "Station package entry '$entryPath'" `
                    -DeclaredText (Test-DeclaredPublicText `
                        -Path $entryPath `
                        -MediaType ([string]$manifestEntry.mediaType)) `
                    -Json (Test-PublicJsonContent `
                        -Path $entryPath `
                        -MediaType ([string]$manifestEntry.mediaType)) | Out-Null
                $previousManifestPath = $entryPath
            }
            $computedContentSha256 = Get-StationPackageCanonicalContentSha256 $manifest
            if ($computedContentSha256 -cne $manifest.contentSha256) {
                throw "Station package content hash does not match its canonical entry manifest."
            }
            if ($expectedMembers.Count -ne $entryMap.Count) {
                $unknown = @($entryMap.Keys | Where-Object { -not $expectedMembers.Contains($_) })
                throw "Station package contains members outside package.manifest.json: $($unknown -join ', ')"
            }
            $signatureBytes = Read-ZipEntryBytes `
                -Entry $entryMap["package.signature.json"] `
                -MaximumBytes 1MB
            $signatureDecoded = Assert-PublicContentSafety `
                -Bytes $signatureBytes `
                -Description "Station package signature" `
                -DeclaredText $true `
                -Json $true
            $signatureText = $signatureDecoded.Text
            Assert-StationPackageSignature `
                -ManifestBytes $manifestBytes `
                -SignatureText $signatureText `
                -PublicKeyPath $SigningPublicKeyPath `
                -PublicKeySha256 $SigningPublicKeySha256
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $fileStream.Dispose()
    }
}

function Assert-ReferencedFile {
    param(
        [Parameter(Mandatory = $true)] $Reference,
        [Parameter(Mandatory = $true)] $ManifestEntries,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $relativePath = if ($Reference.PSObject.Properties.Name -ccontains "relativePath") {
        [string]$Reference.relativePath
    }
    else {
        [string]$Reference.path
    }
    if ([string]::IsNullOrWhiteSpace($relativePath) `
        -or -not $ManifestEntries.ContainsKey($relativePath)) {
        throw "$Description does not reference a manifest-owned evidence file."
    }
    $entry = $ManifestEntries[$relativePath]
    if ($Reference.sizeBytes -ne $entry.sizeBytes `
        -or [string]$Reference.sha256 -cne [string]$entry.sha256) {
        throw "$Description size or SHA-256 differs from the evidence manifest."
    }
}

function Resolve-ManifestOwnedPath {
    param(
        [Parameter(Mandatory = $true)][string] $RunRoot,
        [Parameter(Mandatory = $true)][string] $RelativePath
    )

    $normalized = $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $path = [System.IO.Path]::GetFullPath((Join-Path $RunRoot $normalized))
    $prefix = [System.IO.Path]::GetFullPath($RunRoot).TrimEnd('\', '/') + `
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Manifest-owned public release path escapes its run root."
    }
    return $path
}

function Assert-FrozenManifestFileInventory {
    param([Parameter(Mandatory = $true)] $FrozenManifest)

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $previous = $null
    foreach ($entry in @($FrozenManifest.files)) {
        Assert-ExactProperties $entry @("relativePath", "sizeBytes", "sha256") `
            "Frozen release file inventory entry"
        $relativePath = [string]$entry.relativePath
        if ([string]::IsNullOrWhiteSpace($relativePath) `
            -or $relativePath.Contains('\') `
            -or $relativePath.StartsWith('/', [System.StringComparison]::Ordinal) `
            -or @($relativePath.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0 `
            -or -not $seen.Add($relativePath) `
            -or $entry.sizeBytes -lt 0 `
            -or $entry.sha256 -notmatch '^[0-9a-f]{64}$' `
            -or ($null -ne $previous `
                -and [System.StringComparer]::Ordinal.Compare($previous, $relativePath) -ge 0)) {
            throw "Frozen release file inventory is not canonical."
        }
        $previous = $relativePath
    }
}

function Assert-DeploymentCatalog {
    param(
        [Parameter(Mandatory = $true)] $CatalogReference,
        [Parameter(Mandatory = $true)] $ExpectedPackage,
        [Parameter(Mandatory = $true)] $Summary,
        [Parameter(Mandatory = $true)][System.DateTimeOffset] $PublishedAtUtc,
        [Parameter(Mandatory = $true)][string] $RunRoot
    )

    $path = Resolve-ManifestOwnedPath $RunRoot ([string]$CatalogReference.relativePath)
    $text = [System.IO.File]::ReadAllText($path)
    $catalog = Assert-PublicJsonTextSafety `
        -Text $text `
        -Description "Station deployment catalog"
    Assert-ExactProperties $catalog @(
        "schema", "projectId", "applicationId", "projectSnapshotId",
        "productionLineDefinitionId", "stationSystemId", "packageContentSha256",
        "publishedAtUtc") "Station deployment catalog"
    $catalogPublishedAt = [System.DateTimeOffset]::MinValue
    $expectedCatalogFileName = Get-DeploymentCatalogFileName `
        -ProjectId ([string]$Summary.projectId) `
        -ApplicationId ([string]$Summary.applicationId) `
        -ProjectSnapshotId ([string]$Summary.projectSnapshotId) `
        -StationSystemId ([string]$ExpectedPackage.stationSystemId)
    if ($catalog.schema -cne "openlineops.station-package-deployment" `
        -or $catalog.projectId -cne $Summary.projectId `
        -or $catalog.applicationId -cne $Summary.applicationId `
        -or $catalog.projectSnapshotId -cne $Summary.projectSnapshotId `
        -or $catalog.productionLineDefinitionId -cne $Summary.productionLineDefinitionId `
        -or $catalog.stationSystemId -cne $ExpectedPackage.stationSystemId `
        -or $catalog.packageContentSha256 -cne $ExpectedPackage.packageContentSha256 `
        -or [System.IO.Path]::GetFileName($path) -cne $expectedCatalogFileName `
        -or -not [System.DateTimeOffset]::TryParse([string]$catalog.publishedAtUtc, [ref]$catalogPublishedAt) `
        -or $catalogPublishedAt.Offset -ne [System.TimeSpan]::Zero `
        -or $catalogPublishedAt -ne $PublishedAtUtc) {
        throw "Station deployment catalog does not bind the frozen release and Station package."
    }
}

function Assert-PublicReleaseEvidence {
    param(
        [Parameter(Mandatory = $true)] $Summary,
        [Parameter(Mandatory = $true)] $ManifestEntries,
        [Parameter(Mandatory = $true)][string] $RunRoot
    )

    $release = $Summary.frozenRelease
    if ($null -eq $release) {
        $unboundReleaseFiles = @($ManifestEntries.Keys | Where-Object {
                $_.StartsWith("public-release/", [System.StringComparison]::Ordinal)
            })
        if ($unboundReleaseFiles.Count -ne 0) {
            throw "Public release files exist without frozenRelease evidence."
        }
        return
    }

    Assert-ReferencedFile $release.releaseManifest $ManifestEntries "Frozen release manifest"
    Assert-ReferencedFile $release.signingPublicKey $ManifestEntries "Frozen release signing public key"
    $manifestPath = Resolve-ManifestOwnedPath $RunRoot ([string]$release.releaseManifest.relativePath)
    $manifestText = [System.IO.File]::ReadAllText($manifestPath)
    $frozen = Assert-PublicJsonTextSafety `
        -Text $manifestText `
        -Description "Frozen release manifest"
    Assert-ExactProperties $frozen @(
        "schema", "schemaVersion", "snapshotId", "projectId", "applicationId",
        "publishedAtUtc", "sourceApplicationRelativePath", "applicationProjectRelativePath",
        "metadata", "files", "contentSha256") "Frozen release manifest"
    $publishedAt = [System.DateTimeOffset]::MinValue
    if ($frozen.schema -cne "openlineops.project-release-artifact" `
        -or $frozen.schemaVersion -ne 1 `
        -or $frozen.snapshotId -cne $Summary.projectSnapshotId `
        -or $frozen.projectId -cne $Summary.projectId `
        -or $frozen.applicationId -cne $Summary.applicationId `
        -or $frozen.contentSha256 -cne $release.releaseContentSha256 `
        -or -not [System.DateTimeOffset]::TryParse([string]$frozen.publishedAtUtc, [ref]$publishedAt) `
        -or $frozen.metadata.productionLine.lineDefinitionId -cne $Summary.productionLineDefinitionId `
        -or $frozen.metadata.topologyId -cne $Summary.topologyId) {
        throw "Frozen release manifest does not bind the production closure identity."
    }
    Assert-FrozenManifestFileInventory $frozen

    $publicKeyPath = Resolve-ManifestOwnedPath $RunRoot ([string]$release.signingPublicKey.relativePath)
    $publicKeySha256 = (Get-FileHash -LiteralPath $publicKeyPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($publicKeySha256 -cne [string]$release.signingPublicKey.sha256) {
        throw "Frozen release signing public key differs from its public reference."
    }

    $packages = @($release.stationPackages)
    $catalogs = @($release.deploymentCatalogs)
    if ($packages.Count -ne 2 -or $catalogs.Count -ne 2) {
        throw "Frozen release must contain exactly two Station packages and deployment catalogs."
    }
    foreach ($package in $packages) {
        Assert-ReferencedFile $package $ManifestEntries "Station package"
        $expectedRelativePath = "public-release/station-packages/$($package.packageContentSha256).olopkg"
        if ($package.relativePath -cne $expectedRelativePath) {
            throw "Station package path does not bind its content identity."
        }
        $catalogMatches = @($catalogs | Where-Object {
                $_.stationSystemId -ceq $package.stationSystemId `
                    -and $_.packageContentSha256 -ceq $package.packageContentSha256
            })
        if ($catalogMatches.Count -ne 1) {
            throw "Station package does not have one exact deployment catalog."
        }
        $packagePath = Resolve-ManifestOwnedPath $RunRoot ([string]$package.relativePath)
        Test-StationPackage `
            -Path $packagePath `
            -ExpectedPackage $package `
            -Summary $Summary `
            -FrozenManifestSizeBytes ([System.IO.FileInfo]::new($manifestPath).Length) `
            -FrozenManifestSha256 ([string]$release.releaseManifest.sha256) `
            -FrozenPublishedAtUtc $publishedAt `
            -SigningPublicKeyPath $publicKeyPath `
            -SigningPublicKeySha256 $publicKeySha256
        Assert-ReferencedFile $catalogMatches[0] $ManifestEntries "Station deployment catalog"
        Assert-DeploymentCatalog `
            -CatalogReference $catalogMatches[0] `
            -ExpectedPackage $package `
            -Summary $Summary `
            -PublishedAtUtc $publishedAt `
            -RunRoot $RunRoot
    }

    $entryPackage = @($packages | Where-Object {
            $_.stationSystemId -ceq $release.entryStationDeployment.stationSystemId `
                -and $_.packageContentSha256 -ceq $release.entryStationDeployment.packageContentSha256
        })
    if ($entryPackage.Count -ne 1 `
        -or $release.entryStationDeployment.stationId -cne $release.entryStationDeployment.stationSystemId) {
        throw "Entry Station deployment does not bind one signed Station package."
    }
}

function Assert-PublicJsonSafety {
    param(
        [Parameter(Mandatory = $true)][AllowNull()] $Value,
        [Parameter(Mandatory = $true)][string] $Path
    )

    if ($null -eq $Value) {
        return
    }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Value.PSObject.Properties) {
            if ($property.Name -match '^(?i:logs|commandLine|resultPayload|textValue|message|failureReason|password|authorization)$' `
                -or $property.Name -match '^(?i:raw.*Base64)$') {
                throw "Public production closure JSON contains forbidden property '$($property.Name)'."
            }
            Assert-PublicJsonSafety -Value $property.Value -Path "$Path.$($property.Name)"
        }
        return
    }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $index = 0
        foreach ($item in $Value) {
            Assert-PublicJsonSafety -Value $item -Path "$Path[$index]"
            $index++
        }
        return
    }
    if ($Value -isnot [string]) {
        return
    }

    $text = [string]$Value
    Assert-NoCredentialText -Text $text -Description "Public production closure summary '$Path'"
    if ($text -match '^[A-Za-z]:[\\/]' `
        -or $text -match '^\\\\' `
        -or $text -match '^(?i:file:)' `
        -or $text -match '(^|[\\/])\.\.([\\/]|$)' `
        -or $text -match '(?i)\bBearer\s+' `
        -or $text -match '(?i)Password\s*=' `
        -or $text -match '(?i)(?:amqp|amqps|http|https)://[^\s/:@]+:[^\s/@]+@') {
        throw "Public production closure JSON contains unsafe string content at '$Path'."
    }
}

function Assert-PublicJsonTextSafety {
    param(
        [Parameter(Mandatory = $true)][string] $Text,
        [Parameter(Mandatory = $true)][string] $Description
    )

    try { $value = $Text | ConvertFrom-Json }
    catch { throw "$Description is invalid JSON: $($_.Exception.Message)" }
    Assert-PublicJsonSafety -Value $value -Path $Description
    return $value
}

function Assert-PublicFileReferenceShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @("relativePath", "sizeBytes", "sha256") $Description
}

function Assert-PublicScreenshotShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @("name", "path", "sha256", "sizeBytes") $Description
}

function Assert-PublicArtifactShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @("name", "kind", "storageKeySha256", "mediaType", "sizeBytes", "sha256") $Description
}

function Assert-PublicResourceShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @("kind", "resourceId", "status", "fencingToken") $Description
}

function Assert-PublicTypedOutputShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @("key", "kind", "valueSha256") $Description
}

function Assert-PublicRouteDecisionShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @(
        "sourceOperationRunId", "transitionId", "targetOperationId",
        "terminalDisposition", "sourceJudgement", "traversal", "decidedAtUtc") $Description
}

function Assert-PublicRecoveryDecisionShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @(
        "decisionId", "kind", "operationRunId", "operationId",
        "observedJudgement", "observedOutputCount", "decidedAtUtc") $Description
}

function Assert-PublicIncidentShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @("runtimeIncidentId", "severity", "code", "occurredAtUtc") $Description
}

function Assert-PublicOperationShape {
    param($Value, [string] $Description, [switch] $Trace)
    $properties = @(
        "operationRunId", "operationId", "attempt", "stationSystemId",
        "executionStatus", "judgement", "isTerminal", "startedAtUtc",
        "completedAtUtc", "failureCode", "completedStepCount", "commandCount",
        "incidentCount", "resources", "outputs")
    if ($Trace) {
        $properties += @("runtimeSessionStatus", "artifactCount", "artifacts", "incidents", "commandStatuses")
    }
    Assert-ExactProperties $Value $properties $Description
    foreach ($resource in @($Value.resources)) {
        Assert-PublicResourceShape $resource "$Description resource"
    }
    foreach ($output in @($Value.outputs)) {
        Assert-PublicTypedOutputShape $output "$Description typed output"
    }
    if ($Trace) {
        foreach ($artifact in @($Value.artifacts)) {
            Assert-PublicArtifactShape $artifact "$Description artifact"
        }
        foreach ($incident in @($Value.incidents)) {
            Assert-PublicIncidentShape $incident "$Description Incident"
        }
        foreach ($command in @($Value.commandStatuses)) {
            Assert-ExactProperties $command @(
                "runtimeCommandId", "actionId", "commandName", "executionStatus",
                "resultJudgement", "completedAtUtc") "$Description command status"
        }
    }
}

function Assert-PublicRunShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @(
        "productionRunId", "productionUnitId", "identity", "executionStatus",
        "judgement", "disposition", "controlState", "failureCode", "operationCount",
        "operations", "routeDecisions", "recoveryDecisions", "incidentCount") $Description
    if ($null -ne $Value.identity) {
        Assert-ExactProperties $Value.identity @("modelId", "inputKey", "value") "$Description identity"
    }
    foreach ($operation in @($Value.operations)) {
        Assert-PublicOperationShape $operation "$Description operation"
    }
    foreach ($decision in @($Value.routeDecisions)) {
        Assert-PublicRouteDecisionShape $decision "$Description route decision"
    }
    foreach ($decision in @($Value.recoveryDecisions)) {
        Assert-PublicRecoveryDecisionShape $decision "$Description recovery decision"
    }
}

function Assert-PublicLocationShape {
    param($Value, [string] $Description)
    if ($null -eq $Value) { return }
    Assert-ExactProperties $Value @(
        "kind", "lineId", "stationSystemId", "slotId", "carrierId", "carrierPositionId") $Description
}

function Assert-PublicLocationTransitionShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @(
        "evidenceId", "productionRunId", "materialKind", "materialId",
        "source", "destination", "occurredAtUtc") $Description
    Assert-PublicLocationShape $Value.source "$Description source"
    Assert-PublicLocationShape $Value.destination "$Description destination"
}

function Assert-PublicSlotTransitionShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @(
        "evidenceId", "productionRunId", "lineId", "stationSystemId", "slotId",
        "materialKind", "materialId", "previousStatus", "currentStatus", "occurredAtUtc") $Description
}

function Assert-PublicDispositionTransitionShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @(
        "evidenceId", "productionUnitId", "productionRunId", "previousDisposition",
        "currentDisposition", "occurredAtUtc") $Description
}

function Assert-PublicTraceShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @(
        "traceRecordId", "productionRunId", "executionStatus", "judgement", "disposition",
        "failureCode", "operations", "routeDecisions", "genealogyCount",
        "materialLocationTransitions", "slotOccupancyTransitions",
        "dispositionTransitions", "auditEntries") $Description
    foreach ($operation in @($Value.operations)) {
        Assert-PublicOperationShape $operation "$Description operation" -Trace
    }
    foreach ($decision in @($Value.routeDecisions)) {
        Assert-PublicRouteDecisionShape $decision "$Description route decision"
    }
    foreach ($transition in @($Value.materialLocationTransitions)) {
        Assert-PublicLocationTransitionShape $transition "$Description location transition"
    }
    foreach ($transition in @($Value.slotOccupancyTransitions)) {
        Assert-PublicSlotTransitionShape $transition "$Description Slot transition"
    }
    foreach ($transition in @($Value.dispositionTransitions)) {
        Assert-PublicDispositionTransitionShape $transition "$Description disposition transition"
    }
    foreach ($entry in @($Value.auditEntries)) {
        Assert-ExactProperties $entry @("auditEntryId", "action", "occurredAtUtc") "$Description audit entry"
    }
}

function Assert-PublicLineStateShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @(
        "productionLineDefinitionId", "generatedAtUtc", "activeRunCount", "activeRuns",
        "stations", "slots", "carrierCount") $Description
    foreach ($run in @($Value.activeRuns)) {
        Assert-ExactProperties $run @(
            "productionRunId", "productionUnitId", "executionStatus", "judgement",
            "disposition", "controlState", "isTerminal", "incidentCount") "$Description active Run"
    }
    foreach ($station in @($Value.stations)) {
        Assert-ExactProperties $station @(
            "stationSystemId", "status", "stationId", "presenceState", "presenceHealth",
            "queueCount", "activeOperations") "$Description Station"
        foreach ($operation in @($station.activeOperations)) {
            Assert-ExactProperties $operation @(
                "productionRunId", "productionUnitId", "operationRunId", "operationId",
                "executionStatus", "judgement", "resources") "$Description active Operation"
            foreach ($resource in @($operation.resources)) {
                Assert-PublicResourceShape $resource "$Description active resource"
            }
        }
    }
    foreach ($slot in @($Value.slots)) {
        Assert-ExactProperties $slot @(
            "stationSystemId", "slotId", "status", "materialKind", "materialId", "lastTransitionAtUtc") `
            "$Description Slot"
    }
}

function Assert-PublicUnitShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @("unitId", "runId", "identityValue", "runSubmitted") $Description
}

function Assert-PublicMaterialLifecycleShape {
    param($Value, [string] $Description)
    Assert-ExactProperties $Value @(
        "productionUnitId", "currentDisposition", "currentLocation", "currentCarrierLocation",
        "registeredAtUtc", "observedThroughUtc", "genealogyCount",
        "materialLocationTransitions", "slotOccupancyTransitions", "dispositionTransitions") $Description
    Assert-PublicLocationShape $Value.currentLocation "$Description current location"
    Assert-PublicLocationShape $Value.currentCarrierLocation "$Description current Carrier location"
    foreach ($transition in @($Value.materialLocationTransitions)) {
        Assert-PublicLocationTransitionShape $transition "$Description location transition"
    }
    foreach ($transition in @($Value.slotOccupancyTransitions)) {
        Assert-PublicSlotTransitionShape $transition "$Description Slot transition"
    }
    foreach ($transition in @($Value.dispositionTransitions)) {
        Assert-PublicDispositionTransitionShape $transition "$Description disposition transition"
    }
}

function Assert-ScenarioShape {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)] $Scenario
    )

    $expected = switch ($Name) {
        "concurrentPipeline" { @("status", "unitA", "unitB", "observedAtUtc", "assertion", "lineState", "screenshots") }
        "vendorPassed" { @("status", "run", "trace", "immutableRunTrace", "materialLifecycle", "artifacts", "artifactDownloads", "verifiedSaveActionCount", "verifiedArtifactSave", "screenshots") }
        "vendorFailedRework" { @("status", "unit", "run", "trace", "assertion", "screenshots") }
        "operatorCancel" { @("status", "unit", "run", "vendorProcessesBeforeCancel", "processTreeTerminated", "trace", "screenshots") }
        "vendorCrash" { @("status", "unit", "run", "trace", "incidents", "screenshots") }
        "recovery" { @("status", "unit", "interruptedOperationRunId", "backendPidTerminated", "vendorProcessesBeforeCrash", "recoveryRequired", "terminal", "noAutomaticReplay", "recoveryDecisions", "trace", "screenshots") }
        default { throw "Production closure summary contains unknown scenario '$Name'." }
    }
    Assert-ExactProperties $Scenario $expected "Production closure scenario '$Name'"

    foreach ($screenshot in @($Scenario.screenshots)) {
        Assert-PublicScreenshotShape $screenshot "Production closure scenario '$Name' screenshot"
    }
    if ($Scenario.PSObject.Properties.Name -ccontains "unit") {
        Assert-PublicUnitShape $Scenario.unit "Production closure scenario '$Name' unit"
    }
    if ($Name -ceq "concurrentPipeline") {
        Assert-PublicUnitShape $Scenario.unitA "Concurrent pipeline unit A"
        Assert-PublicUnitShape $Scenario.unitB "Concurrent pipeline unit B"
        Assert-PublicLineStateShape $Scenario.lineState "Concurrent pipeline line state"
    }
    if ($Scenario.PSObject.Properties.Name -ccontains "run") {
        Assert-PublicRunShape $Scenario.run "Production closure scenario '$Name' Run"
    }
    if ($Scenario.PSObject.Properties.Name -ccontains "trace") {
        Assert-PublicTraceShape $Scenario.trace "Production closure scenario '$Name' Trace"
    }
    if ($Name -ceq "vendorPassed") {
        Assert-ExactProperties $Scenario.immutableRunTrace @(
            "before", "after", "unchanged", "terminalCompletedAtUtc", "unloadAtUtc") `
            "Vendor Passed immutable Run Trace"
        Assert-ExactProperties $Scenario.immutableRunTrace.before @("sha256", "sizeBytes") `
            "Vendor Passed immutable Run Trace before identity"
        Assert-ExactProperties $Scenario.immutableRunTrace.after @("sha256", "sizeBytes") `
            "Vendor Passed immutable Run Trace after identity"
        Assert-PublicMaterialLifecycleShape $Scenario.materialLifecycle `
            "Vendor Passed material lifecycle"
        foreach ($artifact in @($Scenario.artifacts)) {
            Assert-PublicArtifactShape $artifact "Vendor Passed artifact"
        }
        foreach ($artifact in @($Scenario.artifactDownloads)) {
            Assert-PublicArtifactShape $artifact "Vendor Passed artifact download"
        }
        Assert-ExactProperties $Scenario.verifiedArtifactSave @(
            "name", "storageKeySha256", "path", "sizeBytes", "sha256",
            "invokedThroughPreloadIpc", "atomicTemporaryFileRemoved") `
            "Vendor Passed Desktop-saved artifact"
    }
    if ($Name -ceq "recovery") {
        Assert-PublicRunShape $Scenario.recoveryRequired "RecoveryRequired Run"
        Assert-PublicRunShape $Scenario.terminal "Reconciled terminal Run"
        foreach ($decision in @($Scenario.recoveryDecisions)) {
            Assert-PublicRecoveryDecisionShape $decision "Recovery scenario decision"
        }
    }

    if ($Name -in @("operatorCancel", "recovery")) {
        $processProperty = if ($Name -ceq "operatorCancel") {
            "vendorProcessesBeforeCancel"
        }
        else {
            "vendorProcessesBeforeCrash"
        }
        foreach ($process in @($Scenario.$processProperty)) {
            Assert-ExactProperties $process @("processId", "parentProcessId", "imageName") `
                "Production closure public process evidence"
        }
    }
    if ($Name -ceq "vendorCrash") {
        foreach ($incident in @($Scenario.incidents)) {
            Assert-PublicIncidentShape $incident "Production closure public Incident evidence"
        }
    }
}

function Assert-PublicProductionSummary {
    param([Parameter(Mandatory = $true)] $Summary)

    Assert-ExactProperties $Summary @(
        "schema",
        "status",
        "startedAtUtc",
        "completedAtUtc",
        "packagedExecutable",
        "packagedBinaries",
        "artifactRoot",
        "projectPath",
        "projectId",
        "applicationId",
        "topologyId",
        "productionLineDefinitionId",
        "projectSnapshotId",
        "applicationPortability",
        "frozenRelease",
        "externalProgramTrial",
        "studioAuthoring",
        "scenarios",
        "restart",
        "diagnostics",
        "failure") "Production closure public summary"
    Assert-Condition ($Summary.schema -ceq "openlineops.production-closure-e2e") `
        "Production closure summary schema is invalid."
    Assert-Condition ($Summary.packagedExecutable -ceq "packaged-desktop/OpenLineOps.exe") `
        "Production closure packaged executable path is not the canonical public label."
    Assert-Condition ($Summary.artifactRoot -ceq ".") `
        "Production closure artifact root is not canonical."
    if ($null -ne $Summary.projectPath) {
        Assert-Condition ($Summary.projectPath -ceq "private-runtime/project") `
            "Production closure project path is not the canonical public label."
    }
    Assert-PublicJsonSafety -Value $Summary -Path "summary"

    Assert-ExactProperties $Summary.packagedBinaries @("before", "after", "unchangedDuringRun") `
        "Production closure packaged binary evidence"
    foreach ($phase in @("before", "after")) {
        $phaseValue = $Summary.packagedBinaries.$phase
        if ($null -eq $phaseValue) {
            continue
        }
        Assert-ExactProperties $phaseValue @("desktopExecutable", "runtimeApiExecutable") `
            "Production closure packaged binaries $phase"
        foreach ($binary in @(
                @{ Name = "desktopExecutable"; Path = "packaged-desktop/OpenLineOps.exe" },
                @{ Name = "runtimeApiExecutable"; Path = "packaged-desktop/runtime/api/OpenLineOps.Api.exe" })) {
            $identity = $phaseValue.($binary.Name)
            Assert-ExactProperties $identity @("path", "sha256", "sizeBytes", "modifiedAtUtc") `
                "Production closure $phase $($binary.Name) identity"
            Assert-Condition ($identity.path -ceq $binary.Path) `
                "Production closure $phase $($binary.Name) path is not canonical."
        }
    }

    if ($null -ne $Summary.packagedBinaries.before -and $null -ne $Summary.packagedBinaries.after) {
        foreach ($binary in @("desktopExecutable", "runtimeApiExecutable")) {
            Assert-Condition ($Summary.packagedBinaries.before.$binary.path `
                    -ceq $Summary.packagedBinaries.after.$binary.path) `
                "Production closure packaged $binary before/after path differs."
        }
    }

    if ($null -ne $Summary.diagnostics) {
        Assert-ExactProperties $Summary.diagnostics @("code", "detailSha256") `
            "Production closure public diagnostics"
        Assert-Condition ($Summary.diagnostics.detailSha256 -match '^[0-9a-f]{64}$') `
            "Production closure public diagnostics hash is invalid."
    }
    if ($null -ne $Summary.applicationPortability) {
        Assert-ExactProperties $Summary.applicationPortability @(
            "status", "sourceProjectId", "targetProjectId", "applicationId",
            "fileCount", "totalSizeBytes", "sourceBeforeCopyTreeSha256",
            "copiedTreeSha256", "afterImportTreeSha256", "afterPublishTreeSha256",
            "afterExecutionTreeSha256", "sourceAfterExecutionTreeSha256", "unchanged") `
            "Production closure Application portability evidence"
    }
    if ($null -ne $Summary.failure) {
        Assert-ExactProperties $Summary.failure @("code", "detailSha256") `
            "Production closure public failure"
        Assert-Condition ($Summary.failure.detailSha256 -match '^[0-9a-f]{64}$') `
            "Production closure public failure hash is invalid."
    }
    if ($null -ne $Summary.externalProgramTrial) {
        Assert-ExactProperties $Summary.externalProgramTrial @(
            "status", "executionStatus", "judgement", "artifactCount", "directoryImport") `
            "Production closure external program trial"
        Assert-ExactProperties $Summary.externalProgramTrial.directoryImport @(
            "entryPoint", "files", "preservedSameBasenames") `
            "Production closure external program directory import"
    }
    if ($null -ne $Summary.studioAuthoring) {
        Assert-ExactProperties $Summary.studioAuthoring @(
            "status", "productModelId", "operationCount", "terminalCount",
            "terminalDispositions", "transitionCount", "publishEnabled", "screenshot") `
            "Production closure Studio authoring"
        Assert-PublicScreenshotShape $Summary.studioAuthoring.screenshot `
            "Production closure Studio authoring screenshot"
    }
    if ($null -ne $Summary.frozenRelease) {
        $release = $Summary.frozenRelease
        Assert-ExactProperties $release @(
            "releaseManifest", "projectRelativeReleaseManifestPath", "releaseContentSha256",
            "manifestSchema", "externalPrograms", "stationPackages", "deploymentCatalogs",
            "signingPublicKey", "entryStationDeployment") "Production closure frozen release"
        Assert-PublicFileReferenceShape $release.releaseManifest "Frozen release manifest reference"
        Assert-PublicFileReferenceShape $release.signingPublicKey "Frozen signing public key reference"
        foreach ($program in @($release.externalPrograms)) {
            Assert-ExactProperties $program @("resourceId", "contentSha256", "files") `
                "Frozen external program"
            foreach ($file in @($program.files)) {
                Assert-PublicFileReferenceShape $file "Frozen external program file"
            }
        }
        foreach ($package in @($release.stationPackages)) {
            Assert-ExactProperties $package @(
                "stationSystemId", "packageContentSha256", "relativePath", "sizeBytes", "sha256") `
                "Frozen Station package"
        }
        foreach ($catalog in @($release.deploymentCatalogs)) {
            Assert-ExactProperties $catalog @(
                "stationSystemId", "packageContentSha256", "relativePath", "sizeBytes", "sha256") `
                "Frozen Station deployment catalog"
        }
        Assert-ExactProperties $release.entryStationDeployment @(
            "stationSystemId", "stationId", "packageContentSha256") `
            "Frozen entry Station deployment"
    }
    if ($null -ne $Summary.restart) {
        Assert-ExactProperties $Summary.restart @(
            "status", "previousCdpPort", "traceCountBefore", "traceCountAfter",
            "activeRunCount", "rebuiltProjection", "screenshots") `
            "Production closure restart evidence"
        Assert-PublicLineStateShape $Summary.restart.rebuiltProjection `
            "Production closure rebuilt projection"
        foreach ($screenshot in @($Summary.restart.screenshots)) {
            Assert-PublicScreenshotShape $screenshot "Production closure restart screenshot"
        }
    }
    foreach ($scenarioProperty in @($Summary.scenarios.PSObject.Properties)) {
        Assert-ScenarioShape -Name $scenarioProperty.Name -Scenario $scenarioProperty.Value
    }
}

function Assert-ProductionSummary {
    param(
        [Parameter(Mandatory = $true)] $Summary,
        [Parameter(Mandatory = $true)] $ManifestEntries
    )

    Assert-Condition ($Summary.schema -ceq "openlineops.production-closure-e2e") `
        "Production closure summary schema is invalid."
    Assert-Condition ($Summary.status -ceq "passed" `
            -and $null -eq $Summary.failure `
            -and $null -eq $Summary.diagnostics) `
        "Production closure summary is not passed."
    $startedAt = [System.DateTimeOffset]::MinValue
    $completedAt = [System.DateTimeOffset]::MinValue
    Assert-Condition ([System.DateTimeOffset]::TryParse([string]$Summary.startedAtUtc, [ref]$startedAt) `
            -and [System.DateTimeOffset]::TryParse([string]$Summary.completedAtUtc, [ref]$completedAt) `
            -and $completedAt -gt $startedAt) `
        "Production closure timestamps are invalid."
    foreach ($name in @("projectId", "applicationId", "topologyId", "productionLineDefinitionId", "projectSnapshotId")) {
        Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$Summary.$name)) `
            "Production closure summary is missing $name."
    }

    $portability = $Summary.applicationPortability
    Assert-Condition ($portability.status -ceq "passed" `
            -and -not [string]::IsNullOrWhiteSpace([string]$portability.sourceProjectId) `
            -and $portability.sourceProjectId -cne $portability.targetProjectId `
            -and $portability.targetProjectId -ceq $Summary.projectId `
            -and $portability.applicationId -ceq $Summary.applicationId `
            -and $portability.fileCount -gt 0 `
            -and $portability.totalSizeBytes -gt 0 `
            -and $portability.unchanged -eq $true) `
        "Application portability evidence does not identify one unchanged Application copied across two Projects."
    $portabilityHashes = @(
        $portability.sourceBeforeCopyTreeSha256,
        $portability.copiedTreeSha256,
        $portability.afterImportTreeSha256,
        $portability.afterPublishTreeSha256,
        $portability.afterExecutionTreeSha256,
        $portability.sourceAfterExecutionTreeSha256)
    Assert-Condition (@($portabilityHashes | Where-Object { $_ -cmatch '^[0-9a-f]{64}$' }).Count `
            -eq $portabilityHashes.Count `
            -and @($portabilityHashes | Select-Object -Unique).Count -eq 1) `
        "Application portability file-tree hashes differ across copy, import, publish, or execution."

    Assert-Condition ($Summary.packagedBinaries.unchangedDuringRun -eq $true) `
        "Packaged binaries were not proven immutable."
    foreach ($binary in @("desktopExecutable", "runtimeApiExecutable")) {
        $before = $Summary.packagedBinaries.before.$binary
        $after = $Summary.packagedBinaries.after.$binary
        Assert-Condition ($before.sha256 -match '^[0-9a-f]{64}$' `
                -and $before.sha256 -ceq $after.sha256 `
                -and $before.sizeBytes -gt 0 `
                -and $before.sizeBytes -eq $after.sizeBytes `
                -and $before.modifiedAtUtc -ceq $after.modifiedAtUtc) `
            "Packaged $binary identity changed during E2E."
    }

    $release = $Summary.frozenRelease
    Assert-Condition ($release.releaseContentSha256 -match '^[0-9a-f]{64}$' `
            -and $release.manifestSchema -ceq "openlineops.project-release-artifact") `
        "Frozen release identity is invalid."
    Assert-ReferencedFile $release.releaseManifest $ManifestEntries "Frozen release manifest"
    Assert-ReferencedFile $release.signingPublicKey $ManifestEntries "Release signing public key"
    $packages = @($release.stationPackages)
    $catalogs = @($release.deploymentCatalogs)
    Assert-Condition ($packages.Count -eq 2 -and $catalogs.Count -eq 2) `
        "Frozen release must export exactly two Station packages and deployment catalogs."
    Assert-Condition (@($packages.stationSystemId | Select-Object -Unique).Count -eq 2 `
            -and @($catalogs.stationSystemId | Select-Object -Unique).Count -eq 2) `
        "Frozen release evidence does not describe two distinct Station Systems."
    foreach ($package in $packages) {
        Assert-Condition ($package.packageContentSha256 -match '^[0-9a-f]{64}$') `
            "Station package content identity is invalid."
        Assert-ReferencedFile $package $ManifestEntries "Station package"
        Assert-Condition (@($catalogs | Where-Object {
                    $_.stationSystemId -ceq $package.stationSystemId `
                        -and $_.packageContentSha256 -ceq $package.packageContentSha256
                }).Count -eq 1) `
            "Station package has no exact deployment catalog."
    }
    foreach ($catalog in $catalogs) {
        Assert-ReferencedFile $catalog $ManifestEntries "Station deployment catalog"
    }

    Assert-Condition ($Summary.externalProgramTrial.status -ceq "passed" `
            -and $Summary.externalProgramTrial.executionStatus -ceq "Completed" `
            -and $Summary.externalProgramTrial.judgement -ceq "Passed" `
            -and $Summary.externalProgramTrial.artifactCount -gt 0) `
        "External program protocol trial evidence is incomplete."
    $directoryImport = $Summary.externalProgramTrial.directoryImport
    $importedFiles = @($directoryImport.files)
    $preservedSameBasenames = @($directoryImport.preservedSameBasenames)
    Assert-Condition ($directoryImport.entryPoint -is [string] `
            -and $directoryImport.entryPoint -cmatch '^files/(?:[A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+\.exe$' `
            -and @($importedFiles | Where-Object { $_ -isnot [string] }).Count -eq 0 `
            -and $importedFiles.Count -ge 3 `
            -and @($importedFiles | Where-Object {
                    $_ -cnotmatch '^files/(?:[A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+$'
                }).Count -eq 0 `
            -and @($importedFiles | Select-Object -Unique).Count -eq $importedFiles.Count `
            -and @($importedFiles | Where-Object {
                    $_ -ceq $directoryImport.entryPoint
                }).Count -eq 1) `
        "External program directory import entry point or file inventory is invalid."
    Assert-Condition (@($preservedSameBasenames | Where-Object { $_ -isnot [string] }).Count -eq 0 `
            -and $preservedSameBasenames.Count -eq 2 `
            -and $preservedSameBasenames[0] -cne $preservedSameBasenames[1] `
            -and @($preservedSameBasenames | Where-Object {
                    $importedFiles -cnotcontains $_
                }).Count -eq 0 `
            -and $preservedSameBasenames[0].Split('/')[-1] `
                -ceq $preservedSameBasenames[1].Split('/')[-1] `
            -and ($preservedSameBasenames[0].Split('/')[0..($preservedSameBasenames[0].Split('/').Count - 2)] -join '/') `
                -cne ($preservedSameBasenames[1].Split('/')[0..($preservedSameBasenames[1].Split('/').Count - 2)] -join '/')) `
        "External program directory import did not preserve two distinct nested files with the same basename."
    Assert-Condition ($Summary.studioAuthoring.status -ceq "passed" `
            -and $Summary.studioAuthoring.operationCount -eq 2 `
            -and $Summary.studioAuthoring.terminalCount -eq 2 `
            -and $Summary.studioAuthoring.transitionCount -eq 4 `
            -and $Summary.studioAuthoring.publishEnabled -eq $true) `
        "Studio route authoring evidence is incomplete."
    Assert-ReferencedFile $Summary.studioAuthoring.screenshot $ManifestEntries "Studio authoring screenshot"

    $requiredScenarios = @(
        "concurrentPipeline",
        "vendorPassed",
        "vendorFailedRework",
        "operatorCancel",
        "vendorCrash",
        "recovery")
    $actualScenarios = @($Summary.scenarios.PSObject.Properties.Name)
    Assert-Condition ($actualScenarios.Count -eq $requiredScenarios.Count) `
        "Production closure summary must contain exactly six scenarios."
    foreach ($scenarioName in $requiredScenarios) {
        Assert-Condition ($actualScenarios -ccontains $scenarioName `
                -and $Summary.scenarios.$scenarioName.status -ceq "passed") `
            "Production closure scenario '$scenarioName' is missing or not passed."
    }

    $concurrent = $Summary.scenarios.concurrentPipeline
    Assert-Condition ($concurrent.unitA.unitId -ne $concurrent.unitB.unitId `
            -and $concurrent.unitA.runId -ne $concurrent.unitB.runId `
            -and @($concurrent.lineState.stations | Where-Object {
                    @($_.activeOperations).Count -gt 0
                }).Count -ge 2 `
            -and @($concurrent.lineState.slots | Where-Object { $_.status -ceq "Running" }).Count -ge 2) `
        "Concurrent pipeline evidence does not prove two products at two running Stations."

    $passed = $Summary.scenarios.vendorPassed
    Assert-Condition ($passed.run.executionStatus -ceq "Completed" `
            -and $passed.run.judgement -ceq "Passed" `
            -and $passed.run.disposition -ceq "Completed" `
            -and $passed.trace.executionStatus -ceq "Completed" `
            -and $passed.trace.judgement -ceq "Passed" `
            -and $passed.trace.disposition -ceq "Completed" `
            -and $passed.run.incidentCount -eq 0) `
        "Vendor Passed axes or disposition are invalid."
    Assert-Condition (@($passed.run.routeDecisions | Where-Object {
                $_.transitionId -ceq "route.vendor-default-terminal" `
                    -and $null -eq $_.targetOperationId `
                    -and $_.terminalDisposition -ceq "Completed"
            }).Count -eq 1) `
        "Vendor Passed route decision is missing."
    $requiredArtifacts = @("measurements.csv", "inspection.png", "report.pdf", "stdout.log", "stderr.log")
    foreach ($artifactName in $requiredArtifacts) {
        $artifact = @($passed.artifacts | Where-Object { $_.name -ceq $artifactName })
        $download = @($passed.artifactDownloads | Where-Object { $_.name -ceq $artifactName })
        Assert-Condition ($artifact.Count -eq 1 -and $download.Count -eq 1 `
                -and $artifact[0].sha256 -match '^[0-9a-f]{64}$' `
                -and $artifact[0].sha256 -ceq $download[0].sha256 `
                -and $artifact[0].sizeBytes -eq $download[0].sizeBytes `
                -and $artifact[0].storageKeySha256 -match '^[0-9a-f]{64}$' `
                -and $artifact[0].storageKeySha256 -ceq $download[0].storageKeySha256) `
            "Vendor Passed artifact '$artifactName' was not downloaded and hash-verified."
    }
    $immutableTrace = $passed.immutableRunTrace
    Assert-Condition ($immutableTrace.unchanged -eq $true `
            -and $immutableTrace.before.sha256 -match '^[0-9a-f]{64}$' `
            -and $immutableTrace.before.sha256 -ceq $immutableTrace.after.sha256 `
            -and $immutableTrace.before.sizeBytes -gt 0 `
            -and $immutableTrace.before.sizeBytes -eq $immutableTrace.after.sizeBytes) `
        "Vendor Passed immutable Run Trace byte identity is invalid."
    $terminalCompletedAt = [System.DateTimeOffset]::MinValue
    $unloadAt = [System.DateTimeOffset]::MinValue
    Assert-Condition ([System.DateTimeOffset]::TryParse(
                [string]$immutableTrace.terminalCompletedAtUtc,
                [ref]$terminalCompletedAt) `
            -and [System.DateTimeOffset]::TryParse(
                [string]$immutableTrace.unloadAtUtc,
                [ref]$unloadAt) `
            -and $unloadAt -gt $terminalCompletedAt) `
        "Vendor Passed final unload must occur after terminal Run completion."
    Assert-Condition ($passed.verifiedSaveActionCount -ge 5) `
        "Trace workbench did not expose all verified save actions."
    Assert-ReferencedFile $passed.verifiedArtifactSave $ManifestEntries "Desktop-saved Trace artifact"

    $failed = $Summary.scenarios.vendorFailedRework
    Assert-Condition ($failed.run.executionStatus -ceq "Completed" `
            -and $failed.run.judgement -ceq "Failed" `
            -and $failed.run.disposition -ceq "Nonconforming" `
            -and $failed.trace.executionStatus -ceq "Completed" `
            -and $failed.trace.judgement -ceq "Failed" `
            -and $failed.trace.disposition -ceq "Nonconforming" `
            -and $failed.run.incidentCount -eq 0) `
        "Vendor Failed/Rework axes or disposition are invalid."
    foreach ($operationExpectation in @(
            @{ Id = "operation.preparation"; Attempt = 2 },
            @{ Id = "operation.vendor-test"; Attempt = 1 },
            @{ Id = "operation.vendor-test"; Attempt = 2 })) {
        Assert-Condition (@($failed.run.operations | Where-Object {
                    $_.operationId -ceq $operationExpectation.Id `
                        -and $_.attempt -eq $operationExpectation.Attempt `
                        -and $_.executionStatus -ceq "Completed"
                }).Count -eq 1) `
            "Vendor Failed/Rework operation $($operationExpectation.Id) attempt $($operationExpectation.Attempt) is missing."
    }
    Assert-Condition (@($failed.run.routeDecisions | Where-Object {
                $_.transitionId -ceq "route.vendor-failed-rework" -and $_.sourceJudgement -ceq "Failed"
            }).Count -eq 1 `
            -and @($failed.run.routeDecisions | Where-Object {
                    $_.transitionId -ceq "route.vendor-failed-terminal" `
                        -and $_.terminalDisposition -ceq "Nonconforming"
                }).Count -eq 1) `
        "Vendor Failed/Rework route decisions are incomplete."

    $cancel = $Summary.scenarios.operatorCancel
    $cancelProcesses = @($cancel.vendorProcessesBeforeCancel)
    Assert-Condition ($cancel.run.executionStatus -ceq "Canceled" `
            -and $cancel.run.judgement -ceq "Aborted" `
            -and $cancel.trace.executionStatus -ceq "Canceled" `
            -and $cancel.trace.judgement -ceq "Aborted" `
            -and $cancel.processTreeTerminated -eq $true `
            -and @($cancelProcesses | Where-Object {
                    $parentId = $_.parentProcessId
                    @($cancelProcesses | Where-Object { $_.processId -eq $parentId }).Count -gt 0
                }).Count -gt 0) `
        "Operator Cancel did not prove full vendor process-tree termination."

    $crash = $Summary.scenarios.vendorCrash
    Assert-Condition ($crash.run.executionStatus -ceq "Failed" `
            -and $crash.run.judgement -ceq "Unknown" `
            -and $crash.run.incidentCount -gt 0 `
            -and @($crash.incidents).Count -gt 0) `
        "Vendor crash did not produce Failed + Unknown with an Incident."

    $recovery = $Summary.scenarios.recovery
    Assert-Condition ($recovery.backendPidTerminated -gt 0 `
            -and $recovery.noAutomaticReplay -eq $true `
            -and $recovery.recoveryRequired.controlState -ceq "RecoveryRequired" `
            -and $recovery.terminal.executionStatus -ceq "Completed" `
            -and $recovery.terminal.judgement -ceq "Passed" `
            -and @($recovery.recoveryRequired.operations | Where-Object {
                    $_.operationId -ceq "operation.vendor-test"
                }).Count -eq 1 `
            -and @($recovery.terminal.operations | Where-Object {
                    $_.operationId -ceq "operation.vendor-test"
                }).Count -eq 1 `
            -and @($recovery.recoveryDecisions | Where-Object { $_.kind -ceq "Reconcile" }).Count -eq 1 `
            -and @($recovery.trace.auditEntries | Where-Object {
                    $_.action -ceq "ProductionRun.Recovery.Reconcile"
                }).Count -eq 1) `
        "Coordinator crash recovery did not prove RecoveryRequired, Reconcile, and no replay."

    Assert-Condition ($Summary.restart.status -ceq "passed" `
            -and $Summary.restart.traceCountBefore -ge 6 `
            -and $Summary.restart.traceCountAfter -eq $Summary.restart.traceCountBefore `
            -and $Summary.restart.activeRunCount -eq 0 `
            -and $Summary.restart.rebuiltProjection.activeRunCount -eq 0) `
        "Studio restart did not prove persisted Trace and rebuilt idle projection."

    $expectedScreenshots = @(
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
    foreach ($name in $expectedScreenshots) {
        $relativePath = "screenshots/$name.png"
        Assert-Condition ($ManifestEntries.ContainsKey($relativePath)) `
            "Required production closure screenshot is missing: $relativePath"
    }
}

$resolvedEvidenceRoot = Resolve-EvidenceRoot $EvidenceRoot
$rootTree = Get-SafeTree $resolvedEvidenceRoot
$manifestFiles = @($rootTree.Files | Where-Object { $_.Name -ceq "evidence-manifest.json" })
if ($manifestFiles.Count -ne 1) {
    throw "Production closure evidence must contain exactly one evidence-manifest.json; found $($manifestFiles.Count)."
}
$runRoot = $manifestFiles[0].Directory.FullName
$runTree = Get-SafeTree $runRoot
$allowedRunDirectories = @(
    "screenshots",
    "verified-trace-artifact-saves",
    "public-release",
    "public-release/station-packages",
    "public-release/deployment-catalog")
foreach ($directory in $rootTree.Directories) {
    if ($directory.FullName -ceq $resolvedEvidenceRoot -or $directory.FullName -ceq $runRoot) {
        continue
    }
    if (-not $directory.FullName.StartsWith(
            $runRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Production closure evidence root contains an unknown directory: $($directory.FullName)"
    }
    $relativeDirectory = Get-CanonicalRelativePath $runRoot $directory.FullName
    if ($allowedRunDirectories -cnotcontains $relativeDirectory) {
        throw "Production closure evidence contains an unknown directory: $relativeDirectory"
    }
}
if (@($rootTree.Files | Where-Object {
            -not $_.FullName.StartsWith(
                $runRoot + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase)
        }).Count -ne 0) {
    throw "Production closure evidence root contains files outside its single manifest-owned run."
}

$manifestBytes = Read-PublicFileBytes `
    -File $manifestFiles[0] `
    -Description "Production closure evidence manifest"
$manifestDecoded = Assert-PublicContentSafety `
    -Bytes $manifestBytes `
    -Description "Production closure evidence manifest" `
    -DeclaredText $true `
    -Json $true
$manifest = $manifestDecoded.Text | ConvertFrom-Json
Assert-ExactProperties $manifest @("schema", "schemaVersion", "generatedAtUtc", "files") `
    "Production closure evidence manifest"
if ($manifest.schema -cne "openlineops.production-closure-evidence-manifest" `
    -or $manifest.schemaVersion -ne 1) {
    throw "Production closure evidence manifest identity is invalid."
}
$generatedAt = [System.DateTimeOffset]::MinValue
if (-not [System.DateTimeOffset]::TryParse([string]$manifest.generatedAtUtc, [ref]$generatedAt)) {
    throw "Production closure evidence manifest generatedAtUtc is invalid."
}

$manifestEntries = [System.Collections.Generic.Dictionary[string,object]]::new(
    [System.StringComparer]::Ordinal)
$manifestPaths = [System.Collections.Generic.List[string]]::new()
foreach ($entry in @($manifest.files)) {
    Assert-ExactProperties $entry @("relativePath", "sizeBytes", "sha256") `
        "Production closure evidence manifest file"
    $relativePath = [string]$entry.relativePath
    if (-not (Test-AllowedEvidencePath $relativePath) `
        -or $relativePath -imatch '(^|/)(user-data|security|keys)(/|$)' `
        -or $relativePath -imatch '\.token$' `
        -or $relativePath -imatch 'private[^/]*\.pem$' `
        -or $relativePath -imatch '\.(pfx|p12)$' `
        -or $entry.sizeBytes -lt 0 `
        -or $entry.sha256 -notmatch '^[0-9a-f]{64}$' `
        -or $manifestEntries.ContainsKey($relativePath)) {
        throw "Production closure evidence manifest contains a forbidden, duplicate, or invalid path: $relativePath"
    }
    $manifestEntries.Add($relativePath, $entry)
    $manifestPaths.Add($relativePath)
}
$sortedManifestPaths = @($manifestPaths)
[System.Array]::Sort($sortedManifestPaths, [System.StringComparer]::Ordinal)
if (-not [System.Linq.Enumerable]::SequenceEqual(
        [string[]]$manifestPaths.ToArray(),
        [string[]]$sortedManifestPaths,
        [System.StringComparer]::Ordinal)) {
    throw "Production closure evidence manifest files are not in canonical ordinal order."
}

$actualFiles = @($runTree.Files | Where-Object { $_.Name -cne "evidence-manifest.json" })
if ($actualFiles.Count -ne $manifestEntries.Count) {
    throw "Production closure evidence membership differs from its exact manifest."
}
foreach ($file in $actualFiles) {
    $relativePath = Get-CanonicalRelativePath $runRoot $file.FullName
    if (-not $manifestEntries.ContainsKey($relativePath)) {
        throw "Production closure evidence contains an unknown file: $relativePath"
    }
    $entry = $manifestEntries[$relativePath]
    $actualSha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($file.Length -ne $entry.sizeBytes -or $actualSha256 -cne [string]$entry.sha256) {
        throw "Production closure evidence file differs from its exact manifest: $relativePath"
    }
    if ($relativePath -cnotmatch '^public-release/station-packages/[0-9a-f]{64}\.olopkg$') {
        $publicBytes = Read-PublicFileBytes `
            -File $file `
            -Description "Production closure evidence '$relativePath'"
        Assert-PublicContentSafety `
            -Bytes $publicBytes `
            -Description "Production closure evidence '$relativePath'" `
            -DeclaredText (Test-DeclaredPublicText -Path $relativePath -MediaType $null) `
            -Json (Test-PublicJsonContent -Path $relativePath -MediaType $null) | Out-Null
    }
}

if (-not $manifestEntries.ContainsKey("summary.json")) {
    throw "Production closure evidence manifest does not own summary.json."
}
$summaryPath = Join-Path $runRoot "summary.json"
try {
    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
}
catch {
    throw "Production closure summary is invalid JSON: $($_.Exception.Message)"
}
Assert-PublicProductionSummary $summary
Assert-PublicReleaseEvidence `
    -Summary $summary `
    -ManifestEntries $manifestEntries `
    -RunRoot $runRoot
if ($RequirePassed) {
    Assert-ProductionSummary $summary $manifestEntries
}

Write-Host "Production closure evidence verification passed."
Write-Host " - Run root: $runRoot"
Write-Host " - Exact public files: $($manifestEntries.Count)"
Write-Host " - Passed summary required: $([bool]$RequirePassed)"
