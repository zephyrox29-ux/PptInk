[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$PythonRuntimePath = '',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\PptInk.App\PptInk.App.csproj'
$artifactRoot = Join-Path $repoRoot 'artifacts'

# 版本号单一来源：从 csproj 读取（v1.1.0 统一 —— 原先脚本/manifest/UA 各写各的，排障时
# 无法从日志、HTTP UA、文件属性判断用户实际跑的哪个版本）
$csprojContent = Get-Content -LiteralPath $appProject -Raw
if ($csprojContent -notmatch '<Version>(\d+\.\d+\.\d+[^<]*)</Version>') {
    throw "Cannot parse <Version> from $appProject"
}
$Version = $Matches[1]

$packageName = "PptInk-v$Version-portable-$Runtime"
$packageRoot = Join-Path $artifactRoot $packageName
$publishRoot = Join-Path $packageRoot 'app'
$zipPath = Join-Path $artifactRoot "$packageName.zip"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 8 SDK is required. Install the SDK on Windows, then rerun this script.'
}
if (-not (Test-Path $appProject)) { throw "Project not found: $appProject" }
if ($PythonRuntimePath) {
    $pythonExe = Join-Path $PythonRuntimePath 'python.exe'
    if (-not (Test-Path $pythonExe)) {
        throw "PythonRuntimePath must be a complete Windows Python runtime directory containing python.exe: $PythonRuntimePath"
    }
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
if (Test-Path $packageRoot) { Remove-Item $packageRoot -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

$publishArgs = @(
    'publish', $appProject,
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '--output', $publishRoot
)
if ($NoRestore) { $publishArgs += '--no-restore' }
Write-Host ('dotnet ' + ($publishArgs -join ' '))
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# Python is optional for the core WPF pen/ink experience. Bundle the protocol and model files
# next to the app; a matching embedded runtime can be supplied by organizations that deploy AI.
$bridgeRoot = Join-Path $publishRoot 'bridge'
New-Item -ItemType Directory -Path $bridgeRoot -Force | Out-Null
$pythonSource = Join-Path $repoRoot 'python'
foreach ($file in @('ink_bridge.py', 'pptink_recog.py', 'recognizer_config.json', 'requirements.txt')) {
    $source = Join-Path $pythonSource $file
    if (-not (Test-Path $source)) { throw "Required Python bridge file missing: $source" }
    Copy-Item $source $bridgeRoot -Force
}
if ($PythonRuntimePath) {
    $runtimeDest = Join-Path $bridgeRoot 'python'
    New-Item -ItemType Directory -Path $runtimeDest -Force | Out-Null
    Copy-Item (Join-Path $PythonRuntimePath '*') $runtimeDest -Recurse -Force
}

$sampleRoot = Join-Path $packageRoot 'Examples\plugins'
New-Item -ItemType Directory -Path $sampleRoot -Force | Out-Null
Copy-Item (Join-Path $repoRoot 'plugins\timer') $sampleRoot -Recurse -Force
Copy-Item (Join-Path $repoRoot 'README.md') $packageRoot -Force
# v1.1.0：AGPL 分发合规 —— 包内随附许可证与第三方声明（原先只复制 README）。
# 另：旧脚本此处曾复制 5 个 v0.9.8 就删掉的说明文档（试用必读.txt 等），CI 打包必失败。
Copy-Item (Join-Path $repoRoot 'LICENSE') $packageRoot -Force
Copy-Item (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') $packageRoot -Force

$firstRead = @"
PptInk v$Version 便携版（Windows x64）

1. 解压整个目录后运行「启动融绘.bat」或 app\PptInk.exe（便携模式：不写开机自启）。
2. 主程序是 self-contained .NET 发布，不要求目标机器预装 .NET Runtime。
3. 手写、工具栏、擦除、撤销及 PPT 相关功能请在实际 Windows 环境验收。
4. 本包默认不含 Python 解释器。AI 形状识别需要安装 Python 3.10+ 与 bridge\requirements.txt，或在打包时向 build-portable.ps1 提供完整嵌入式运行时目录。
5. 插件样例位于 Examples\plugins\timer。通过应用内插件安装入口导入前，先将样例目录压缩为 ZIP，且 ZIP 根目录应直接包含 manifest.json 与 main.lua。
6. 该包不包含希沃硬件验证声明；首次使用需检查系统缩放、触控笔迹、穿透与导出行为。
"@
Set-Content -Path (Join-Path $packageRoot '便携版-先读.txt') -Value $firstRead -Encoding UTF8

# Root-level launcher so the portable package is easy to open from a classroom desktop.
# v1.1.0：补 --portable —— 原先便携包首跑就写 HKCU Run 自启，与"zip 解压即用、不留常驻项"
# 的设计矛盾；目录被删/移走后 Windows 每次开机都在启动一个不存在的 exe。
$launcher = @'
@echo off
setlocal
start "" "%~dp0app\PptInk.exe" --portable %*
'@
Set-Content -Path (Join-Path $packageRoot '启动融绘.bat') -Value $launcher -Encoding ASCII

Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Portable package created: $zipPath"
Write-Host 'Note: the Python runtime and AI packages are bundled only when -PythonRuntimePath is supplied.'
