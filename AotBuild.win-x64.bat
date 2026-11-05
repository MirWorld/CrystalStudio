@echo off
setlocal EnableExtensions

REM Use the same Windows x64 NativeAOT target as Server.Avalonia.
REM Forward options such as -OutputDirectory and -SkipTests to the publisher.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-Avalonia.ps1" -Aot %*
set "BUILD_EXIT=%ERRORLEVEL%"
if not "%BUILD_EXIT%"=="0" (
    echo [ERROR] CrystalStudio NativeAOT publish failed with exit code %BUILD_EXIT%.
    exit /b %BUILD_EXIT%
)

echo CrystalStudio NativeAOT build succeeded.
exit /b 0
