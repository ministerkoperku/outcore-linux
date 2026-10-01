@echo off
setlocal
set "GAME=%~1"
if "%GAME%"=="" set "GAME=A:\"
if not exist "%GAME%\Outcore.exe" (
  echo Podaj folder gry:  odinstaluj.bat "X:\folder\gry"
  goto end
)
del /q "%GAME%\winhttp.dll" "%GAME%\doorstop_config.ini" "%GAME%\.doorstop_version" 2>nul
rmdir /s /q "%GAME%\BepInEx" 2>nul
reg delete "HKCU\Software\Wine\AppDefaults\Outcore.exe\DllOverrides" /v winhttp /f >nul 2>nul
echo Usuniete.
:end
endlocal
pause
