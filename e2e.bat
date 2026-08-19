@echo off
rem Run the end-to-end demonstration suite. With no argument it drives all
rem 12 surfaces as real windows against real files; pass a surface name to
rem run just one, and --keep to preserve the scenario fixtures:
rem   e2e.bat
rem   e2e.bat zip
rem   e2e.bat "match and merge" --keep
rem
rem Writes evidence\<timestamp>\report.html and opens it when the run ends -
rem most useful on the runs that fail, which is when the report matters.
rem Exit code passes through (0 pass, 1 fail), so this stays scriptable.
cd /d "%~dp0"

dotnet run --project tools\OrdoSort.Smoke -- e2e %*
set "CODE=%ERRORLEVEL%"

rem Newest run first: the yyyyMMdd-HHmmss stamps sort chronologically by name.
for /f "delims=" %%D in ('dir /b /ad /o-n evidence 2^>nul') do (
    if exist "evidence\%%D\report.html" (
        start "" "evidence\%%D\report.html"
        goto :done
    )
)
:done
exit /b %CODE%
