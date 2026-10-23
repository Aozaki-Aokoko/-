# 重新编译本插件（需要 .NET Framework 4.x 自带的 csc.exe，Windows 自带）
# 用法：右键“使用 PowerShell 运行”，或在命令行执行：
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw '找不到 csc.exe：本机缺少 .NET Framework 4.x。'
}

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'dist\坐标覆盖插件.exe'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
    "/win32manifest:$PSScriptRoot\app.manifest" `
    "/out:$out" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "$PSScriptRoot\Program.cs"

if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE）。" }
Write-Host "编译成功：$out"
