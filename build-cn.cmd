@echo off
REM 国服（XIVLauncherCN）编译脚本，CI 上用 -p:DalamudLibPath 覆盖
setlocal
set "CN_LIB=%APPDATA%\XIVLauncherCN\addon\Hooks\dev/"

if not exist "%CN_LIB%Dalamud.dll" (
  echo [x] 找不到国服 Dalamud: %CN_LIB%
  exit /b 1
)

dotnet build "%~dp0MentorRoulette.csproj" -p:DalamudLibPath="%CN_LIB%" %*
if errorlevel 1 (
  echo [x] 编译失败
  exit /b 1
)
echo [ok] 编译完成，产物在 %~dp0bin\ 下
endlocal
