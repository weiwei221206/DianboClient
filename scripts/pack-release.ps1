# 生成点波音乐的发布压缩包。
#
# 发布包形态（与开发目录 bin\...\win-x64 不同，专门为发布整理过）：
#   点波音乐-v<版本>-win-x64\
#       启动点波音乐.exe        <- 唯一的 exe，根目录只留它
#       app\                    <- 程序主体与全部运行时依赖
#           mpv\                <- 播放后端（mpv.exe 等），随包分发
#
# 规则：
#   * 根目录只保留启动器 exe，其余文件一律进 app\；
#   * app\ 下只保留 zh-CN 与 en-us 两种语言资源，其余语言目录删除；
#   * 把 mpv 后端放进 app\mpv\（程序就是按这个相对路径找后端的）；
#   * 默认不打包调试符号（*.pdb），需要时加 -KeepSymbols；
#   * 最终压缩包只包含上面这一个顶层文件夹。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\pack-release.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\pack-release.ps1 -KeepSymbols
#   powershell -ExecutionPolicy Bypass -File scripts\pack-release.ps1 -WithoutMpv
[CmdletBinding()]
param(
    # 构建配置，固定 Release。
    [string]$Configuration = 'Release',

    # 目标平台，工程只声明了 x64。
    [string]$Platform = 'x64',

    # 打包暂存目录，默认 <仓库>\publish\pack。
    [string]$StagingRoot,

    # 压缩包输出路径，默认 <仓库>\点波音乐-v<版本>-win-x64.zip。
    [string]$ZipPath,

    # mpv 后端目录，默认 <仓库>\validation\.deps\mpv-ci（与开发时的查找路径一致）。
    [string]$MpvSourceDirectory,

    # 只整理暂存目录，不生成压缩包。
    [switch]$NoZip,

    # 保留 *.pdb 调试符号。
    [switch]$KeepSymbols,

    # 不打包 mpv 后端（只发主程序时用；这样用户必须自己指定后端路径）。
    [switch]$WithoutMpv
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# Windows PowerShell 5.1 不带 ZipFile / ZipArchive 类型，需要显式加载程序集。
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# 发布包保留的语言资源；其余语言目录会被删除。
$keepLanguages = @('zh-CN', 'en-us')

# 语言目录形如 zh-CN、en-us、en-GB、sr-Cyrl-RS、ca-Es-VALENCIA；用「至少一段短横线」把它和
# Assets、Microsoft.UI.Xaml、mpv 这类正常子目录区分开。
$languageDirectoryPattern = '^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})+$'

# 官方 mpv 包里除安装脚本外的二进制；程序只需要 mpv.exe，另两个是官方原样附带。
$mpvFileNames = @('mpv.exe', 'mpv.com', 'vulkan-1.dll')

function Get-DotNet {
    $bundled = Join-Path $root '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $bundled) { return $bundled }
    return (Get-Command dotnet -ErrorAction Stop).Source
}

function Get-AppVersion {
    # 版本号只在根目录的 Directory.Build.props 里维护，所有工程共用。
    $props = Join-Path $root 'Directory.Build.props'
    $match = [regex]::Match((Get-Content -LiteralPath $props -Raw), '<Version>([^<]+)</Version>')
    if (-not $match.Success) { throw "无法从 $props 读取 <Version>。" }
    return $match.Groups[1].Value.Trim()
}

function Invoke-Publish {
    param([string]$Project, [string]$OutputDirectory, [string]$DotNet)
    Write-Host ("正在发布 {0} …" -f (Split-Path -Leaf $Project)) -ForegroundColor Cyan
    & $DotNet publish $Project -c $Configuration -p:Platform=$Platform -r win-x64 -o $OutputDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw "发布失败：$Project" }
}

