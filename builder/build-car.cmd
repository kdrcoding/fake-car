@echo off
setlocal
rem Builds a Fake Car folder from your own E-Sys and PSdZData (no car, no window).
rem   build-car.cmd <car folder> [E-Sys folder] [PSdZData parent folder]
rem   build-car.cmd ..\cars\F30_340i_US C:\EC-Apps\E-Sys C:\Data
rem   build-car.cmd --types C:\EC-Apps\E-Sys C:\Data
rem The car folder needs car.txt and FA.xml; everything else is written by the builder.

if "%~1"=="" (
  echo usage: build-car.cmd ^<car folder^> [E-Sys folder] [PSdZData parent folder]
  exit /b 2
)
set "CAR=%~f1"
rem --types instead of a car folder lists every car type in the PSdZData (Fake Car's Add a car window).
if "%~1"=="--types" set "CAR=--types"
set "ESYS=%~2"
if "%ESYS%"=="" set "ESYS=C:\EC-Apps\E-Sys"
set "DATA=%~3"
if "%DATA%"=="" set "DATA=C:\Data"
if not exist "%ESYS%\lib\PsdZ\psdz-api.jar" (
  echo E-Sys not found in "%ESYS%". Give the E-Sys folder as the second argument.
  exit /b 2
)
if not exist "%DATA%\psdzdata\kiswb" (
  echo PSdZData not found in "%DATA%\psdzdata". Give the folder that holds psdzdata as the third argument.
  exit /b 2
)

set "HERE=%~dp0"
set "L=%ESYS%\lib"
set "CP=%HERE%CarBuilder.jar;%DATA%\psdzdata\extLibs\kis.jar;%L%\3rd-party\*;%L%\PsdZ\*;%L%\UUID\*;%L%\environment\*;%L%\estcm\*;%L%\*;%L%\fs\*;%L%\odxchecker\*;%L%\odxchecker\odx201\lib\*;%L%\xml\*"

rem CarBuilder is pure Java, compiled for Java 11, and wants a 2 GB heap, so it needs E-Sys's OWN Java to be
rem 64-bit AND version 11 or newer. Every supported E-Sys (22.06 and newer) ships exactly that. An older E-Sys
rem (its jre is Java 8) cannot be used: this builder will not load on Java 8, and E-Sys's own 2017-era libraries
rem will not run on a newer Java either (javax.xml.bind / sun.misc and more were removed after Java 8). So we use
rem E-Sys's own Java and, if it is too old, stop with a clear message instead of a cryptic Java error.
set "JAVA=%ESYS%\jre\bin\java.exe"
set "HEAP=-Xmx2g"
if not exist "%JAVA%" (
  echo Failed: no Java found in "%ESYS%\jre". Pick your E-Sys folder under Options, or use E-Sys 22.06 or newer.
  exit /b 3
)
rem Read the Java's bitness and version (Java 8 prints "1.8.0_x", so "1." = too old).
set "BITS=" & set "JV="
"%JAVA%" -XshowSettings:properties -version >"%TEMP%\fc-java.txt" 2>&1
for /f "tokens=2 delims==" %%B in ('findstr /i "sun.arch.data.model" "%TEMP%\fc-java.txt"') do set "BITS=%%B"
for /f "tokens=2 delims==" %%V in ('findstr /i "java.version" "%TEMP%\fc-java.txt"') do set "JV=%%V"
del "%TEMP%\fc-java.txt" >nul 2>&1
set "BITS=%BITS: =%"
set "JV=%JV: =%"
set "MAJ=0"
if not "%JV:~0,2%"=="1." for /f "tokens=1 delims=." %%M in ("%JV%") do set "MAJ=%%M"
if %MAJ% LSS 11 (
  echo Failed: your E-Sys is too old for the builder ^(its Java is %JV%^). Use E-Sys 22.06 or newer, or pick a newer E-Sys under Options.
  exit /b 3
)
rem A 32-bit Java cannot reserve 2 GB ("Could not reserve enough space for ... object heap"); keep it 32-bit-safe.
if not "%BITS%"=="64" set "HEAP=-Xmx1280m"
echo Builder Java: "%JAVA%" %HEAP%

rem PSdZ looks for its settings next to E-Sys's own start folder.
pushd "%L%\environment"
"%JAVA%" %HEAP% -cp "%CP%" CarBuilder "%CAR%" "%DATA%"
set RC=%ERRORLEVEL%
popd
exit /b %RC%
