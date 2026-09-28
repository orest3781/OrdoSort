@echo off
rem Build the portable single-file exe locally: publish\OrdoSort.exe
rem (~7 MB; needs the .NET 8 Desktop Runtime installed. Windows ships .NET
rem Framework, not .NET 8; where it is missing, the exe shows a download link
rem when started. The release's self-contained zip carries its own runtime.)
cd /d "%~dp0.."
dotnet publish src\OrdoSort.Wpf -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish
if errorlevel 1 ( echo Publish failed. & pause & exit /b 1 )
echo.
echo Portable exe: %CD%\publish\OrdoSort.exe
echo Drop it anywhere; it reads (or creates) a config.json beside itself,
echo or pass one:  OrdoSort.exe --config C:\path\config.json
