@echo off
rem Build the full-size workbench in demo-full\: ten routes, a deep inbox, and
rem a folder per tool stocked with the awkward cases (locked PDFs, messy
rem filenames, a roster with ambiguous and suggested rows that open Review
rem matches). Real documents are never touched — everything lives under
rem demo-full\.
rem
rem   demo-full.bat          300 inbox documents (default)
rem   demo-full.bat 2000     a deeper inbox
cd /d "%~dp0"
dotnet run --project tools\OrdoSort.Smoke -- demo-full %1
if errorlevel 1 pause
