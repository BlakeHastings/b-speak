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

tag=$(curl -fsSL -H 'User-Agent: b-speak-installer' "https://api.github.com/repos/$repo/releases/latest" \
  | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -n 1)
[ -n "$tag" ] || { echo "Could not find the latest b-speak release." >&2; exit 1; }
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
case ":$PATH:" in *":$HOME/.local/bin:"*) ;; *) echo "Add ~/.local/bin to your PATH, or run $bin/bspeak directly." ;; esac
echo "Next: bspeak setup   (downloads the voice models once and checks they work)"
