@echo off
setlocal EnableExtensions

REM Publish the standalone Windows x64 NativeAOT editor.
REM Forward options such as -OutputDirectory and -SkipTests to the publisher.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-Avalonia.ps1" -Aot %*
set "BUILD_EXIT=%ERRORLEVEL%"
if not "%BUILD_EXIT%"=="0" (
    echo [ERROR] CrystalStudio NativeAOT publish failed with exit code %BUILD_EXIT%.
    echo See the publish log in "%~dp0Build\logs" for details.
    REM Keep an interactive window open; redirected automation must not wait.
    powershell.exe -NoLogo -NoProfile -Command "if (-not [Console]::IsInputRedirected) { Read-Host 'Press Enter to close this window' }"
    exit /b %BUILD_EXIT%
)

echo CrystalStudio NativeAOT build succeeded.
exit /b 0
