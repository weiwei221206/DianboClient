# 点波音乐开发版：交给 MSBuild 判断依赖是否变化，固定启动 x64 Release。
[CmdletBinding()]
param([switch]$BuildOnly)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

try {
    $dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet)) {
        $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    }
    $project = Join-Path $root 'src\Dianbo.App\Dianbo.App.csproj'
    Write-Host '正在检查并增量构建点波音乐…' -ForegroundColor Cyan
    & $dotnet build $project -c Release -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) {
        throw '构建失败。若点波音乐仍在运行，请从托盘完全退出后重试，并查看上方错误。'
    }
    $exe = Join-Path $root 'src\Dianbo.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\Dianbo.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw '构建完成，但没有找到 Dianbo.exe。' }
    if (-not $BuildOnly) {
        Start-Process -FilePath $exe -WorkingDirectory $root
        Write-Host '已发出启动请求；若程序已在运行，将切回已有窗口。' -ForegroundColor Green
    }
    exit 0
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    if (-not $BuildOnly) { Read-Host '按回车关闭' | Out-Null }
    exit 1
}
