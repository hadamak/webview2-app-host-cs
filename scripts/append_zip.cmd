@echo off
setlocal EnableExtensions

if "%~3"=="" (
  echo Usage: %~nx0 ^<exe^> ^<zip^> ^<output^>
  exit /b 1
)

if not exist "%~1" (
  echo ERROR: EXE not found: %~1
  exit /b 1
)

if not exist "%~2" (
  echo ERROR: ZIP not found: %~2
  exit /b 1
)

if exist "%~3" del /f /q "%~3" >nul 2>nul

copy /b "%~1"+"%~2" "%~3" >nul
if errorlevel 1 (
  echo ERROR: copy /b failed
  exit /b 1
)

echo Bundled: %~3
exit /b 0
