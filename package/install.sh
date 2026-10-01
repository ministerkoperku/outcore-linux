#!/usr/bin/env bash
# Outcore Linux Bridge - instalacja na Linuksie (Steam + Proton, np. Arch).
# Uzycie: ./install.sh [folder_gry]
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
appid=1275670

find_game() {
  local roots=("$HOME/.local/share/Steam" "$HOME/.steam/steam" "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam")
  for root in "${roots[@]}"; do
    local vdf="$root/steamapps/libraryfolders.vdf"
    local libs=("$root")
    if [[ -f "$vdf" ]]; then
      while IFS= read -r lib; do libs+=("$lib"); done < <(grep -oP '"path"\s+"\K[^"]+' "$vdf")
    fi
    for lib in "${libs[@]}"; do
      if [[ -f "$lib/steamapps/common/Outcore/Outcore.exe" ]]; then
        echo "$lib/steamapps/common/Outcore"
        return 0
      fi
    done
  done
  return 1
}

game="${1:-}"
if [[ -z "$game" ]]; then
  game="$(find_game)" || { echo "Nie znalazlem gry. Podaj folder: $0 /sciezka/do/Outcore"; exit 1; }
fi
[[ -f "$game/Outcore.exe" ]] || { echo "W '$game' nie ma Outcore.exe"; exit 1; }
echo "Gra: $game"
cp -r "$here/payload/." "$game/"

# Per-app override w prefiksie Protona (jesli juz istnieje - po pierwszym uruchomieniu gry).
steamapps="$(dirname "$(dirname "$game")")"
reg="$steamapps/compatdata/$appid/pfx/user.reg"
if [[ -f "$reg" ]]; then
  if ! grep -q 'AppDefaults\\\\Outcore.exe\\\\DllOverrides' "$reg"; then
    printf '\n[Software\\\\Wine\\\\AppDefaults\\\\Outcore.exe\\\\DllOverrides] 1700000000\n"winhttp"="native,builtin"\n' >> "$reg"
    echo "Dodano DllOverrides do prefiksu Protona."
  fi
fi

cat <<MSG

Gotowe. Na wszelki wypadek ustaw tez w Steam -> Outcore -> Wlasciwosci -> Opcje uruchamiania:

    WINEDLLOVERRIDES="winhttp=n,b" %command%

Log moda: $game/BepInEx/LogOutput.log
Konfiguracja (tapeta, ikony, pasek): $game/BepInEx/config/koper.outcore.linux.cfg
MSG
