#!/usr/bin/env sh
# Installs b-speak on macOS or Linux from the latest GitHub release, or updates an existing install.
#   curl -fsSL https://raw.githubusercontent.com/BlakeHastings/b-speak/main/install.sh | sh
# Installs to ~/.bspeak/bin (or $BSPEAK_HOME/bin) and links ~/.local/bin/bspeak.
set -eu

repo=BlakeHastings/b-speak
home="${BSPEAK_HOME:-$HOME/.bspeak}"
bin="$home/bin"

# Already installed from a release: let it update itself. If that fails (for example an older
# build with a broken updater), fall through to a fresh install.
if [ -x "$bin/bspeak" ] && [ -f "$bin/bspeak.release" ]; then
  if "$bin/bspeak" update; then exit 0; fi
  echo "bspeak update did not succeed; reinstalling from the latest release instead."
fi

case "$(uname -s)" in
  Linux) os=linux ;;
  Darwin) os=osx ;;
  MINGW* | MSYS* | CYGWIN*) echo "On Windows, use install.ps1:  irm https://raw.githubusercontent.com/$repo/main/install.ps1 | iex" >&2; exit 1 ;;
  *) echo "Unsupported system: $(uname -s)" >&2; exit 1 ;;
esac
case "$(uname -m)" in
  x86_64 | amd64) arch=x64 ;;
  arm64 | aarch64) arch=arm64 ;;
  *) echo "Unsupported CPU: $(uname -m)" >&2; exit 1 ;;
esac
platform="$os-$arch"
command -v curl > /dev/null 2>&1 || { echo "b-speak's installer needs curl." >&2; exit 1; }

# Find the latest version from GitHub's releases/latest redirect (no API call, no JSON, no rate limit).
latest=$(curl -fsSLI -o /dev/null -w '%{url_effective}' "https://github.com/$repo/releases/latest")
tag="${latest##*/}"
case "$tag" in v*) ;; *) echo "Could not find the latest b-speak release." >&2; exit 1 ;; esac
version="${tag#v}"
name="bspeak-$version-$platform.tar.gz"
base="https://github.com/$repo/releases/download/$tag"

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
echo "Downloading b-speak $version for $platform ..."
curl -fsSL -o "$tmp/$name" "$base/$name" || { echo "b-speak $version has no build for $platform." >&2; exit 1; }
curl -fsSL -o "$tmp/SHA256SUMS" "$base/SHA256SUMS"
expected=$(grep " \**$name\$" "$tmp/SHA256SUMS" | cut -d ' ' -f 1)
if command -v sha256sum > /dev/null 2>&1; then actual=$(sha256sum "$tmp/$name" | cut -d ' ' -f 1)
else actual=$(shasum -a 256 "$tmp/$name" | cut -d ' ' -f 1); fi
[ -n "$expected" ] && [ "$actual" = "$expected" ] || { echo "The download failed its checksum." >&2; exit 1; }

[ -x "$bin/bspeak" ] && "$bin/bspeak" host delete > /dev/null 2>&1 || true
mkdir -p "$bin"
tar -xzf "$tmp/$name" -C "$bin"
chmod +x "$bin/bspeak"
if [ "$os" = osx ]; then xattr -dr com.apple.quarantine "$bin" 2> /dev/null || true; fi

mkdir -p "$HOME/.local/bin"
ln -sf "$bin/bspeak" "$HOME/.local/bin/bspeak"
echo "b-speak $version installed to $bin (linked from ~/.local/bin/bspeak)."

# Make sure ~/.local/bin is on PATH in future shells.
case ":$PATH:" in
  *":$HOME/.local/bin:"*) ;;
  *)
    line='export PATH="$HOME/.local/bin:$PATH"'
    for rc in "$HOME/.profile" "$HOME/.bashrc" "$HOME/.zshrc"; do
      if [ -f "$rc" ] || [ "$rc" = "$HOME/.profile" ]; then
        grep -qs '.local/bin' "$rc" || printf '\n# Added by the b-speak installer\n%s\n' "$line" >> "$rc"
      fi
    done
    echo "Added ~/.local/bin to your PATH in your shell profile; new shells pick it up. For this shell: $line"
    ;;
esac

# Native libraries speech to text and audio need, which minimal systems may lack.
if [ "$os" = linux ]; then
  ldcache=$( (ldconfig -p || /sbin/ldconfig -p) 2> /dev/null || true)
  missing=""
  printf '%s' "$ldcache" | grep -q 'libgomp.so.1' || missing="$missing libgomp (speech to text)"
  printf '%s' "$ldcache" | grep -q 'libasound.so.2' || missing="$missing, ALSA libasound (audio)"
  if [ -n "$missing" ]; then
    echo "Missing system libraries:$missing."
    echo "  Debian/Ubuntu: sudo apt-get install -y libgomp1 libasound2   (Ubuntu 24.04: libasound2t64)"
    echo "  Fedora:        sudo dnf install -y libgomp alsa-lib"
    echo "  Arch:          sudo pacman -S gcc-libs alsa-lib"
  fi
fi

echo "Next: bspeak setup   (downloads the voice models once and checks they work)"
