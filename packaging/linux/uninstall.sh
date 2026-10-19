#!/usr/bin/env bash
set -euo pipefail
data_root="${XDG_DATA_HOME:-$HOME/.local/share}/fst"
config_root="${XDG_CONFIG_HOME:-$HOME/.config}"
if pgrep -u "$(id -u)" -x fst >/dev/null; then
  echo 'Exit FST from its tray menu before uninstalling.' >&2; exit 1
fi
python3 - "$data_root" "$config_root" "$HOME/.local/bin/fst" <<'PY'
import os, shutil, sys
data, config, launcher=sys.argv[1:]
# Exact application-owned locations only. Backups and Lua are preserved.
app=os.path.join(data, 'app')
if os.path.islink(app): raise SystemExit('Refusing to remove a linked app directory')
if os.path.isdir(app): shutil.rmtree(app)
for path in (os.path.join(config,'autostart','fst.desktop'),os.path.join(data,'..','applications','fst.desktop'),os.path.join(data,'..','icons','hicolor','scalable','apps','fst.svg'),os.path.join(config,'fst','settings.json')):
    if os.path.isfile(path) or os.path.islink(path): os.unlink(path)
if os.path.isfile(launcher) and not os.path.islink(launcher):
    with open(launcher, encoding='utf-8') as f: owned='# FastSteamTools launcher\n' in f.read()
    if owned: os.unlink(launcher)
PY
if command -v secret-tool >/dev/null; then secret-tool clear application fst service ryuu || true; fi
echo 'FST removed. Steam, Lua files and recovery backups preserved.'
