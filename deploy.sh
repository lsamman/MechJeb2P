#!/usr/bin/env bash
# MechJeb2P: build MechJeb2 and MechJeb2.UI in Release and copy both into a KSP install.
#
#   ./deploy.sh                      # uses KspDir from Directory.Build.props.user, or the Steam default
#   ./deploy.sh "/path/to/KSP"       # a given install
#
# MechJeb2's own copy step (copy_build.sh) copies MechJeb2.dll, MechJebLib*.dll, alglib.dll and the data folders;
# MechJeb2.UI.dll, which draws every window, is copied here as well (its project only copies when KSPDIR is set).
set -euo pipefail

cd "$(dirname "$0")"

KSPDIR="${1:-}"
if [ -z "$KSPDIR" ] && [ -f Directory.Build.props.user ]; then
  KSPDIR=$(sed -n 's:.*<KspDir>\(.*\)</KspDir>.*:\1:p' Directory.Build.props.user | head -1)
fi
if [ -z "$KSPDIR" ]; then
  KSPDIR="$HOME/.local/share/Steam/steamapps/common/Kerbal Space Program"
fi
if [ ! -d "$KSPDIR/GameData" ]; then
  echo "No GameData under '$KSPDIR'" >&2
  exit 1
fi

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export KSPDIR

dotnet build MechJeb2/MechJeb2.csproj -c Release -p:KspDir="$KSPDIR"
dotnet build MechJeb2.UI/MechJeb2.UI.csproj -c Release -p:KspDir="$KSPDIR"

PLUGINS="$KSPDIR/GameData/MechJeb2/Plugins"
mkdir -p "$PLUGINS"
cp MechJeb2.UI/bin/Release/MechJeb2.UI.dll "$PLUGINS/"
cp -r Localization "$KSPDIR/GameData/MechJeb2/"

echo "MechJeb2P deployed to $PLUGINS"
ls -la "$PLUGINS"/*.dll
