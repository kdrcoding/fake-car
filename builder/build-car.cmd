@echo off
setlocal
rem Builds a Fake Car folder from your own E-Sys and PSdZData (no car, no window).
rem   build-car.cmd <car folder> [E-Sys folder] [PSdZData parent folder]
rem   build-car.cmd ..\cars\F30_340i_US C:\EC-Apps\E-Sys C:\Data
rem The car folder needs car.txt and FA.xml; everything else is written by the builder.

if "%~1"=="" (
  echo usage: build-car.cmd ^<car folder^> [E-Sys folder] [PSdZData parent folder]
  exit /b 2
)
set "CAR=%~f1"
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
rem PSdZ looks for its settings next to E-Sys's own start folder.
pushd "%L%\environment"
"%ESYS%\jre\bin\java" -Xmx2g -cp "%CP%" CarBuilder "%CAR%" "%DATA%"
set RC=%ERRORLEVEL%
popd
exit /b %RC%
