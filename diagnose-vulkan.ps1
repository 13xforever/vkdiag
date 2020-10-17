#!/bin/pwsh

param([switch]$norestart)

# use manual checks so you can get in a good state instead of simply failing when run through Explorer context menu
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]"Administrator")

function Elevate-Context
{
    Write-Host "Restarting with elevated permissions..."
    Start-Process pwsh -Verb runAs -ArgumentList "$PSCommandPath -norestart"
    break
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
if (-not $is64bit)
{
    Write-Error "This script is only intended for use with 64-bit OS"
    break
}

Clear-Host
$osInfo = Get-CimInstance -Class CIM_OperatingSystem | Select-Object Caption, Version
Write-Host "OS: $($osInfo.Caption)"
Write-Host "Version: $($osInfo.Version)"

Write-Host
Write-Host "Enumerating GPUs..."
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
        if (Test-Path "$gpuEntryPath\0000")
        {
            $gpus += $gpuGuid
        }
        else
        {
            $item = Get-ItemProperty -LiteralPath "$gpuEntryPath\Video"
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
# Computer\HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Video\{47F997B3-03D7-11EB-A625-5CF370691F26}\0000
# VulkanDriverName = C:\WINDOWS\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_feed726c6560f7a7\nv-vk64.json
# VulkanDriverNameWoW = C:\WINDOWS\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_feed726c6560f7a7\nv-vk32.json
# VulkanImplicitLayers = C:\WINDOWS\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_feed726c6560f7a7\nv-vk64.json
# VulkanImplicitLayersWow = C:\WINDOWS\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_feed726c6560f7a7\nv-vk32.json
foreach ($gpuGuid in $gpus)
{
    $gpuEntryPath = "Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Video\$gpuGuid"
    $gpu = Get-ItemProperty -LiteralPath "$gpuEntryPath\Video"
    $output = Get-ItemProperty -LiteralPath "$gpuEntryPath\0000"
    $name = @($gpu.DeviceDesc -split ';')[-1]
    $driverVersion = $output.DriverVersion
    Write-Host "`t$name ($driverVersion)"
    $key = Get-Item $gpuEntryPath
    foreach ($output in $key.Property)
    {
        if ($output -notmatch '\d{4}')
        {
            continue
        }

        foreach ($prop in @('VulkanDriverName', 'VulkanDriverNameWoW', 'VulkanImplicitLayers', 'VulkanImplicitLayersWow'))
        {
            $propValue = Get-ItemPropertyValue -LiteralPath "$gpuEntryPath\$output" -Name $prop
            if (-not (Test-Path -LiteralPath $propValue))
            {
                Write-Warning "`t`tInvalid value for output $output, property $($prop): $propValue"
            }
        }
    }
}

# Computer\HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvlddmkm
#                                                               amdkmdag
# ImagePath = \SystemRoot\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_feed726c6560f7a7\nvlddmkm.sys
#             \SystemRoot\System32\DriverStore\FileRepository\u0355166.inf_amd64_b850e0f0c3bce936\B355483\amdkmdag.sys

Write-Host
Write-Host "Checking Vulkan entries..."
# Computer\HKEY_LOCAL_MACHINE\SOFTWARE\Khronos\Vulkan\Drivers
# Computer\HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Khronos\Vulkan\Drivers
# should be empty
foreach ($node in @('', '\WOW6432Node'))
{
    if ($node -eq '')
    {
        Write-Host "64-bit entries..."
    }
    else
    {
        Write-Host "`32-bit entries..."
    }
    $keyPath = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE$node\Khronos\Vulkan\Drivers"
    $key = Get-Item -LiteralPath $keyPath
    if ($key.ValueCount -gt 0)
    {
        if (-not $isAdmin)
        {
            Elevate-Context
            break
        }
        Write-Host "`tCleaning explicit Vulkan driver entries..."
        foreach ($prop in $key.Property)
        {
            Write-Host "`t`tRemoving $prop"
            Remove-ItemProperty -LiteralPath $keyPath -Name $prop
        }
    }
    $keyPath = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE$node\Khronos\Vulkan\ImplicitLayers"
    $key = Get-Item -LiteralPath $keyPath
    if ($key.ValueCount -gt 0)
    {
        Write-Host "`tChecking implicit Vulkan layers..."
        foreach ($prop in $key.Property)
        {
            $name = Split-Path $prop -Leaf
            if ((Test-Path $prop) -and ($name.ToLower().EndsWith('.json')))
            {
                Write-Host "`t`t$($name): OK"
            }
            else
            {
                if (-not $isAdmin)
                {
                    Elevate-Context
                    break
                }
                Write-Host "`t`t$($name): Removing"
                Remove-ItemProperty -LiteralPath $keyPath -Name $prop
            }
        }
    }
}