# 把 mpv 后端复制进 app\mpv\，并生成来源说明 SOURCE.txt。
# 程序用 AppContext.BaseDirectory\mpv\mpv.exe 定位后端，所以目录名和位置都不能改。
function Copy-MpvBackend {
    param([string]$SourceDirectory, [string]$AppDirectory)

    if (-not $SourceDirectory) { $SourceDirectory = Join-Path $root 'validation\.deps\mpv-ci' }
    $executable = Join-Path $SourceDirectory 'mpv.exe'
    if (-not (Test-Path -LiteralPath $executable)) {
        throw @"
没有找到 mpv 后端：$executable
请把官方包解压到该目录，或用 -MpvSourceDirectory 指定别的目录。
  来源：https://github.com/mpv-player/mpv/releases/tag/git-release
  固定版本：mpv-v0.41.0-dev-ga1f50f2c3-36640285359-x86_64-pc-windows-msvc.zip
  SHA-256：5cea8bd5e60ac1e93e209b55e995342161863c2fe3e066ff2cff6d45efe423ff
确实要发不带后端的包，加 -WithoutMpv。
"@
    }

    $target = Join-Path $AppDirectory 'mpv'
    New-Item -ItemType Directory -Path $target -Force | Out-Null

    $copied = @()
    foreach ($name in $mpvFileNames) {
        $source = Join-Path $SourceDirectory $name
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination $target -Force
            $copied += $name
        }
    }

    $version = (Get-Item -LiteralPath (Join-Path $target 'mpv.exe')).VersionInfo.FileVersion
    $hash = (Get-FileHash -LiteralPath (Join-Path $target 'mpv.exe') -Algorithm SHA256).Hash
    $archive = @(Get-ChildItem -LiteralPath $SourceDirectory -File -Filter 'mpv-*.zip' | Select-Object -First 1)

    $lines = @()
    $lines += '点波音乐随包分发的 mpv 后端'
    $lines += '（本文件由 scripts\pack-release.ps1 生成，请勿手工修改）'
    $lines += ''
    $lines += "版本：     $version"
    $lines += "SHA-256：  $hash"
    $lines += '官方来源： https://github.com/mpv-player/mpv/releases/tag/git-release'
    if ($archive.Count -gt 0) { $lines += "官方文件： $($archive[0].Name)" }
    if ($version -match 'g([0-9a-f]{7,40})') {
        $lines += "对应源码： https://github.com/mpv-player/mpv/commit/$($Matches[1])"
    }
    $lines += ''
    $lines += '使用方式：点波音乐把它当作独立进程的播放后端启动（只调用命令行与 JSON IPC），'
    $lines += '不链接 mpv 的代码，也不修改其二进制。'
    $lines += ''
    $lines += '许可提醒：该官方构建启用了 GPL 组件（mpv 与 FFmpeg 的 GPL 选项），而官方 Windows'
    $lines += '包里并不附带任何许可文本。对外发布前必须补齐再分发所需的许可与 notices 材料：'
    $lines += 'mpv 自身的 Copyright 文件、GPL-2.0 全文，以及静态链接进来的 FFmpeg 等组件的许可。'
    $lines += '详见仓库 docs\程序开发详细说明.md 第 14 节「发布边界与交付清单」。'
    Set-Content -LiteralPath (Join-Path $target 'SOURCE.txt') -Value $lines -Encoding UTF8

    [pscustomobject]@{ Version = $version; Sha256 = $hash; Files = $copied }
}

$dotnet = Get-DotNet
$version = Get-AppVersion
$packageName = "点波音乐-v$version-win-x64"

if (-not $StagingRoot) { $StagingRoot = Join-Path $root 'publish\pack' }
if (-not $ZipPath) { $ZipPath = Join-Path $root "$packageName.zip" }

$packageDirectory = Join-Path $StagingRoot $packageName
$appDirectory = Join-Path $packageDirectory 'app'

# ---------------------------------------------------------------- 干净暂存目录
foreach ($path in @($StagingRoot, $ZipPath)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

# ---------------------------------------------------------------- 发布
Invoke-Publish -Project (Join-Path $root 'src\Dianbo.Launcher\Dianbo.Launcher.csproj') -OutputDirectory $packageDirectory -DotNet $dotnet
Invoke-Publish -Project (Join-Path $root 'src\Dianbo.App\Dianbo.App.csproj') -OutputDirectory $appDirectory -DotNet $dotnet

# ---------------------------------------------------------------- 只留启动 exe
$rootFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File)
$expectedLauncher = '启动点波音乐.exe'
$unexpected = @($rootFiles | Where-Object { $_.Name -ne $expectedLauncher })
if ($unexpected.Count -gt 0) {
    Write-Host ("根目录出现非启动器文件，已移除：{0}" -f (($unexpected | Select-Object -ExpandProperty Name) -join ', ')) -ForegroundColor Yellow
    $unexpected | Remove-Item -Force
}
$launcher = Join-Path $packageDirectory $expectedLauncher
if (-not (Test-Path -LiteralPath $launcher)) { throw "没有找到启动器 $expectedLauncher，发布可能没有成功。" }
$rootDirectories = @(Get-ChildItem -LiteralPath $packageDirectory -Directory | Where-Object { $_.Name -ne 'app' })
if ($rootDirectories.Count -gt 0) { throw ("根目录出现了预期外的子目录：{0}" -f (($rootDirectories.Name) -join ', ')) }

