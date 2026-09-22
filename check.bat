@echo off
rem The one check command: the same steps CI runs (.github/workflows/ci.yml),
rem so a green check.bat means a green PR. Run it before claiming work is done.
rem   check.bat            build Release + all tests
rem   check.bat core       just tests/OrdoSort.Core.Tests (fast)
rem   check.bat wpf        just tests/OrdoSort.Wpf.Tests
rem Exit code passes through (0 pass, non-zero fail).
cd /d "%~dp0"

set "TARGET=OrdoSort.sln"
if /i "%~1"=="core" set "TARGET=tests\OrdoSort.Core.Tests"
if /i "%~1"=="wpf"  set "TARGET=tests\OrdoSort.Wpf.Tests"

dotnet restore %TARGET% || exit /b 1
dotnet build %TARGET% --no-restore -c Release || exit /b 1
dotnet test %TARGET% --no-build -c Release
exit /b %ERRORLEVEL%
