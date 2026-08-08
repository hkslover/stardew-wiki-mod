#!/usr/bin/env bash
#
# 构建并部署 StardewWikiAgent mod。
#
# 用法:
#   ./build.sh            # Debug 构建 + 自动部署到游戏 Mods 目录
#   ./build.sh release    # Release 构建 + 生成发布 zip
#   ./build.sh clean      # 清理 bin/ 与 obj/
#
# 说明:
#   - dotnet 不在默认 PATH,这里用显式 SDK 路径,并把 NuGet 缓存 / CLI home
#     指向仓库内的 gitignored 目录,避免污染全局环境。
#   - Pathoschild.Stardew.ModBuildConfig 负责解析 SMAPI/Stardew 引用程序集,
#     并在构建后把 DLL + manifest.json 自动拷贝到游戏的 Mods/StardewWikiAgent。
#   - 本机游戏是 macOS 的 .app 结构,ModBuildConfig 无法自动定位,所以这里
#     显式传入 GamePath 指向 Contents/MacOS(内含 SMAPI 与 Mods 目录)。

set -euo pipefail

# 切到脚本所在目录(即仓库根),这样在任何位置调用都能正确工作。
cd "$(dirname "$0")"

# ---- 环境:显式 SDK 与仓库内缓存 -------------------------------------------
export DOTNET_ROOT="/usr/local/share/dotnet"
export DOTNET_CLI_HOME="$PWD/.dotnet-cli-home"
export NUGET_PACKAGES="$PWD/.nuget-packages"
DOTNET="$DOTNET_ROOT/dotnet"

# ---- 游戏路径:让 ModBuildConfig 找到 SMAPI 并部署 --------------------------
# 若已在环境里设置 GamePath 则尊重之,否则用本机默认 Steam 安装位置。
GAME_PATH="${GamePath:-/Users/snow/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS}"

CSPROJ="ConsoleHelloMod.csproj"

if [ ! -x "$DOTNET" ]; then
  echo "错误:找不到 dotnet:$DOTNET" >&2
  echo "请确认 .NET SDK 已安装到 $DOTNET_ROOT" >&2
  exit 1
fi

mode="${1:-debug}"

case "$mode" in
  clean)
    echo ">> 清理 bin/ 与 obj/"
    rm -rf bin obj
    echo ">> 完成。"
    exit 0
    ;;
  release)
    configuration="Release"
    ;;
  debug)
    configuration="Debug"
    ;;
  *)
    echo "未知参数:$mode(可用:debug | release | clean)" >&2
    exit 2
    ;;
esac

if [ ! -d "$GAME_PATH" ]; then
  echo "警告:游戏路径不存在:$GAME_PATH" >&2
  echo "      构建仍会尝试,但自动部署可能失败。" >&2
  echo "      可通过环境变量覆盖,例如:" >&2
  echo "        GamePath=\"/你的/Stardew Valley/Contents/MacOS\" ./build.sh" >&2
fi

echo ">> 构建配置: $configuration"
echo ">> 游戏路径: $GAME_PATH"
echo

"$DOTNET" build "$CSPROJ" \
  --configuration "$configuration" \
  -p:GamePath="$GAME_PATH"

echo
echo ">> 构建成功。"
echo ">> 已部署到: $GAME_PATH/Mods/StardewWikiAgent"
if [ "$configuration" = "Release" ]; then
  echo ">> 发布 zip 位于: bin/$configuration/StardewWikiAgent *.zip"
fi
