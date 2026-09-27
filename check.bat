@echo off
rem The one check command: the same steps CI runs (.github/workflows/ci.yml),
rem so a green check.bat means a green PR. Run it before claiming work is done.
rem   check.bat              format check + build Release + everyday tests
rem   check.bat core         format check + just tests/OrdoSort.Core.Tests (fast)
rem   check.bat wpf          format check + just tests/OrdoSort.Wpf.Tests
rem   check.bat integration  format check + only the tests that start real
rem                          Edge or Office (Category=Integration)
rem   check.bat all          format check + everyday and integration tests
rem Everyday runs leave out Integration: those depend on what is installed on
rem the machine, not on the code (docs/testing.md).
rem Exit code passes through (0 pass, non-zero fail).
cd /d "%~dp0"

set "TARGET=OrdoSort.sln"
if /i "%~1"=="core" set "TARGET=tests\OrdoSort.Core.Tests"
if /i "%~1"=="wpf"  set "TARGET=tests\OrdoSort.Wpf.Tests"
set "FILTER=Category!=Integration"
if /i "%~1"=="integration" set "FILTER=Category=Integration"
if /i "%~1"=="all" set "FILTER="

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
if defined FILTER (
    dotnet test %TARGET% --no-build -c Release --filter "%FILTER%"
) else (
    dotnet test %TARGET% --no-build -c Release
)
exit /b %ERRORLEVEL%
