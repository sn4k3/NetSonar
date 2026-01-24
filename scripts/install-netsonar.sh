#!/usr/bin/env bash
#
# Script to download and install/upgrade NetSonar in current location
# Can be run outside NetSonar and as standalone script
# Then run this script
# usage 1: ./install-netsonar.sh
# usage 2: ./install-netsonar.sh 0.1.0
#
set -euo pipefail

arch="$(uname -m)"           # x86_64, arm64, or aarch64
arch="${arch/aarch64/arm64}" # Normalize aarch64 to arm64
archCode="${arch/86_/}"      # x86_64 -> x64, arm64 -> arm64
osVariant=''                 # osx, linux, arch, rhel, suse
tag="${1:-latest}"
tag="${tag#v}"               # normalize
owner="sn4k3"
software="NetSonar"
api_url="https://api.github.com/repos/$owner/$software/releases/latest"
macOS_least_version='13.0'

# Helpers
version() { echo "$@" | awk -F. '{ printf("%d%03d%03d%03d\n", $1,$2,$3,$4); }'; }
testcmd() { command -v "$1" &> /dev/null; }
get_filesize() {
    (
      du --apparent-size --block-size=1 "$1" 2>/dev/null ||
      gdu --apparent-size --block-size=1 "$1" 2>/dev/null ||
      find "$1" -printf "%s" 2>/dev/null ||
      gfind "$1" -printf "%s" 2>/dev/null ||
      stat --printf="%s" "$1" 2>/dev/null ||
      stat -f%z "$1" 2>/dev/null ||
      wc -c <"$1" 2>/dev/null
    ) | awk '{print $1}'
}

download() {
    local download_url="$1"
    if [ -z "$download_url" ]; then
        echo 'Error: Download url was not specified!'
        exit 1
    fi

    local filename tmpfile download_size filesize
    filename="$(basename "$download_url")"
    tmpfile="$(mktemp "${TMPDIR:-/tmp}/${software}Update.XXXXXXXX")"
    # Use double quotes so $tmpfile is expanded now, not at exit when it's out of scope
    trap "rm -f '$tmpfile'" EXIT

    echo "- Downloading: $download_url"
    curl -fL --retry 4 --retry-all-errors "$download_url" -o "$tmpfile"

    download_size="$(curl -fsLI "$download_url" | awk 'tolower($0) ~ /^content-length:/ {gsub("\r","",$2); sz=$2} END{print sz}')"
    if [ -n "$download_size" ]; then
        echo '- Validating file'
        filesize="$(get_filesize "$tmpfile")"
        if [ "$download_size" -ne "$filesize" ]; then
            echo "Error: File verification failed, expecting $download_size bytes but downloaded $filesize bytes. Please re-run the script."
            exit 1
        fi
    fi

    echo '- Kill instances'
    pkill -TERM -f "${software}" 2>/dev/null || true
    sleep 1
    pkill -KILL -f "dotnet.*${software}\.dll" 2>/dev/null || true

    # return tmpfile path via global
    DOWNLOAD_TMPFILE="$tmpfile"
    DOWNLOAD_FILENAME="$filename"
}

cat << "EOF"
 _   _      _   ____
| \ | | ___| |_/ ___|  ___  _ __   __ _ _ __
|  \| |/ _ \ __\___ \ / _ \| '_ \ / _` | '__|
| |\  |  __/ |_ ___) | (_) | | | | (_| | |
|_| \_|\___|\__|____/ \___/|_| |_|\__,_|_|
    Auto download and installer script

- Detecting OS
EOF

# Arch validation
if [ "$arch" != "x86_64" ] && [ "$arch" != "arm64" ]; then
    echo "Error: Unsupported host arch $arch"
    exit 1
fi

# Tag validation
if [[ "$tag" =~ ^v?[0-9]+[.][0-9]+[.][0-9]+$ ]]; then
    tag="${tag#v}"
    api_url="https://api.github.com/repos/$owner/$software/releases/tags/v$tag"
elif [ "$tag" != "latest" ] && [ -n "$tag" ]; then
    echo "Error: Invalid '$tag' tag/version was provided."
    exit 1
else
    tag='latest'
fi

# OS detection
if [[ "${OSTYPE:-}" == darwin* ]]; then
    osVariant="osx"
elif testcmd apt-get; then
    osVariant="linux"
elif testcmd pacman; then
    osVariant="arch"
elif testcmd dnf; then
    osVariant="rhel"
elif testcmd zypper; then
    osVariant="suse"
fi

if [ -z "$osVariant" ]; then
    echo "Error: Unable to detect your Operating System."
    exit 1
fi

echo "- $osVariant $arch"

# Ensure curl
if ! testcmd curl; then
    echo '- Installing curl'
    case "$osVariant" in
        linux) sudo apt-get update && sudo apt-get install -y curl ;;
        arch)  sudo pacman -Sy --noconfirm curl ;;
        rhel)  sudo dnf install -y curl ;;
        suse)  sudo zypper install -y curl ;;
    esac
