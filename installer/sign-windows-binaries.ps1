param(
    [Parameter(Mandatory = $true)]
    [string]$FilePath
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($FilePath) -or -not (Test-Path -LiteralPath $FilePath)) {
    throw "Arquivo para assinatura digital não encontrado: '$FilePath'"
}

$resolved = (Resolve-Path -LiteralPath $FilePath).Path
$rawB64 = $env:WINDOWS_CODESIGN_PFX_BASE64
$pfxPassword = $env:WINDOWS_CODESIGN_PASSWORD
$cleanB64 = if ($rawB64) { ($rawB64 -replace '\s+', '') } else { "" }

if ($cleanB64.Length -le 64) {
    Write-Host "Nenhum certificado oficial (WINDOWS_CODESIGN_PFX_BASE64) configurado; mantendo '$resolved' sem certificado autoassinado."
    exit 0
}

$tempPfxPath = $null
$cert = $null

try {
    Write-Host "Importando certificado oficial YouLink a partir de WINDOWS_CODESIGN_PFX_BASE64..."
    $tempPfxPath = Join-Path ([System.IO.Path]::GetTempPath()) ("youlink-codesign-" + [Guid]::NewGuid().ToString("N") + ".pfx")
    [System.IO.File]::WriteAllBytes($tempPfxPath, [Convert]::FromBase64String($cleanB64))
    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable -bor [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($tempPfxPath, $pfxPassword, $flags)
    $expectedThumb = ($cert.Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()

    Write-Host "Assinando digitalmente (Authenticode SHA-256): $resolved"
    $sigResult = Set-AuthenticodeSignature -FilePath $resolved -Certificate $cert -HashAlgorithm SHA256 -TimestampServer "http://timestamp.digicert.com"
    if ($null -eq $sigResult) {
        throw "Set-AuthenticodeSignature retornou nulo para '$resolved'."
    }

    $verify = Get-AuthenticodeSignature -FilePath $resolved
    $actualThumb = if ($null -ne $verify.SignerCertificate -and $verify.SignerCertificate.Thumbprint) {
        ($verify.SignerCertificate.Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    } else {
        ""
    }

    if ($verify.Status -ne "Valid" -or $actualThumb -ne $expectedThumb) {
        throw "Assinatura Authenticode inválida em '$resolved' (Status=$($verify.Status), Thumbprint=$actualThumb, Esperado=$expectedThumb)."
    }

    Write-Host "  -> Assinado e validado com sucesso: Subject='$($verify.SignerCertificate.Subject)' Thumbprint=$actualThumb Status=$($verify.Status)"
}
finally {
    if ($null -ne $cert) {
        $cert.Dispose()
    }
    if ($tempPfxPath -and (Test-Path -LiteralPath $tempPfxPath)) {
        Remove-Item -LiteralPath $tempPfxPath -Force -ErrorAction SilentlyContinue
    }
}
