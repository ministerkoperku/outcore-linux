@echo off
rem Zbiera logi Outcore + moda do D:\outcore_logi
set "OUT=%~dp0..\outcore_logi"
set "GAME=%~1"
if "%GAME%"=="" set "GAME=A:\"
mkdir "%OUT%" 2>nul
copy /y "%GAME%\BepInEx\LogOutput.log" "%OUT%\" >nul 2>nul
copy /y "%GAME%\BepInEx\config\BepInEx.cfg" "%OUT%\" >nul 2>nul
copy /y "%USERPROFILE%\AppData\LocalLow\DoctorShinobi\Outcore\Player.log" "%OUT%\" >nul 2>nul
copy /y "%USERPROFILE%\AppData\LocalLow\DoctorShinobi\Outcore\Player-prev.log" "%OUT%\" >nul 2>nul
(
echo === pliki moda ===
dir /b "%GAME%\winhttp.dll" "%GAME%\doorstop_config.ini" 2>&1
dir /b /s "%GAME%\BepInEx\plugins" 2>&1
echo === override ===
reg query "HKCU\Software\Wine\AppDefaults\Outcore.exe\DllOverrides" 2>&1
echo === env ===
set WINE 2>&1
echo USERPROFILE=%USERPROFILE%
dir /b "%USERPROFILE%\AppData\LocalLow" 2>&1
) > "%OUT%\info.txt"
echo Zapisane w %OUT%
dir /b "%OUT%"
pause
