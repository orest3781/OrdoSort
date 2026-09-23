@echo off
rem The one check command: the same steps CI runs (.github/workflows/ci.yml),
rem so a green check.bat means a green PR. Run it before claiming work is done.
rem   check.bat            format check + build Release + all tests
rem   check.bat core       format check + just tests/OrdoSort.Core.Tests (fast)
rem   check.bat wpf        format check + just tests/OrdoSort.Wpf.Tests
rem Exit code passes through (0 pass, non-zero fail).
cd /d "%~dp0"

set "TARGET=OrdoSort.sln"
if /i "%~1"=="core" set "TARGET=tests\OrdoSort.Core.Tests"
if /i "%~1"=="wpf"  set "TARGET=tests\OrdoSort.Wpf.Tests"

dotnet restore OrdoSort.sln || exit /b 1
rem Cheapest check first, and always the whole solution, so "core" still
rem catches a badly formatted file under src\. Rules live in .editorconfig.
rem Whitespace and code style only: analyzer warnings are the build's job.
dotnet format whitespace OrdoSort.sln --verify-no-changes --no-restore -v q
if errorlevel 1 goto :format_failed
dotnet format style OrdoSort.sln --verify-no-changes --no-restore -v q
if errorlevel 1 goto :format_failed
goto :format_ok
:format_failed
(
    echo.
    echo Formatting check failed. Fix it with:  dotnet format OrdoSort.sln
    exit /b 1
)
:format_ok
dotnet build %TARGET% --no-restore -c Release || exit /b 1
dotnet test %TARGET% --no-build -c Release
exit /b %ERRORLEVEL%
