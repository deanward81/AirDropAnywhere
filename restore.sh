#!/bin/bash

# Setup some colors to use. These need to work in fairly limited shells, like the Ubuntu Docker container where there are only 8 colors.
# See if stdout is a terminal
if [ -t 1 ] && command -v tput > /dev/null; then
    # see if it supports colors
    ncolors=$(tput colors)
    if [ -n "$ncolors" ] && [ $ncolors -ge 8 ]; then
        bold="$(tput bold       || echo)"
        normal="$(tput sgr0     || echo)"
        black="$(tput setaf 0   || echo)"
        red="$(tput setaf 1     || echo)"
        green="$(tput setaf 2   || echo)"
        yellow="$(tput setaf 3  || echo)"
        blue="$(tput setaf 4    || echo)"
        magenta="$(tput setaf 5 || echo)"
        cyan="$(tput setaf 6    || echo)"
        white="$(tput setaf 7   || echo)"
    fi
fi

SCRIPT_PATH=$(cd "$(dirname "$0")"; pwd -P)
USER_HOME=$(eval echo ~${SUDO_USER})
sayError() {
    printf "%b\n" "${red:-}Error: $1${normal:-}"
}

sayWarning() {
    printf "%b\n" "${yellow:-}Warning: $1${normal:-}"
}

sayInfo() {
    printf "%b\n" "${green:-}$1${normal:-}"
}

updateProfile() {
    PROFILE=$HOME/.bashrc
    if [ -f "$PROFILE" ]; then
        if ! grep -q "^export $1=.*$" "$PROFILE"; then
            echo "export $1=$2" >> $PROFILE
        else
            sed -i '' 's;^export '"$1"'=.*$;export '"$1"'='"$2"';' "$PROFILE"
        fi
    fi

    PROFILE=$HOME/.zshrc
    if [ ! -f "$PROFILE" ]; then
        # create a .zshrc if one doesn't exist
        touch "$PROFILE"
        chown "$SUDO_USER:staff" "$PROFILE"
    fi
    if ! grep -q "^export $1=.*$" "$PROFILE"; then
        echo "export $1=$2" >> $PROFILE
    else
        sed -i '' 's;^export '"$1"'=.*$;export '"$1"'='"$2"';' "$PROFILE"
    fi
}

installDotNet() {
    sayInfo "Installing .NET SDK $DOTNET_VERSION..."
    # gotta download and install from scratch
    chmod 755 "$SCRIPT_PATH/build/dotnet-install.sh"
    "$SCRIPT_PATH/build/dotnet-install.sh" --version $DOTNET_VERSION --no-path --install-dir "$DOTNET_ROOT"
    if [ $? -ne 0 ]; then
        sayError "Error installing .NET SDK"
        exit 1
    fi

    sayInfo "Installed .NET SDK $DOTNET_VERSION!"
}

# make sure we're running elevated
if [ $EUID -ne 0 ]; then
    sayWarning "Script must be run in an elevated context. Running using sudo"
    sudo env HOME=$USER_HOME $0
    exit 0
fi

# check if JQ is installed
if [ ! -x "$(which jq)" ]; then
    sayError "jq is not installed. See https://stedolan.github.io/jq/download/"
    exit 1
fi

# check if dotnet is installed
DOTNET_EXE=$(which dotnet)
DOTNET_VERSION=$(cat global.json | jq --raw-output '.sdk.version')
export DOTNET_ROOT=/usr/local/share/dotnet
export PATH=$PATH:$DOTNET_ROOT
if [ ! -x "$DOTNET_EXE" ]; then
    installDotNet
else
    # make sure it's the latest build
    DOTNET_SDKS=$(dotnet --list-sdks | grep "$DOTNET_VERSION")
    if [ -z "$DOTNET_SDKS" ]; then
        sayWarning ".NET Core SDK $DOTNET_VERSION not installed"
        installDotNet
    fi
fi

# update the path
updateProfile "DOTNET_ROOT" "$DOTNET_ROOT"
updateProfile "PATH" "\$PATH:\$DOTNET_ROOT"