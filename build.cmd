@echo off
rem Wrapper: the agent shell has a stripped Windows environment (no LOCALAPPDATA /
rem ProgramFiles / SystemRoot / PATHEXT), which makes NuGet's restore task fail with
rem "Value cannot be null. (Parameter 'path1')". Restore the essentials, then delegate.
setlocal
set PATHEXT=.COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC
if "%LOCALAPPDATA%"=="" set LOCALAPPDATA=C:\Users\%USERNAME%\AppData\Local
if "%APPDATA%"=="" set APPDATA=C:\Users\%USERNAME%\AppData\Roaming
if "%ProgramFiles%"=="" set ProgramFiles=C:\Program Files
if "%ProgramFiles(x86)%"=="" set "ProgramFiles(x86)=C:\Program Files (x86)"
if "%ProgramW6432%"=="" set ProgramW6432=C:\Program Files
if "%ProgramData%"=="" set ProgramData=C:\ProgramData
if "%SystemRoot%"=="" set SystemRoot=C:\Windows
if "%SystemDrive%"=="" set SystemDrive=C:
if "%windir%"=="" set windir=C:\Windows
if "%NUMBER_OF_PROCESSORS%"=="" set NUMBER_OF_PROCESSORS=8
if "%PROCESSOR_ARCHITECTURE%"=="" set PROCESSOR_ARCHITECTURE=AMD64
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

"C:\Program Files\dotnet\dotnet.exe" %*
exit /b %ERRORLEVEL%
