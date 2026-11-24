#!/usr/bin/env bash
# Builds bspeak, installs it to ~/.bspeak/bin, puts a `bspeak` command on your PATH (~/bin),
# and installs the b-speak skill for Claude Code (~/.claude/skills/b-speak).
# Usage: scripts/install.sh
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
home="${BSPEAK_HOME:-$HOME/.bspeak}"
bin="$home/bin"

# A running host locks its files; stop it first.
stop_host() {
  for existing in "$bin/bspeak.exe" "$bin/bspeak"; do
    if [ -f "$existing" ]; then "$existing" host delete > /dev/null 2>&1 || true; break; fi
  done
  # The host takes a moment to exit and release its files.
  for _ in $(seq 1 50); do
    if command -v powershell > /dev/null 2>&1; then
      powershell -NoProfile -Command "if (Get-Process bspeak -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '*\\.bspeak\\bin\\*' }) { exit 1 }" && break
    else
      pgrep -f "$bin/bspeak" > /dev/null || break
    fi
    sleep 0.2
  done
}

echo "Publishing to $bin ..."
# Retry: an agent's wait loop can restart the host between stopping it and copying files.
for attempt in 1 2 3; do
  stop_host
  if dotnet publish "$repo/src/BSpeak.Cli" -c Release -o "$bin" --nologo -v quiet; then break; fi
  [ "$attempt" = 3 ] && { echo "Could not replace files in $bin; something keeps bspeak running." >&2; exit 1; }
  echo "Files were in use; retrying ..."
done

mkdir -p "$HOME/bin"
exe="$bin/bspeak"; [ -f "$bin/bspeak.exe" ] && exe="$bin/bspeak.exe"
cat > "$HOME/bin/bspeak" <<EOF
#!/usr/bin/env bash
exec "$exe" "\$@"
EOF
chmod +x "$HOME/bin/bspeak"
if [ -f "$bin/bspeak.exe" ]; then
  # For cmd and PowerShell.
  winexe="$(cygpath -w "$bin/bspeak.exe" 2>/dev/null || echo "$bin/bspeak.exe")"
  printf '@echo off\r\n"%s" %%*\r\n' "$winexe" > "$HOME/bin/bspeak.cmd"
fi

mkdir -p "$HOME/.claude/skills/b-speak"
cp "$repo/skills/b-speak/"*.md "$HOME/.claude/skills/b-speak/"

for asset in voices espeak runtimes; do
  [ -d "$bin/$asset" ] || { echo "Publish is missing $asset/; text to speech will not work." >&2; exit 1; }
done

echo "Installed. 'bspeak help' lists the commands; the Claude Code skill is b-speak."
case ":$PATH:" in *":$HOME/bin:"*) ;; *) echo "Add $HOME/bin to your PATH to use 'bspeak' directly." ;; esac
