@echo off
setlocal
rem Outcore Linux Bridge - instalator do kontenera (GameNative / Wine / Proton).
rem Uzycie:  instaluj.bat            (szuka gry na A:\ i w typowych miejscach)
rem          instaluj.bat "X:\sciezka\do\Outcore"

set "SRC=%~dp0payload"
set "GAME=%~1"
if not "%GAME%"=="" goto check
for %%P in ("A:\" "C:\Program Files (x86)\Steam\steamapps\common\Outcore\" "D:\Steam\steamapps\common\Outcore\" "E:\" "F:\" "G:\") do (
  if exist "%%~P\Outcore.exe" (
    set "GAME=%%~P"
    goto check
  )
)
echo Nie znalazlem Outcore.exe. Podaj folder gry:  instaluj.bat "X:\folder\gry"
goto end

:check
if not exist "%GAME%\Outcore.exe" (
  echo W "%GAME%" nie ma Outcore.exe
  goto end
)
echo Gra: %GAME%
xcopy "%SRC%\*" "%GAME%\" /E /I /Y /Q /H
if errorlevel 1 (
  echo BLAD: nie moge zapisac do folderu gry. Sprobuj innego dysku albo daj GameNative dostep do plikow.
  goto end
)
rem BepInEx laduje sie przez winhttp.dll - Wine musi wziac nasza wersje zamiast wbudowanej.
reg add "HKCU\Software\Wine\AppDefaults\Outcore.exe\DllOverrides" /v winhttp /t REG_SZ /d "native,builtin" /f >nul
if errorlevel 1 echo UWAGA: nie udalo sie ustawic DllOverrides. Dodaj w kontenerze zmienna WINEDLLOVERRIDES=winhttp=n,b
echo.
echo Gotowe. Uruchom gre normalnie. Log moda: %GAME%\BepInEx\LogOutput.log
:end
endlocal
pause
