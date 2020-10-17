#!/bin/pwsh

param([switch]$norestart)

# use manual checks so you can get in a good state instead of simply failing when run through Explorer context menu
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator"))  
{  
    Write-Host "Restarting with elevated permissions..."
    #    Start-Process pwsh -Verb runAs -ArgumentList "$PSCommandPath -norestart"
    #    break
}

if ($PSVersionTable.PSVersion.Major -lt 6)
{
    Write-Host "This script requires newer PowerShell version"
    Write-Host "You can get the latest version at https://github.com/PowerShell/PowerShell#get-powershell"
    if (-not $norestart)
    {
        Write-Host "Trying to restart..."
        & pwsh $PSCommandPath -norestart
        break
    }
}

$is64bit = [Environment]::Is64BitOperatingSystem
$isWin10 = [Environment]::OSVersion.Version.Major
if (-not $is64bit)
{
    Write-Error "This script is only intended for use with 64-bit OS"
    break
}

# Computer\HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Video\{....}\Video
# DeviceDesc = @oem33.inf,%nvidia_dev.1b06%;NVIDIA GeForce GTX 1080 Ti
#              @oem60.inf,%nvidia_dev.2206%;NVIDIA GeForce RTX 3080
#              @oem26.inf,%amd67df.51%;Radeon (TM) RX 480 Graphics
# Service = nvlddmkm
#           amdkmdag

# It seems like active GPUs should have both Video AND 0000 subkeys
$registeredGpus = @(Get-ChildItem Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Video)
$gpus = @()
foreach ($entry in $registeredGpus)
{
    $gpuGuid = Split-Path $entry.Name -Leaf
    $gpuEntryPath = "Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Video\$gpuGuid"
    if (Test-Path "$gpuEntryPath\Video")
    {
        $item = Get-ItemProperty -LiteralPath "$gpuEntryPath\Video"
        if (Test-Path "$gpuEntryPath\0000")
        {
            $gpus += $item
        }
        else
        {
            if ($item.Service -ine 'BasicDisplay')
            {
                $name = @($item.DeviceDesc -split ';')[-1]
                Write-Warning "Found inactive GPU entry: $name"
            }
        }
    }
}
if ($gpus.Length -eq 0)
{
    Write-Error "Failed to enumerate any GPUs"
    break
}

$suffix = ''
if ($gpus.Length -gt 1)
{
    $suffix = 's'
}
Write-Host "Found $($gpus.Length) active GPU$($suffix):"
foreach ($gpu in $gpus)
{
    $name = @($gpu.DeviceDesc -split ';')[-1]
    Write-Host "`t$name"
}
# Computer\HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvlddmkm
#                                                               amdkmdag
# ImagePath = \SystemRoot\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_feed726c6560f7a7\nvlddmkm.sys
#             \SystemRoot\System32\DriverStore\FileRepository\u0355166.inf_amd64_b850e0f0c3bce936\B355483\amdkmdag.sys

