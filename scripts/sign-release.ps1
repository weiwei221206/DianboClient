# 给点波音乐的发布产物做 Authenticode 代码签名。
#
# 说明（重要）：
#   * 只有【受信任 CA 签发】的代码签名证书才能消除下游用户的蓝色 SmartScreen 警告。
#   * 自签名证书（-SelfSigned）只适合本机开发验证：签名后本机不再提示，
#     但其他机器仍需先手动把该证书装进“受信任的根证书颁发机构”，否则提示不变。
#   * 未签名时也可以运行本脚本的 -Report 模式，先看清当前产物到底是什么状态。
#
# 用法示例：
#   pwsh -File scripts\sign-release.ps1 -Report
#   pwsh -File scripts\sign-release.ps1 -PfxPath D:\cert\my.pfx -PfxPassword '***'
#   pwsh -File scripts\sign-release.ps1 -Thumbprint 3F1A...9C
#   pwsh -File scripts\sign-release.ps1 -SelfSigned
[CmdletBinding(DefaultParameterSetName = 'Report')]
param(
    # 已安装在当前用户证书存储中的代码签名证书指纹（推荐：私钥不落盘到仓库）。
    [Parameter(ParameterSetName = 'Thumbprint', Mandatory)]
    [string]$Thumbprint,

    # PFX 文件路径（含私钥）。私钥文件不要提交进仓库。
    [Parameter(ParameterSetName = 'Pfx', Mandatory)]
    [string]$PfxPath,

    [Parameter(ParameterSetName = 'Pfx')]
    [string]$PfxPassword,

    # 生成一张自签名证书并导入当前用户证书存储，仅用于本机验证签名流程。
    [Parameter(ParameterSetName = 'SelfSigned', Mandatory)]
    [switch]$SelfSigned,

    # 只报告产物当前的签名状态，不做任何修改。
    [Parameter(ParameterSetName = 'Report', Mandatory)]
    [switch]$Report,

    # 要签名的目录，默认为 Release 构建输出目录。
    [string]$Directory,

    # 不写入时间戳。签名本身仍然有效，但证书过期后签名即失效，不建议。
    [switch]$NoTimestamp,

    # 连带把 *.dll 里的 .NET 程序集也签名（更慢，通常只有 exe 是必须的）。
    [switch]$IncludeDll
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

if (-not $Directory) {
    $Directory = Join-Path $root 'src\Dianbo.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64'
}
$Directory = (Resolve-Path -LiteralPath $Directory -ErrorAction Stop).Path

$timestampUrls = @(
    'http://timestamp.digicert.com',
    'http://timestamp.sectigo.com',
    'http://timestamp.globalsign.com/tsa/r6advanced1'
)

function Get-SignTool {
    # 1) Microsoft.Windows.SDK.BuildTools 包已随项目还原到本机 NuGet 缓存，优先使用它。
    $pkgRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'
    if (Test-Path -LiteralPath $pkgRoot) {
        $candidate = Get-ChildItem -LiteralPath $pkgRoot -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    # 2) 已安装的 Windows SDK。
    foreach ($base in @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")) {
        if (Test-Path -LiteralPath $base) {
            $candidate = Get-ChildItem -LiteralPath $base -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\' } |
                Sort-Object FullName -Descending |
                Select-Object -First 1
            if ($candidate) { return $candidate.FullName }
        }
    }

    # 3) PATH 里已有的。
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    throw '找不到 signtool.exe。请还原项目（会有 Microsoft.Windows.SDK.BuildTools 包），或安装 Windows SDK。'
}

function Get-TargetFiles {
    param([string]$Dir, [switch]$WithDll)
    $patterns = if ($WithDll) { @('*.exe', '*.dll') } else { @('*.exe') }
    $files = foreach ($p in $patterns) {
        Get-ChildItem -LiteralPath $Dir -Filter $p -File -ErrorAction SilentlyContinue
    }
    # 跳过已被第三方（微软）合法签名的文件，避免覆盖别人的签名。
    $files | Where-Object {
        (Get-AuthenticodeSignature -LiteralPath $_.FullName).Status -ne 'Valid'
    } | Sort-Object Name
}

function Show-SignatureReport {
    param([string]$Dir)
    $targets = Get-TargetFiles -Dir $Dir -WithDll:$IncludeDll
    $rows = foreach ($f in $targets) {
        $sig = Get-AuthenticodeSignature -LiteralPath $f.FullName
        [pscustomobject]@{
            文件     = $f.Name
            大小MB   = [math]::Round($f.Length / 1MB, 1)
            签名状态 = $sig.Status
            签名者   = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '—' }
        }
    }
    $rows | Format-Table -AutoSize
    $unsigned = @($rows | Where-Object { $_.签名状态 -ne 'Valid' }).Count
    Write-Host ("目录：{0}" -f $Dir) -ForegroundColor DarkGray
    Write-Host ("可签名文件 {0} 个，其中未获得有效签名 {1} 个。" -f $rows.Count, $unsigned) -ForegroundColor Cyan
    return $unsigned
}

$signtool = Get-SignTool
Write-Host ("signtool: {0}" -f $signtool) -ForegroundColor DarkGray

# ---------------------------------------------------------------- 只报告
if ($Report) {
    [void](Show-SignatureReport -Dir $Directory)
    exit 0
}

# ---------------------------------------------------------------- 准备证书
$cert = $null
switch ($PSCmdlet.ParameterSetName) {
    'Thumbprint' {
        $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $Thumbprint } |
            Select-Object -First 1
        if (-not $cert) { throw "当前用户/本机证书存储中找不到指纹为 $Thumbprint 的证书。" }
    }
    'Pfx' {
        $resolved = (Resolve-Path -LiteralPath $PfxPath -ErrorAction Stop).Path
        $secure = if ($PfxPassword) { ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force } else { $null }
        $cert = Get-PfxCertificate -FilePath $resolved -Password $secure -ErrorAction Stop
    }
    'SelfSigned' {
        $subject = 'CN=Dianbo Local Dev Signing (NOT FOR DISTRIBUTION)'
        $existing = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq $subject } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1
        if ($existing) {
            $cert = $existing
            Write-Host '复用已有的自签名测试证书。' -ForegroundColor Yellow
        }
        else {
            $cert = New-SelfSignedCertificate `
                -Type CodeSigningCert `
                -Subject $subject `
                -CertStoreLocation Cert:\CurrentUser\My `
                -NotAfter (Get-Date).AddYears(3) `
                -KeyUsage DigitalSignature `
                -KeyExportPolicy Exportable
            Write-Host '已生成自签名测试证书。' -ForegroundColor Yellow
        }
        Write-Host '提醒：自签名只对“已信任该证书的机器”有效，不能消除别人的 SmartScreen 警告。' -ForegroundColor Yellow
    }
}

