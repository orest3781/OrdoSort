@echo off
rem Build the portable single-file exe locally: publish\OrdoSort.exe
rem (~5 MB; needs the .NET 8 Desktop Runtime, already on modern Windows).
cd /d "%~dp0"
dotnet publish src\OrdoSort.Wpf -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish
if errorlevel 1 ( echo Publish failed. & pause & exit /b 1 )
echo.
echo Portable exe: %~dp0publish\OrdoSort.exe
echo Drop it anywhere; it reads (or creates) a config.json beside itself,
echo or pass one:  OrdoSort.exe --config C:\path\config.json
