@echo off
chcp 65001 >nul 2>&1
setlocal enableextensions

rem ============================================================================
rem  构建并部署 StardewWikiAgent mod（Windows）。
rem
rem  用法:
rem    build.bat            Debug 构建 + 自动部署到游戏 Mods 目录
rem    build.bat release    Release 构建 + 生成发布 zip
rem    build.bat clean      清理 bin\ 与 obj\
rem
rem  说明:
rem    - Windows 下 dotnet 已在 PATH，无需像 build.sh 那样指定 DOTNET_ROOT 等。
rem    - Pathoschild.Stardew.ModBuildConfig 通常能自动定位 Stardew 安装目录并
rem      在构建后把 mod 文件拷到 Mods\StardewWikiAgent；如需覆盖，先设置环境
rem      变量 GamePath 再运行本脚本，例如:
rem        set "GamePath=D:\SteamLibrary\steamapps\common\Stardew Valley"
rem        build.bat
rem ============================================================================

rem 切到脚本所在目录（即仓库根），这样在任何位置调用都能正确工作。
cd /d "%~dp0"

set "CSPROJ=StardewWikiAgent.csproj"

set "MODE=%~1"
if "%MODE%"=="" set "MODE=debug"

if /i "%MODE%"=="clean" (
  echo [build] 清理 bin\ 与 obj\
  if exist bin rmdir /s /q bin
  if exist obj rmdir /s /q obj
  echo [build] 完成。
  exit /b 0
)

if /i "%MODE%"=="release" (
  set "CONFIGURATION=Release"
) else if /i "%MODE%"=="debug" (
  set "CONFIGURATION=Debug"
) else (
  echo [build] 未知参数: %MODE%  可用: debug / release / clean
  exit /b 2
)

echo [build] 构建配置: %CONFIGURATION%
echo.

if defined GamePath (
  echo [build] 使用指定的游戏路径: %GamePath%
  dotnet build "%CSPROJ%" --configuration %CONFIGURATION% -p:GamePath="%GamePath%"
) else (
  dotnet build "%CSPROJ%" --configuration %CONFIGURATION%
)

if errorlevel 1 (
  echo.
  echo [build] 构建失败。
  exit /b 1
)

echo.
echo [build] 构建成功，已部署到游戏的 Mods\StardewWikiAgent 目录。
if /i "%CONFIGURATION%"=="Release" echo [build] 发布 zip 位于 bin\%CONFIGURATION%\ 目录下。
exit /b 0
