@echo off
REM ============================================================
REM  LabelGou one-key build script
REM  Usage:
REM     compile-labelgou.bat                (build Debug, incl. tests)
REM     compile-labelgou.bat run            (build + launch WPF app)
REM     compile-labelgou.bat test           (build + run unit tests)
REM     compile-labelgou.bat release        (build Release)
REM     compile-labelgou.bat publish        (self-contained win-x64 publish to artifacts\publish)
REM     compile-labelgou.bat clean
REM  Note: this machine has no Visual Studio; the .NET 8 SDK is installed
REM  (admin-free, zip layout) at D:\dev\dotnet-sdk. Fall back to PATH dotnet.
REM ============================================================
setlocal
cd /d "%~dp0"

set "DOTNET_EXE=D:\dev\dotnet-sdk\dotnet.exe"
if not exist "%DOTNET_EXE%" set "DOTNET_EXE=dotnet"
if not exist "%DOTNET_EXE%" (
  where dotnet >nul 2>nul
  if errorlevel 1 (
    echo [ERROR] .NET SDK not found. Expected "%DOTNET_EXE%" or dotnet on PATH.
    exit /b 2
  )
  set "DOTNET_EXE=dotnet"
)

echo [INFO] using: %DOTNET_EXE%

if "%~1"=="" goto build_debug
if /i "%~1"=="run"     goto cmd_run
if /i "%~1"=="test"    goto cmd_test
if /i "%~1"=="release" goto cmd_release
if /i "%~1"=="publish" goto cmd_publish
if /i "%~1"=="clean"   goto cmd_clean
echo [ERROR] unknown argument: %~1
exit /b 64

:build_debug
call "%DOTNET_EXE%" build LabelGou.sln -c Debug -v minimal
goto end

:cmd_run
call "%DOTNET_EXE%" run --project src\LabelGou.App\LabelGou.App.csproj -c Debug
goto end

:cmd_test
call "%DOTNET_EXE%" test tests\LabelGou.Core.Tests\LabelGou.Core.Tests.csproj -c Debug -v minimal
goto end

:cmd_release
call "%DOTNET_EXE%" build LabelGou.sln -c Release -v minimal
goto end

:cmd_publish
call "%DOTNET_EXE%" publish src\LabelGou.App\LabelGou.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts\publish\labelgou-win-x64
goto end

:cmd_clean
for /d %%d in (src tests) do (
  if exist "%%d" for /d %%p in ("%%d\*") do (
    if exist "%%p\bin" rmdir /s /q "%%p\bin"
    if exist "%%p\obj" rmdir /s /q "%%p\obj"
  )
)
echo [INFO] cleaned bin/obj under src and tests
goto end

:end
set "RC=%ERRORLEVEL%"
echo [INFO] exit code: %RC%
exit /b %RC%
