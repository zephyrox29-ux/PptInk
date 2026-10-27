# 一键编译最新版本（假设已安装 .NET 8 SDK）。
#
#   pwsh ./scripts/build-latest.ps1                 # restore + build + 测试
#   pwsh ./scripts/build-latest.ps1 -SkipTests      # 只编译
#   pwsh ./scripts/build-latest.ps1 -Portable       # 编译后直接打便携包
#   pwsh ./scripts/build-latest.ps1 -Configuration Debug
#
# 非 Windows 会自动加 EnableWindowsTargeting 做交叉编译；WPF 测试需要 Windows
# 运行时，因此非 Windows 只运行可执行的 Plugin Studio 测试。
param(
    [switch]$SkipTests,
    [switch]$Portable,
    [string]$Runtime = "win-x64",
    [ValidateSet("Release", "Debug")][string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "未找到 dotnet：请先安装 .NET 8 SDK（https://dotnet.microsoft.com/download）" -ForegroundColor Red
    exit 2
}

$ver = "?"
try {
    [xml]$proj = Get-Content "src/PptInk.App/PptInk.App.csproj"
    $ver = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
} catch { }

$isWindowsHost = $true
if ($PSVersionTable.PSVersion.Major -ge 6) { $isWindowsHost = $IsWindows }

$extra = @()
if (-not $isWindowsHost) { $extra += "-p:EnableWindowsTargeting=true" }

Write-Host "== PptInk $ver · $Configuration ==" -ForegroundColor Cyan

Write-Host "[1/3] restore" -ForegroundColor Cyan
dotnet restore PptInk.sln @extra
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "[2/3] build" -ForegroundColor Cyan
dotnet build PptInk.sln -c $Configuration --no-restore @extra
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $SkipTests) {
    Write-Host "[3/3] test" -ForegroundColor Cyan
    if ($isWindowsHost) {
        dotnet test PptInk.sln -c $Configuration --no-build @extra
    } else {
        # Microsoft.WindowsDesktop.App 仅 Windows 提供：非 Windows 只跑可执行的测试项目
        dotnet test "tools/PptInk.PluginStudio.Tests/PptInk.PluginStudio.Tests.csproj" `
            -c $Configuration --no-build
    }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} else {
    Write-Host "[3/3] test（已跳过）" -ForegroundColor DarkGray
}

if ($Portable) {
    Write-Host "打包便携包…" -ForegroundColor Cyan
    & "$root/scripts/build-portable.ps1" -Configuration $Configuration -Runtime $Runtime
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "== 完成 ==" -ForegroundColor Green
Write-Host "产物：src/PptInk.App/bin/$Configuration/net8.0-windows/PptInk.exe"