# ---------------------------------------------------------------- 语言资源
$removedLanguages = @()
foreach ($directory in Get-ChildItem -LiteralPath $appDirectory -Directory) {
    if ($directory.Name -notmatch $languageDirectoryPattern) { continue }
    if ($keepLanguages -contains $directory.Name) { continue }
    $removedLanguages += $directory.Name
    Remove-Item -LiteralPath $directory.FullName -Recurse -Force
}
$keptLanguages = @(Get-ChildItem -LiteralPath $appDirectory -Directory |
    Where-Object { $_.Name -match $languageDirectoryPattern } |
    Select-Object -ExpandProperty Name)
$missing = @($keepLanguages | Where-Object { $keptLanguages -notcontains $_ })
if ($missing.Count -gt 0) { throw ("发布产物里缺少需要保留的语言资源：{0}" -f ($missing -join ', ')) }

# ---------------------------------------------------------------- mpv 后端
$mpv = $null
if (-not $WithoutMpv) {
    Write-Host '正在放入 mpv 播放后端 …' -ForegroundColor Cyan
    $mpv = Copy-MpvBackend -SourceDirectory $MpvSourceDirectory -AppDirectory $appDirectory
}

# ---------------------------------------------------------------- 调试符号
$symbols = @()
if (-not $KeepSymbols) {
    $symbols = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File -Filter '*.pdb')
    $symbols | Remove-Item -Force
}

# ---------------------------------------------------------------- 汇总
Write-Host ''
Write-Host '=== 发布包整理结果 ===' -ForegroundColor Cyan
Write-Host ("包目录：{0}" -f $packageDirectory)
Write-Host ("根目录文件：{0}" -f ((Get-ChildItem -LiteralPath $packageDirectory -File).Name -join ', '))
if ($mpv) {
    Write-Host ("mpv 后端：{0}（{1}）" -f ($mpv.Files -join ', '), $mpv.Version)
    Write-Host ("  mpv.exe SHA-256：{0}" -f $mpv.Sha256) -ForegroundColor DarkGray
}
else {
    Write-Host 'mpv 后端：未打包（-WithoutMpv）' -ForegroundColor Yellow
}
Write-Host ("保留语言：{0}" -f ($keptLanguages -join ', '))
Write-Host ("删除语言 {0} 个：{1}" -f $removedLanguages.Count, (($removedLanguages | Sort-Object) -join ', ')) -ForegroundColor DarkGray
if (-not $KeepSymbols) { Write-Host ("删除调试符号 {0} 个。" -f $symbols.Count) -ForegroundColor DarkGray }
$sizeMb = (Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("未压缩体积：{0:N1} MB" -f $sizeMb)

if ($NoZip) { exit 0 }

# ---------------------------------------------------------------- 压缩
# 刻意不用 ZipFile.CreateFromDirectory：Windows PowerShell 5.1 用的是 .NET Framework 实现，
# 写出的条目名用反斜杠分隔（不符合 ZIP 规范，非 Windows 工具解开后会变成一层层怪文件名）。
# 这里自己写条目名，统一正斜杠；中文名仍由运行库打上 UTF-8 标志位。
function New-ReleaseZip {
    param([string]$SourceDirectory, [string]$DestinationPath)

    $baseLength = $SourceDirectory.TrimEnd('\').Length + 1
    $fileStream = [System.IO.File]::Open($DestinationPath, [System.IO.FileMode]::CreateNew)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($fileStream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File) {
                $entryName = $file.FullName.Substring($baseLength).Replace('\', '/')
                $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
                if ($file.LastWriteTime -ge [datetime]'1980-01-01') { $entry.LastWriteTime = $file.LastWriteTime }
                $entryStream = $entry.Open()
                $sourceStream = [System.IO.File]::OpenRead($file.FullName)
                try { $sourceStream.CopyTo($entryStream) }
                finally { $sourceStream.Dispose(); $entryStream.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $fileStream.Dispose() }
}

Write-Host ''
Write-Host '正在压缩 …' -ForegroundColor Cyan
New-ReleaseZip -SourceDirectory $StagingRoot -DestinationPath $ZipPath
$zipMb = (Get-Item -LiteralPath $ZipPath).Length / 1MB
Write-Host ("压缩包：{0}" -f $ZipPath) -ForegroundColor Green
Write-Host ("压缩包体积：{0:N1} MB" -f $zipMb)
exit 0
