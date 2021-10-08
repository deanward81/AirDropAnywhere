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

cat << EOM
--------------------------------------------------------------
|                                                            |
|               MacOS VS Code Raspberry Pi                   |
|             Remote Deployment and Debugging                |
|                      Setup Script                          |
|                                                            |
--------------------------------------------------------------
|                                                            |
| This script was derived from the Windows version created   |
| by @petecodes at https://git.io/JO79c.                     |
|                                                            |
| It will automatically create SSH keys and copy the keys to |
| the specified Raspberry Pi, and then configure the VS      |
| remote debugger so that VS Code can remotely debug .NET    |
| projects deployed to the Pi.                               |
|                                                            |
| Created By: Dean Ward                                      |
| Date:       2021-04-25                                     |
|                                                            |
--------------------------------------------------------------
EOM

cat << EOM
--------------------------------------------------------------
|                                                            |
|                   Setting Pi Host Name                     |
|                                                            |
--------------------------------------------------------------
EOM

read -p "Enter Raspberry Pi Hostname e.g. raspberrypi: " PI_HOSTNAME
if [ -n "$PI_HOSTNAME" ]; then
    echo "${green:-}Raspberry Pi hostname set to '$PI_HOSTNAME'${normal:-}"
else
    echo "${yellow:-}No Raspberry Pi hostname specified, exiting...${normal:-}"
    exit
fi

if [ ! -f ~/.ssh/id_rsa_yubikey.pub -a ! -f ~/.ssh/id_rsa ]; then
    cat << EOM
--------------------------------------------------------------
|                                                            |
|                   Generating SSH keys                      |
|                                                            |
--------------------------------------------------------------
EOM
ssh-keygen -N:""
echo "${green:-}Generated SSH keys!${normal:-}"
fi

cat << EOM
--------------------------------------------------------------
|                                                            |
|           Adding Raspberry Pi to SSH known_hosts           |
|                                                            |
--------------------------------------------------------------
EOM
ssh-keyscan -H $PI_HOSTNAME >> ~/.ssh/known_hosts
echo "${green:-}Added Raspberry Pi to SSH known_hosts!${normal:-}"

cat << EOM
--------------------------------------------------------------
|                                                            |
|                   Creating .ssh directory                  |
|                                                            |
|                 (Enter your login password)                |
|                                                            |
--------------------------------------------------------------
EOM
ssh pi@$PI_HOSTNAME -oStrictHostKeyChecking=no "mkdir -p ~/.ssh"
echo "${green:-}Created .ssh directory on '$PI_HOSTNAME'!${normal:-}"

cat << EOM
--------------------------------------------------------------
|                                                            |
|               Copying SSH Keys to Raspberry Pi             |
|                                                            |
|                 (Enter your login password)                |
|                                                            |
--------------------------------------------------------------
EOM
if [ -f ~/.ssh/id_rsa_yubikey.pub ]; then
    cat ~/.ssh/id_rsa_yubikey.pub | ssh -oStrictHostKeyChecking=no pi@$PI_HOSTNAME "cat >> ~/.ssh/authorized_keys"
else
    cat ~/.ssh/id_rsa.pub | ssh -oStrictHostKeyChecking=no pi@$PI_HOSTNAME "cat >> ~/.ssh/authorized_keys"
fi
echo "${green:-}Added public key to authorized_keys on '$PI_HOSTNAME'!${normal:-}"

cat << EOM
--------------------------------------------------------------
|                                                            |
|    Downloading Visual Studio Debugger on Raspberry Pi      |
|                                                            |
--------------------------------------------------------------
EOM
ssh pi@$PI_HOSTNAME "cd && curl -sSL https://aka.ms/getvsdbgsh | /bin/sh /dev/stdin -v latest -l ~/vsdbg"
echo "${green:-}Downloaded Visual Studio Debugger to '$PI_HOSTNAME'!${normal:-}"

echo "${green:-}All done!${normal:-}"