if (-not $cert.HasPrivateKey) { throw '所选证书没有私钥，无法签名。' }
if ($cert.NotAfter -lt (Get-Date)) { throw "证书已于 $($cert.NotAfter) 过期。" }
Write-Host ("使用证书：{0}" -f $cert.Subject) -ForegroundColor Cyan
Write-Host ("有效期至：{0:yyyy-MM-dd}" -f $cert.NotAfter) -ForegroundColor DarkGray

# ---------------------------------------------------------------- 签名
$targets = Get-TargetFiles -Dir $Directory -WithDll:$IncludeDll
if ($targets.Count -eq 0) { throw "目录中没有需要签名的文件：$Directory" }

$failed = @()
$i = 0
foreach ($file in $targets) {
    $i++
    Write-Progress -Activity '正在签名' -Status $file.Name -PercentComplete (100 * $i / $targets.Count)
    # 从证书存储中按指纹选取证书（/s My 指定当前用户“个人”存储）。
    $signArgs = @('sign', '/v', '/fd', 'SHA256', '/sha1', $cert.Thumbprint, '/s', 'My')

    if (-not $NoTimestamp) {
        $stamped = $false
        foreach ($url in $timestampUrls) {
            $attempt = $signArgs + @('/tr', $url, '/td', 'SHA256', $file.FullName)
            $out = & $signtool @attempt 2>&1
            if ($LASTEXITCODE -eq 0) { $stamped = $true; break }
            Write-Host ("时间戳服务器 {0} 失败，换下一个。" -f $url) -ForegroundColor DarkYellow
        }
        if (-not $stamped) {
            Write-Host '所有时间戳服务器都不可用；改为不带时间戳签名（证书过期后签名会失效）。' -ForegroundColor Yellow
            $out = & $signtool @($signArgs + $file.FullName) 2>&1
        }
    }
    else {
        $out = & $signtool @($signArgs + $file.FullName) 2>&1
    }

    if ($LASTEXITCODE -ne 0) {
        $failed += $file.Name
        Write-Host ("签名失败：{0}" -f $file.Name) -ForegroundColor Red
        $out | Select-Object -Last 6 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    }
}
Write-Progress -Activity '正在签名' -Completed

# ---------------------------------------------------------------- 校验
Write-Host ''
Write-Host '=== 签名结果 ===' -ForegroundColor Cyan
[void](Show-SignatureReport -Dir $Directory)

if ($failed.Count -gt 0) {
    Write-Host ("以下文件签名失败：{0}" -f ($failed -join ', ')) -ForegroundColor Red
    exit 1
}

if ($cert.Subject -like '*NOT FOR DISTRIBUTION*') {
    Write-Host ''
    Write-Host '这是自签名测试证书：本机不会再提示，但发给别人仍然会弹 SmartScreen。' -ForegroundColor Yellow
}
exit 0