fi

if [ "$osVariant" == "osx" ]; then
    macOS_version="$(sw_vers -productVersion)"
    appPath="/Applications/${software}.app"

    if [ "$(version "$macOS_version")" -lt "$(version "$macOS_least_version")" ]; then
        echo "Error: Unable to install, $software requires at least macOS $macOS_least_version."
        exit 1
    fi

    if ! testcmd codesign; then
        echo '- Codesign required, installing, please accept the prompt...'
        xcode-select --install
        until xcode-select -p &>/dev/null; do sleep 2; done
    fi
    if ! testcmd unzip; then
        echo "Error: unzip is required. Install Command Line Tools or unzip package."
        exit 1
    fi

    echo '- Detecting download'
    # Capture response first
    response="$(curl -fs "$api_url")"

    # Robust parsing: look for browser_download_url with correct pattern, extract 4th quote-delimited field
    download_url="$(echo "$response" \
        | grep "browser_download_url.*_${osVariant}-${archCode}_.*[.]zip" \
        | head -1 \
        | cut -d '"' -f 4 || true)"

    if [ -z "$download_url" ]; then
        echo "Error: Unable to detect the download url. Version '$tag' may not exist."
        exit 1
    fi

    download "$download_url"

    echo '- Removing old versions'
    rm -rf "$appPath"

    echo "- Inflating $DOWNLOAD_FILENAME to $appPath"
    unzip -q -o "$DOWNLOAD_TMPFILE" -d "/Applications"
    rm -f "$DOWNLOAD_TMPFILE"

    if [ -d "$appPath" ]; then
        echo '- Removing com.apple.quarantine security flag (gatekeeper)'
        find "$appPath" -print0 | xargs -0 xattr -d com.apple.quarantine &> /dev/null || true

        echo '- Codesign app bundle'
        codesign --force --deep --sign - "$appPath"

        echo ''
        echo "Installation was successful. $software will now run."
        echo ''
        open -n "$appPath"
    else
        echo "Installation unsuccessful, unable to create '$appPath'."
        exit 1
    fi
else
    echo '- Detecting download'
    response="$(curl -fs "$api_url")"

    # Try specific distro variant first (e.g. arch-x64, arch-arm64)
    download_url="$(echo "$response" \
        | grep "browser_download_url.*_${osVariant}-${archCode}_.*[.]AppImage" \
        | head -1 \
        | cut -d '"' -f 4 || true)"

    # Fallback to generic linux (e.g. linux-x64, linux-arm64)
    if [ -z "$download_url" ]; then
        download_url="$(echo "$response" \
            | grep "browser_download_url.*_linux-${archCode}_.*[.]AppImage" \
            | head -1 \
            | cut -d '"' -f 4 || true)"
    fi

    if [ -z "$download_url" ]; then
        echo "Error: Unable to detect the download url. Version '$tag' may not exist."
        exit 1
    fi

    download "$download_url"

    targetDir="$PWD"
    [ -d "$HOME/Applications" ] && targetDir="$HOME/Applications"
    targetFilePath="$targetDir/${software}.AppImage"

    echo '- Removing old versions'
    rm -f "$targetDir/${software}_"*".AppImage"

    echo "- Moving $DOWNLOAD_FILENAME to $targetDir"
    mv -f "$DOWNLOAD_TMPFILE" "$targetFilePath"
    rm -f "$DOWNLOAD_TMPFILE"

    echo '- Setting permissions'
    chmod -fv 775 "$targetFilePath"

    "$targetFilePath" &

    echo ''
    echo "Installation was successful. $software will now run."
    echo 'If prompted for "Desktop integration", click "Integrate and run".'
    echo ''
fi
