@echo off
rem =====================================================================================
rem  publish.cmd - builds the portable executable (one .exe, .NET included) in
rem  publish\win-x64, with the example configurations next to it (Examples folder).
rem  Usage: double-click, or run "publish.cmd" from the project folder.
rem =====================================================================================

setlocal
cd /d "%~dp0"

dotnet publish App\DmxController.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish\win-x64
if errorlevel 1 exit /b 1

rem Example configurations (open them with File > Open / import)
if exist publish\win-x64\Examples rmdir /s /q publish\win-x64\Examples
xcopy /y /i /q examples\*.json publish\win-x64\Examples\ >nul
copy /y examples\README.txt publish\win-x64\README.txt >nul

echo.
echo Done: publish\win-x64\DmxController.exe (+ Examples folder)
