# Admin scripts from https://www.autoitscript.com/forum/topic/174609-powershell-script-to-self-elevate/
function Test-IsAdmin()
{
    # Get the current ID and its security principal
    $windowsID = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $windowsPrincipal = new-object System.Security.Principal.WindowsPrincipal($windowsID)

    # Get the Admin role security principal
    $adminRole=[System.Security.Principal.WindowsBuiltInRole]::Administrator

    # Are we an admin role?
    if ($windowsPrincipal.IsInRole($adminRole))
    {
        $true
    }
    else
    {
        $false
    }
}

if (!$IsWindows) {
    Write-Host -ForegroundColor Yellow "This script should only be run on Windows"
    return
}

if (!(Test-IsAdmin)) {
    Write-Host -ForegroundColor Yellow "This script should only be run as an admin"
    return
}

# work out which version of .NET we should be running (based upon global.json)
Set-ExecutionPolicy -ExecutionPolicy Unrestricted -Scope CurrentUser
$globalJson = Get-Content -Raw -Path 'global.json' | ConvertFrom-Json
$dotnetVersion = $globalJson.sdk.version

Write-Host -ForegroundColor Green "Found .NET SDK version '$dotnetVersion'"

$dotnetPath = Join-Path $env:LOCALAPPDATA "Microsoft" "dotnet"
if (!(Test-Path -Path $dotnetPath -PathType Container)) {
    Write-Host -ForegroundColor Green "Creating '$dotnetPath'"
    New-Item -ItemType Directory -Path $dotnetPath
}

# check to see if the installation root is in path _above_ the system-wide installation path
$machinePath = [Environment]::GetEnvironmentVariable("Path", [System.EnvironmentVariableTarget]::Machine)
$paths = $machinePath.Split(';')
$systemIndex = $paths.IndexOf((Join-Path $env:ProgramFiles "dotnet" "\"))
$userIndex = $paths.IndexOf($dotnetPath)
if ($userIndex -eq -1 -or $systemIndex -lt $userIndex) {
    Write-Host -ForegroundColor Green "Updating PATH to include '$dotnetPath'"
    
    [Environment]::SetEnvironmentVariable("Path", $dotnetPath + ";" + $machinePath, [System.EnvironmentVariableTarget]::Machine)
}

& ./build/dotnet-install.ps1 -Version $dotnetVersion