@echo off
setlocal EnableExtensions

cd /d "%~dp0"

set "SOLUTION=SupportCaseManager.slnx"
set "CONFIG=Debug"
set "STOP_RUNNING=1"

:parse_args
if "%~1"=="" goto :args_done
if /I "%~1"=="Debug" (
    set "CONFIG=Debug"
    shift
    goto :parse_args
)
if /I "%~1"=="Release" (
    set "CONFIG=Release"
    shift
    goto :parse_args
)
if /I "%~1"=="--stop-running" (
    set "STOP_RUNNING=1"
    shift
    goto :parse_args
)
if /I "%~1"=="--no-stop-running" (
    set "STOP_RUNNING=0"
    shift
    goto :parse_args
)
if /I "%~1"=="/?" goto :usage
if /I "%~1"=="-h" goto :usage
if /I "%~1"=="--help" goto :usage
echo Unknown argument: %~1
goto :usage

:args_done

where dotnet >nul 2>nul
if errorlevel 1 (
    echo dotnet CLI was not found. Install the .NET SDK and try again.
    exit /b 1
)

if not exist "%SOLUTION%" (
    echo %SOLUTION% was not found. Run this script from the project root directory.
    exit /b 1
)

call :check_running_apps
if errorlevel 1 exit /b 1

echo Restoring %SOLUTION%...
dotnet restore "%SOLUTION%"
if errorlevel 1 goto :failed

echo Building %SOLUTION% -c %CONFIG%...
dotnet build "%SOLUTION%" -c "%CONFIG%" --no-restore
if errorlevel 1 goto :failed

echo.
echo Build succeeded. Configuration: %CONFIG%
exit /b 0

:check_running_apps
powershell -NoProfile -ExecutionPolicy Bypass -Command "$root=(Resolve-Path -LiteralPath '.').Path; $stop='%STOP_RUNNING%' -eq '1'; $names=@('SupportCaseManager.App','SupportCaseManager.AiAssistant.App'); $apps=@(Get-Process -Name $names -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and $_.Path -like '*\bin\*' }); if (-not $apps) { exit 0 }; if (-not $stop) { Write-Host ''; Write-Host 'Build output is locked by running app process(es):'; $apps | Select-Object Id,ProcessName,Path | Format-Table -AutoSize; Write-Host 'Close the app and run build.bat again, or allow auto-close by running without --no-stop-running.'; exit 1 }; Write-Host 'Closing running app process(es) for build:'; $apps | Select-Object Id,ProcessName,Path | Format-Table -AutoSize; foreach ($app in $apps) { if ($app.MainWindowHandle -ne 0) { [void]$app.CloseMainWindow() } }; Start-Sleep -Seconds 3; $remaining=@(Get-Process -Id $apps.Id -ErrorAction SilentlyContinue); if ($remaining) { Write-Host 'Force-stopping remaining process(es):'; $remaining | Select-Object Id,ProcessName | Format-Table -AutoSize; $remaining | Stop-Process -Force; Start-Sleep -Seconds 1 }; exit 0"
if errorlevel 1 (
    echo.
    echo Build was not started because the target output is locked.
    exit /b 1
)
exit /b 0

:usage
echo Usage: build.bat [Debug^|Release] [--no-stop-running]
echo.
echo Examples:
echo   build.bat
echo   build.bat Release
echo   build.bat Debug --no-stop-running
exit /b 2

:failed
echo.
echo Build failed. If DLL files are still locked, close SupportCaseManager.App or SupportCaseManager.AiAssistant.App and run this again.
exit /b 1
