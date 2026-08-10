@echo off
setlocal
cd /d "%~dp0"

set "EXE=%~dp0App\PhotogrammetryCloudJobSync.exe"
if not exist "%EXE%" (
  echo Building Release app into App\ folder...
  dotnet build -c Release --nologo -v q
  if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
  )
)

if not exist "%EXE%" (
  echo Could not find "%EXE%"
  pause
  exit /b 1
)

start "" "%EXE%"
exit /b 0
