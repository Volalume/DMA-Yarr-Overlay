@echo off
setlocal

pushd "%~dp0"
set "PROJECT_FILE=%~dp0Overlay\Overlay.csproj"
set "OUTPUT_DIR=%~dp0publish\single-file"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] .NET SDK was not found. Install the .NET 10 SDK and try again.
    goto :failed
)

if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"

echo Building YarrOverlay as a self-contained single EXE...
dotnet publish "%PROJECT_FILE%" ^
    --configuration Release ^
    --runtime win-x64 ^
    --self-contained true ^
    --output "%OUTPUT_DIR%" ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:IncludeAllContentForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugSymbols=false ^
    -p:DebugType=None

if errorlevel 1 goto :failed

echo.
echo Build completed:
echo %OUTPUT_DIR%\YarrOverlay.exe
goto :finished

:failed
echo.
echo Build failed.
popd
if /i not "%~1"=="--no-pause" pause
exit /b 1

:finished
popd
if /i not "%~1"=="--no-pause" pause
exit /b 0
