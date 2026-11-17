@echo off
rem Builds PalmGuard and tools with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No SDK, NuGet or Visual Studio required.
setlocal
set CSC=
for /d %%d in ("%WINDIR%\Microsoft.NET\Framework64\v4*") do if exist "%%d\csc.exe" set "CSC=%%d\csc.exe"
if "%CSC%"=="" (
  echo csc.exe not found under %WINDIR%\Microsoft.NET\Framework64\v4*
  exit /b 1
)
cd /d "%~dp0"
if not exist bin mkdir bin
set REFS=-r:System.Windows.Forms.dll -r:System.Drawing.dll
set OPTS=-nologo -optimize+ -platform:x64

"%CSC%" %OPTS% -target:winexe %REFS% -win32icon:assets\palmguard.ico -resource:assets\palmguard.ico,PalmGuard.ico -out:bin\PalmGuard.exe src\PalmGuard.cs src\Engine.cs || exit /b 1
"%CSC%" %OPTS% -target:exe           -out:bin\Replay.exe tools\Replay.cs src\Engine.cs || exit /b 1
"%CSC%" %OPTS% -target:exe    %REFS% -out:bin\TouchLogger.exe tools\TouchLogger.cs || exit /b 1
"%CSC%" %OPTS% -target:exe    %REFS% -out:bin\ForceProbe.exe tools\ForceProbe.cs || exit /b 1
"%CSC%" %OPTS% -target:exe           -out:bin\TouchpadParams.exe tools\TouchpadParams.cs || exit /b 1
"%CSC%" %OPTS% -target:exe           -out:bin\HidCaps.exe tools\HidCaps.cs || exit /b 1
echo.
echo Built into %~dp0bin
