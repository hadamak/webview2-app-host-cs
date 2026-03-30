# Protect-File.ps1
# 指定されたファイルを AES-256 で暗号化し、.wve 形式で出力します。

param (
    [Parameter(Mandatory=$true)]
    [string]$InputFile,
    
    [string]$OutputFile = "$InputFile.wve"
)

# ---------------------------------------------------------------------------
# 設定 (CryptoUtils.cs と一致させる)
# ---------------------------------------------------------------------------
$Magic = [byte[]](0x57, 0x56, 0x41, 0x45) # "WVAE"
$Mode  = [byte]0x00                       # Simple AES
$Key   = [byte[]](
    0x4a, 0x6e, 0x39, 0x76, 0x2d, 0x31, 0x32, 0x33,
    0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x30, 0x61,
    0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
    0x6a, 0x6b, 0x6c, 0x6d, 0x6e, 0x6f, 0x70, 0x71
)

if (-not (Test-Path $InputFile)) {
    Write-Error "Input file not found: $InputFile"
    return
}

$PlainData = [System.IO.File]::ReadAllBytes($InputFile)

# ---------------------------------------------------------------------------
# 暗号化処理
# ---------------------------------------------------------------------------
$Aes = [System.Security.Cryptography.Aes]::Create()
$Aes.Key = $Key
$Aes.GenerateIV()
$Aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
$Aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7

$Encryptor = $Aes.CreateEncryptor()
$MS = New-Object System.IO.MemoryStream
$CS = New-Object System.Security.Cryptography.CryptoStream($MS, $Encryptor, [System.Security.Cryptography.CryptoStreamMode]::Write)

$CS.Write($PlainData, 0, $PlainData.Count)
$CS.FlushFinalBlock()
$EncryptedData = $MS.ToArray()

$CS.Dispose()
$MS.Dispose()
$Aes.Dispose()

# ---------------------------------------------------------------------------
# ファイル出力 (Header + Body)
# ---------------------------------------------------------------------------
$FS = [System.IO.File]::Create($OutputFile)
$FS.Write($Magic, 0, $Magic.Count)
$FS.WriteByte($Mode)
$FS.Write($Aes.IV, 0, $Aes.IV.Count)
$FS.Write($EncryptedData, 0, $EncryptedData.Count)
$FS.Dispose()

Write-Host "File protected successfully: $OutputFile" -ForegroundColor Green
