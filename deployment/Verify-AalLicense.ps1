param(
    [Parameter(Mandatory = $true)]
    [string]$LicensePath,

    [Parameter(Mandatory = $true)]
    [string]$SignedLicensePath,

    [Parameter(Mandatory = $true)]
    [string]$PublicKeyPath,

    [string]$GpgExecutable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-GpgExecutable {
    param([string]$RequestedExecutable)

    $requestedCandidates = @(
        $RequestedExecutable,
        $env:CIA_GPG_EXECUTABLE
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $requestedCandidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }

        $command = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            return $command.Source
        }
    }

    $pathCommand = Get-Command 'gpg.exe' -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $pathCommand) {
        return $pathCommand.Source
    }

    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $standardCandidates = @(
        (Join-Path $programFiles 'GnuPG\bin\gpg.exe'),
        (Join-Path $programFilesX86 'GnuPG\bin\gpg.exe'),
        (Join-Path $programFiles 'Git\usr\bin\gpg.exe'),
        (Join-Path $programFiles 'Git\mingw64\bin\gpg.exe'),
        (Join-Path $localApplicationData 'Programs\GnuPG\bin\gpg.exe')
    )

    foreach ($candidate in $standardCandidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw 'GnuPG (gpg.exe) is required to verify the signed AAL license before publication. Set CIA_GPG_EXECUTABLE or install GnuPG in a standard location.'
}

function Invoke-Gpg {
    param(
        [string]$Executable,
        [string[]]$Arguments,
        [string]$FailureMessage
    )

    $argumentLine = ($Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Executable
    $startInfo.Arguments = $argumentLine
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Could not start GnuPG from '$Executable'."
    }

    $standardOutput = $process.StandardOutput.ReadToEndAsync()
    $standardError = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $output = @(
        ($standardOutput.Result -split "`r?`n")
        ($standardError.Result -split "`r?`n")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    if ($process.ExitCode -ne 0) {
        throw "$FailureMessage`n$($output -join [Environment]::NewLine)"
    }

    return $output
}

function Normalize-Fingerprint {
    param([string]$Value)

    return ($Value -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
}

function Normalize-OpenPgpText {
    param([string]$Value)

    return $Value.Replace("`r`n", "`n").Replace("`r", "`n")
}

$licenseFullPath = [System.IO.Path]::GetFullPath($LicensePath)
$signedLicenseFullPath = [System.IO.Path]::GetFullPath($SignedLicensePath)
$publicKeyFullPath = [System.IO.Path]::GetFullPath($PublicKeyPath)

foreach ($requiredPath in @($licenseFullPath, $signedLicenseFullPath, $publicKeyFullPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required AAL verification artifact is missing: $requiredPath"
    }
}

$expectedFingerprintValue = '16C968BD782888D630101761EF8DC38A512D8472'

$gpg = Resolve-GpgExecutable $GpgExecutable
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$verificationHome = Join-Path $temporaryRoot ("cia-aal-verify-" + [Guid]::NewGuid().ToString('N').Substring(0, 12))
$extractedLicensePath = Join-Path $verificationHome 'LICENSE.extracted'

try {
    [System.IO.Directory]::CreateDirectory($verificationHome) | Out-Null

    Invoke-Gpg $gpg @(
        '--homedir', $verificationHome,
        '--batch',
        '--no-autostart',
        '--import', $publicKeyFullPath
    ) 'The supplied AAL public key could not be imported.' | Out-Null

    $keyListing = Invoke-Gpg $gpg @(
        '--homedir', $verificationHome,
        '--batch',
        '--no-autostart',
        '--with-colons',
        '--fingerprint',
        '--fingerprint',
        '--list-keys'
    ) 'The supplied AAL public key could not be inspected.'

    $publicKeyRecords = @($keyListing | Where-Object { $_ -match '^pub:' })
    $fingerprintRecords = @($keyListing | Where-Object { $_ -match '^fpr:' })
    if ($publicKeyRecords.Count -ne 1 -or $fingerprintRecords.Count -lt 1) {
        throw 'The AAL public-key artifact must contain exactly one primary public key.'
    }

    $publicKeyFingerprint = Normalize-Fingerprint (($fingerprintRecords[0] -split ':')[9])
    if ($publicKeyFingerprint -cne $expectedFingerprintValue) {
        throw "The AAL public-key fingerprint '$publicKeyFingerprint' does not match the approved fingerprint '$expectedFingerprintValue'."
    }

    $signatureStatus = Invoke-Gpg $gpg @(
        '--homedir', $verificationHome,
        '--batch',
        '--no-autostart',
        '--status-fd', '1',
        '--verify', $signedLicenseFullPath
    ) 'The AAL license signature is invalid.'

    $validSignatureFingerprints = @(
        $signatureStatus |
            Where-Object { $_ -match '^\[GNUPG:\] VALIDSIG ' } |
            ForEach-Object { Normalize-Fingerprint (($_ -split ' ')[2]) }
    )
    if ($validSignatureFingerprints.Count -ne 1 -or $validSignatureFingerprints[0] -cne $expectedFingerprintValue) {
        throw 'The AAL license was not signed by the approved signing key.'
    }

    Invoke-Gpg $gpg @(
        '--homedir', $verificationHome,
        '--batch',
        '--no-autostart',
        '--yes',
        '--output', $extractedLicensePath,
        '--decrypt', $signedLicenseFullPath
    ) 'The signed AAL license content could not be extracted.' | Out-Null

    $approvedLicense = [System.IO.File]::ReadAllText($licenseFullPath)
    $signedLicenseContent = [System.IO.File]::ReadAllText($extractedLicensePath)
    if ((Normalize-OpenPgpText $approvedLicense) -cne (Normalize-OpenPgpText $signedLicenseContent)) {
        throw 'The signed AAL license content does not match LICENSE after OpenPGP text line-ending canonicalization.'
    }

    Write-Output "Verified signed AAL license with fingerprint $expectedFingerprintValue."
}
finally {
    if (Test-Path -LiteralPath $verificationHome) {
        $verifiedTemporaryRoot = $temporaryRoot.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        $verifiedHome = [System.IO.Path]::GetFullPath($verificationHome)
        $verifiedLeaf = [System.IO.Path]::GetFileName($verifiedHome)

        if ((-not $verifiedHome.StartsWith($verifiedTemporaryRoot, [StringComparison]::OrdinalIgnoreCase)) -or
            (-not $verifiedLeaf.StartsWith('cia-aal-verify-', [StringComparison]::Ordinal))) {
            throw "Refusing to remove unexpected verification directory '$verifiedHome'."
        }

        [System.IO.Directory]::Delete($verifiedHome, $true)
    }
}
