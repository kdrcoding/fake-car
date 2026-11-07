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

rem CarBuilder is pure Java, compiled for Java 11, and wants a 2 GB heap. A 32-bit Java cannot reserve
rem that heap ("Could not reserve enough space for 2097152KB object heap"); a Java 8 runtime cannot even
rem load it ("UnsupportedClassVersionError ... class file version 55.0"). So it needs a Java that is BOTH
rem 64-bit AND version 11 or newer. Every modern E-Sys ships exactly that, so prefer the E-Sys's own Java;
rem fall back to JAVA_HOME. Only if nothing qualifies do we run E-Sys's Java anyway, at a 32-bit-safe heap.
set "JAVA="
set "HEAP=-Xmx1280m"
for %%J in (
  "%ESYS%\jre\bin\java.exe"
  "%JAVA_HOME%\bin\java.exe"
) do if not defined JAVA if exist "%%~J" call :pick "%%~J"
if not defined JAVA set "JAVA=%ESYS%\jre\bin\java.exe"
echo Builder Java: "%JAVA%" %HEAP%

rem PSdZ looks for its settings next to E-Sys's own start folder.
pushd "%L%\environment"
"%JAVA%" %HEAP% -cp "%CP%" CarBuilder "%CAR%" "%DATA%"
set RC=%ERRORLEVEL%
popd
exit /b %RC%

:pick
rem %~1 is a java.exe. Take it only if it is 64-bit AND Java 11+, and then give it the full 2 GB heap.
rem -XshowSettings:properties prints "sun.arch.data.model = 64|32" and "java.version = 11.0.15" (Java 8
rem shows "1.8.0_x"); read both from a temp file.
set "BITS=" & set "JV="
"%~1" -XshowSettings:properties -version >"%TEMP%\fc-java.txt" 2>&1
for /f "tokens=2 delims==" %%B in ('findstr /i "sun.arch.data.model" "%TEMP%\fc-java.txt"') do set "BITS=%%B"
for /f "tokens=2 delims==" %%V in ('findstr /i "java.version" "%TEMP%\fc-java.txt"') do set "JV=%%V"
del "%TEMP%\fc-java.txt" >nul 2>&1
set "BITS=%BITS: =%"
set "JV=%JV: =%"
rem Major version: "1.x" means Java 8 or older (reject); otherwise the number before the first dot.
set "MAJ=0"
if not "%JV:~0,2%"=="1." for /f "tokens=1 delims=." %%M in ("%JV%") do set "MAJ=%%M"
if "%BITS%"=="64" if %MAJ% GEQ 11 (
  set "JAVA=%~1"
  set "HEAP=-Xmx2g"
)
goto :eof
