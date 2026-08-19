# ODCL 代码签名：让 UAC 显示自定义发布者而非"未知发布者"
# 用法（需管理员运行的 PowerShell）: .\sign.ps1 [-Exe <path>]
param(
    [string]$Exe = "bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ODCL.exe"
)
$ErrorActionPreference = "Stop"
$Subject = "CN=Adam001"

# 清理旧发布者证书，避免 UAC 缓存旧名称
Get-ChildItem Cert:\LocalMachine\My, Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPublisher |
    Where-Object { $_.Subject -eq "CN=ODCL Publisher, O=ODCL" } |
    ForEach-Object { Remove-Item "Cert:\LocalMachine\$($_.PSParentPath.Split('\')[-1])\$($_.Thumbprint)" -Force -ErrorAction SilentlyContinue }

$cert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.Subject -eq $Subject -and $_.EnhancedKeyUsageList.ObjectId -contains "1.3.6.1.5.5.7.3.3" } |
    Select-Object -First 1

if (-not $cert) {
    Write-Host "创建自签名代码签名证书: $Subject"
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
        -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(10)
}

$cer = Join-Path $env:TEMP "odcl-cert.cer"
Export-Certificate -Cert "Cert:\LocalMachine\My\$($cert.Thumbprint)" -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null

$signtool = Join-Path (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools") `
    "10.0.28000.2526\bin\10.0.28000.0\x64\signtool.exe"
if (-not (Test-Path $signtool)) { throw "未找到 signtool.exe（Microsoft.Windows.SDK.BuildTools）" }

Write-Host "使用证书: $Subject  ($($cert.Thumbprint))"
& $signtool sign /fd SHA256 /sm /sha1 $cert.Thumbprint $Exe
if ($LASTEXITCODE -ne 0) { throw "签名失败，signtool 退出码 $LASTEXITCODE" }

Get-AuthenticodeSignature $Exe | Format-List Status, @{n="Signer"; e={$_.SignerCertificate.Subject}} | Out-String | Write-Host



