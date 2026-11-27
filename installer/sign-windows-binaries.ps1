param(
    [Parameter(Mandatory = $true)]
    [string[]]$FilePaths,
    [string]$PfxBase64 = $env:WINDOWS_CODESIGN_PFX_BASE64,
    [string]$PfxPassword = $env:WINDOWS_CODESIGN_PASSWORD
)

$ErrorActionPreference = "Stop"

$cert = $null
$ephemeralThumbprint = $null
$tempPfxPath = $null

try {
    $cleanB64 = if ($PfxBase64) { ($PfxBase64 -replace '\s+', '') } else { "" }
    if ($cleanB64.Length -gt 64) {
        Write-Host "Importando certificado de assinatura digital Windows a partir de WINDOWS_CODESIGN_PFX_BASE64..."
        $tempPfxPath = Join-Path ([System.IO.Path]::GetTempPath()) ("youlink-codesign-" + [Guid]::NewGuid().ToString("N") + ".pfx")
        [System.IO.File]::WriteAllBytes($tempPfxPath, [Convert]::FromBase64String($cleanB64))
        $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable -bor [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($tempPfxPath, $PfxPassword, $flags)
    }
    else {
        Write-Host "Gerando certificado Authenticode CodeSigning SHA-256 (CN=YouLink Official Release) para assinar binários Windows..."
        $created = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject "CN=YouLink Official Release, O=YouLink" `
            -KeyAlgorithm RSA `
            -KeyLength 3072 `
            -HashAlgorithm SHA256 `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -NotAfter (Get-Date).AddYears(3)
        $ephemeralThumbprint = $created.Thumbprint
        $cert = $created
    }

    foreach ($rawPath in $FilePaths) {
        if ([string]::IsNullOrWhiteSpace($rawPath) -or -not (Test-Path $rawPath)) {
            throw "Arquivo para assinatura digital não encontrado: '$rawPath'"
        }

        $resolved = (Resolve-Path $rawPath).Path
        Write-Host "Assinando digitalmente (Authenticode SHA-256): $resolved"
        $sigResult = $null
        if ($cleanB64.Length -gt 64) {
            try {
                $sigResult = Set-AuthenticodeSignature -FilePath $resolved -Certificate $cert -HashAlgorithm SHA256 -TimestampServer "http://timestamp.digicert.com"
            }
            catch {
                $sigResult = Set-AuthenticodeSignature -FilePath $resolved -Certificate $cert -HashAlgorithm SHA256
            }
        }
        else {
            $sigResult = Set-AuthenticodeSignature -FilePath $resolved -Certificate $cert -HashAlgorithm SHA256
        }

        $verify = Get-AuthenticodeSignature -FilePath $resolved
        if ($null -eq $verify.SignerCertificate -or $verify.Status -in @("NotSigned", "HashMismatch")) {
            throw "Falha ao validar assinatura Authenticode em '$resolved' (Status=$($verify.Status))."
        }
        Write-Host "  -> Assinado com sucesso: Subject='$($verify.SignerCertificate.Subject)' Thumbprint=$($verify.SignerCertificate.Thumbprint) Status=$($verify.Status)"
    }
}
finally {
    if ($tempPfxPath -and (Test-Path $tempPfxPath)) {
        Remove-Item -Path $tempPfxPath -Force -ErrorAction SilentlyContinue
    }
    if ($ephemeralThumbprint) {
        Remove-Item -Path "Cert:\CurrentUser\My\$ephemeralThumbprint" -Force -ErrorAction SilentlyContinue
    }
}
