@echo off
rem Build the portable Box Labels exe locally: publish-boxlabels\BoxLabels.exe
rem (needs the .NET 8 Desktop Runtime, already on modern Windows).
rem
rem This is the label maker on its own, for someone who needs box labels and
rem nothing else from OrdoSort. Hand over the whole publish-boxlabels folder.
cd /d "%~dp0.."
dotnet publish src\BoxLabels.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish-boxlabels
if errorlevel 1 ( echo Publish failed. & pause & exit /b 1 )
echo.
echo Box Labels: %CD%\publish-boxlabels\BoxLabels.exe
echo.
echo On first run it asks for the shared box-labels.json — the same file
echo OrdoSort prints from, so both stay in step on the running box number.
echo To pre-point it before handing it over, run it once and choose the file,
echo or write box-labels-app.json beside the exe:
echo     { "box_labels_file": "\\server\records\box-labels.json" }
echo Run against a different store just once:  BoxLabels.exe --file C:\path\box-labels.json
