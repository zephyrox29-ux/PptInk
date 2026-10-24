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
$packageName = "PptInk-v1.0.0Beta-portable-$Runtime"
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
Copy-Item (Join-Path $repoRoot '试用必读.txt') $packageRoot -Force
Copy-Item (Join-Path $repoRoot 'docs\工程审计与整改报告.md') $packageRoot -Force
Copy-Item (Join-Path $repoRoot 'docs\触摸交互整改说明.md') $packageRoot -Force
Copy-Item (Join-Path $repoRoot 'docs\设置中心与交互体验增强说明.md') $packageRoot -Force
Copy-Item (Join-Path $repoRoot 'docs\本轮界面定位与快捷面板整改说明.md') $packageRoot -Force

$firstRead = @'
PptInk v1.0.0Beta 便携版（Windows x64）

1. 解压整个目录后运行 app\PptInk.exe。
2. 主程序是 self-contained .NET 发布，不要求目标机器预装 .NET Runtime。
3. 手写、工具栏、擦除、撤销及 PPT 相关功能请在实际 Windows 环境验收。
4. 本包默认不含 Python 解释器。AI 形状识别需要安装 Python 3.10+ 与 bridge\requirements.txt，或在打包时向 build-portable.ps1 提供完整嵌入式运行时目录。
5. 插件样例位于 Examples\plugins\timer。通过应用内插件安装入口导入前，先将样例目录压缩为 ZIP，且 ZIP 根目录应直接包含 manifest.json 与 main.lua。
6. 该包不包含希沃硬件验证声明；首次使用需检查系统缩放、触控笔迹、穿透与导出行为。
'@
Set-Content -Path (Join-Path $packageRoot '便携版-先读.txt') -Value $firstRead -Encoding UTF8

# Root-level launcher so the portable package is easy to open from a classroom desktop.
$launcher = @'
@echo off
setlocal
start "" "%~dp0app\PptInk.exe" %*
'@
Set-Content -Path (Join-Path $packageRoot '启动融绘.bat') -Value $launcher -Encoding ASCII

Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Portable package created: $zipPath"
Write-Host 'Note: the Python runtime and AI packages are bundled only when -PythonRuntimePath is supplied.'
