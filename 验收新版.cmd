@echo off
setlocal
set "DOTNET_ROOT=%~dp0artifacts\tools\dotnet"
set "DESKFRZA_TEST_APP=%~dp0artifacts\acceptance-build-notifications\DeskFrzA.exe"
if not exist "%DESKFRZA_TEST_APP%" (
    echo Release build not found. Build DeskFrz/DeskFrz.csproj first.
    pause
    exit /b 1
)
if not exist "%DOTNET_ROOT%\dotnet.exe" (
    echo Local .NET runtime not found: %DOTNET_ROOT%
    pause
    exit /b 1
)
start "" "%DESKFRZA_TEST_APP%" --data "%~dp0artifacts\acceptance-20261113"
