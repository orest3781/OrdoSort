@echo off
rem Launch OrdoSort. With no argument it prefers the full workbench in
rem demo-full\ and falls back to the small demo\ sketch; pass a path to run
rem against your own:   run.bat C:\OrdoSort\config.json
rem
cd /d "%~dp0"

set "CONFIG=%~1"
if "%CONFIG%"=="" (
    if exist "%~dp0demo-full\config.json" (
        set "CONFIG=%~dp0demo-full\config.json"
    ) else (
        set "CONFIG=%~dp0demo\config.json"
    )
)

if not exist "%CONFIG%" (
    echo Config not found: %CONFIG%
    echo.
    echo Run  demo-full.bat  for the full workbench ^(ten routes, 300 documents^),
    echo or   reset.bat      for the five-document sketch,
    echo or pass your own config path.
    pause
    exit /b 1
)

set "EXE=%~dp0src\OrdoSort.Wpf\bin\Debug\net8.0-windows\OrdoSort.exe"

rem Always build. This used to build only when the exe was missing, which
rem meant run.bat would happily launch a binary from days ago and show you
rem code you had already changed - the worst kind of wrong, because nothing
rem says so. An incremental build costs about a second when nothing changed.
echo Building OrdoSort...
dotnet build src\OrdoSort.Wpf\OrdoSort.Wpf.csproj -c Debug -v quiet
if errorlevel 1 ( echo Build failed. & pause & exit /b 1 )

echo Config: %CONFIG%
start "" "%EXE%" --config "%CONFIG%"
