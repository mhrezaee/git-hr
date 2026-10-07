#!/usr/bin/env bash
# Builds the downloadable packages of GitHr for one platform.
#
#   build/package.sh <runtime id> <version> [output folder]
#   build/package.sh win-x64 1.2.0            -> dist/GitHr-1.2.0-win-x64.zip, dist/GitHr-1.2.0-win-x64-setup.exe
#   build/package.sh osx-arm64 1.2.0          -> dist/GitHr-1.2.0-osx-arm64.dmg
#   build/package.sh linux-x64 1.2.0          -> dist/GitHr-1.2.0-linux-x64.tar.gz, dist/GitHr-1.2.0-linux-x64.AppImage
#
# Every package contains one self-contained executable: no .NET installation needed, only git.
# Windows needs Inno Setup (ISCC) for the installer, macOS needs hdiutil/codesign, Linux downloads appimagetool.
# Runs on bash 3.2 (macOS) and Git Bash (Windows).
set -euo pipefail

if [ $# -lt 2 ]; then
  echo "usage: $0 <runtime id> <version> [output folder]" >&2
  exit 2
fi
rid=$1
version=$2
root=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "${3:-$root/dist}"
dist=$(cd "${3:-$root/dist}" && pwd)
publish="$root/artifacts/publish/$rid"
name="GitHr-$version-$rid"

echo "==> Publishing GitHr $version for $rid"
rm -rf "$publish"
dotnet publish "$root/src/GitHr.App/GitHr.App.csproj" -c Release -r "$rid" --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:Version="$version" -p:DebugType=none -p:DebugSymbols=false \
  -o "$publish"
# Native debug symbols that come with the Skia/HarfBuzz packages: not needed to run, and ~100 MB.
rm -f "$publish"/*.pdb "$publish"/*.dbg
cp "$root/LICENSE" "$publish/LICENSE.txt"

package_windows() {
  local arch=${rid#win-}
  echo "==> $name.zip"
  rm -f "$dist/$name.zip"
  powershell.exe -NoProfile -Command \
    "Compress-Archive -Path '$(cygpath -w "$publish")\\*' -DestinationPath '$(cygpath -w "$dist/$name.zip")'"

  local iscc
  iscc=$(command -v iscc || command -v ISCC || true)
  for candidate in "/c/Program Files/Inno Setup 7" "/c/Program Files (x86)/Inno Setup 7" "/c/Program Files (x86)/Inno Setup 6"; do
    if [ -z "$iscc" ] && [ -x "$candidate/ISCC.exe" ]; then
      iscc="$candidate/ISCC.exe"
    fi
  done
  if [ -z "$iscc" ]; then
    if [ "${CI:-}" = "true" ]; then
      echo "Inno Setup (ISCC.exe) not found" >&2
      exit 1
    fi
    echo "warning: Inno Setup (ISCC.exe) not found; skipping the installer" >&2
    return
  fi
  echo "==> $name-setup.exe"
  "$iscc" -Q "-DVersion=$version" "-DFileVersion=${version%%-*}" "-DArch=$arch" "-DPublishDir=$(cygpath -w "$publish")" \
    "-O$(cygpath -w "$dist")" "-F$name-setup" "$(cygpath -w "$root/build/windows/GitHr.iss")"
}

package_macos() {
  local app="$root/artifacts/macos/$rid/GitHr.app"
  echo "==> GitHr.app"
  rm -rf "$(dirname "$app")"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp "$publish/GitHr" "$app/Contents/MacOS/GitHr"
  cp "$root/build/macos/githr.icns" "$app/Contents/Resources/githr.icns"
  cp "$publish/LICENSE.txt" "$app/Contents/Resources/LICENSE.txt"
  sed "s/{{VERSION}}/$version/g" "$root/build/macos/Info.plist" > "$app/Contents/Info.plist"
  # Apple Silicon only runs signed code; an ad-hoc signature is enough for a downloaded app the user allows once.
  codesign --force --deep --sign - "$app"

  echo "==> $name.dmg"
  local stage="$root/artifacts/macos/$rid/dmg"
  mkdir -p "$stage"
  mv "$app" "$stage/"
  ln -s /Applications "$stage/Applications"
  rm -f "$dist/$name.dmg"
  hdiutil create -volname "GitHr $version" -srcfolder "$stage" -ov -format UDZO "$dist/$name.dmg"
}

package_linux() {
  echo "==> $name.tar.gz"
  local stage="$root/artifacts/linux/$rid/$name"
  rm -rf "$(dirname "$stage")"
  mkdir -p "$stage"
  cp "$publish/GitHr" "$publish/LICENSE.txt" "$stage/"
  cp "$root/build/linux/githr.desktop" "$root/src/GitHr.App/Assets/githr.png" "$stage/"
  tar -C "$(dirname "$stage")" -czf "$dist/$name.tar.gz" "$name"

  # AppImage: one file that runs on most distributions (x64 only; appimagetool runs on the build machine).
  if [ "$rid" != "linux-x64" ]; then
    return
  fi
  echo "==> $name.AppImage"
  local appdir="$root/artifacts/linux/$rid/GitHr.AppDir"
  mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/512x512/apps"
  cp "$publish/GitHr" "$appdir/usr/bin/GitHr"
  cp "$root/build/linux/githr.desktop" "$appdir/githr.desktop"
  cp "$root/build/linux/githr.desktop" "$appdir/usr/share/applications/githr.desktop"
  cp "$root/src/GitHr.App/Assets/githr.png" "$appdir/githr.png"
  cp "$root/src/GitHr.App/Assets/githr.png" "$appdir/usr/share/icons/hicolor/512x512/apps/githr.png"
  cat > "$appdir/AppRun" <<'APPRUN'
#!/bin/sh
here=$(dirname "$(readlink -f "$0")")
exec "$here/usr/bin/GitHr" "$@"
APPRUN
  chmod +x "$appdir/AppRun" "$appdir/usr/bin/GitHr"

  local tool="$root/artifacts/appimagetool-x86_64.AppImage"
  if [ ! -x "$tool" ]; then
    curl -fsSL -o "$tool" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
    chmod +x "$tool"
  fi
  # Extract-and-run: CI machines usually have no FUSE.
  rm -f "$dist/$name.AppImage"
  ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream "$appdir" "$dist/$name.AppImage"
}

case "$rid" in
  win-*) package_windows ;;
  osx-*) package_macos ;;
  linux-*) package_linux ;;
  *) echo "unknown runtime id: $rid" >&2; exit 2 ;;
esac

echo "==> Done:"
ls -l "$dist"/"$name"*
