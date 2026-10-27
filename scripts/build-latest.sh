#!/usr/bin/env bash
# 一键编译最新版本（假设已安装 .NET 8 SDK）。
#
#   ./scripts/build-latest.sh                 # restore + build + 测试
#   SKIP_TESTS=1 ./scripts/build-latest.sh    # 只编译
#   CONFIG=Debug ./scripts/build-latest.sh    # Debug 配置
#
# Linux/macOS 会自动加 EnableWindowsTargeting 做交叉编译；WPF 测试需要 Windows
# 运行时，因此非 Windows 只运行可执行的 Plugin Studio 测试。
set -eo pipefail
cd "$(dirname "$0")/.."

if ! command -v dotnet >/dev/null 2>&1; then
  echo "未找到 dotnet：请先安装 .NET 8 SDK（https://dotnet.microsoft.com/download）" >&2
  exit 2
fi

VER=$(grep -oP '(?<=<Version>)[^<]+' src/PptInk.App/PptInk.App.csproj | head -1 || echo "?")
CONFIG=${CONFIG:-Release}
HOST=$(uname -s)
EXTRA=()
if [ "${HOST}" != "Windows_NT" ] && [ "${HOST}" != "MINGW64_NT" ] && [ "${HOST}" != "MSYS_NT" ]; then
  EXTRA+=("-p:EnableWindowsTargeting=true")
fi

echo "== PptInk ${VER} · ${CONFIG} · ${HOST} =="

echo "[1/3] restore"
dotnet restore PptInk.sln "${EXTRA[@]}"

echo "[2/3] build"
dotnet build PptInk.sln -c "${CONFIG}" --no-restore "${EXTRA[@]}"

if [ "${SKIP_TESTS:-0}" != "1" ]; then
  echo "[3/3] test"
  case "${HOST}" in
    Windows_NT|MINGW64_NT|MSYS_NT)
      dotnet test PptInk.sln -c "${CONFIG}" --no-build "${EXTRA[@]}"
      ;;
    *)
      # Microsoft.WindowsDesktop.App 仅 Windows 提供：Linux/macOS 跑不了 WPF 测试
      dotnet test tools/PptInk.PluginStudio.Tests/PptInk.PluginStudio.Tests.csproj \
        -c "${CONFIG}" --no-build
      ;;
  esac
else
  echo "[3/3] test（已跳过）"
fi

echo "== 完成 =="
echo "产物：src/PptInk.App/bin/${CONFIG}/net8.0-windows/PptInk.exe"
echo "便携包：pwsh ./scripts/build-portable.ps1 -Configuration ${CONFIG} -Runtime win-x64"