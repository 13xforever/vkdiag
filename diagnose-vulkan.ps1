#!/usr/bin/pwsh

param(
    [switch]$norestart,
    [switch]$autofix,
    [switch]$cleanExplicitReg,
    [switch]$disableBadLayers
)

# use manual checks so you can get in a good state instead of simply failing when run through Explorer context menu
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]"Administrator")
$argList = "-ExecutionPolicy RemoteSigned -File $PSCommandPath -norestart"
if ($autofix)
{
    $argList = "$argList -autofix"
}
if ($cleanExplicitReg)
{
    $argList = "$argList -cleanExplicitReg"
}
if ($disableBadLayers)
{
    $argList = "$argList -disableBadLayers"
}

function Request-ContextElevation
{
    if (-not $isAdmin)
    {
        Write-Host "Restarting with elevated permissions..."
        Start-Process pwsh -Verb runAs -ArgumentList $argList
        exit
    }
}

if ($PSVersionTable.PSVersion.Major -lt 6)
{
    Write-Host "This script requires newer PowerShell version"
    Write-Host "You can get the latest version at https://github.com/PowerShell/PowerShell#get-powershell"
    if (-not $norestart)
    {
        Write-Host "Trying to restart..."
        Start-Process pwsh -Verb open -ArgumentList $argList
        break
    }
}

$is64bit = [Environment]::Is64BitOperatingSystem
if (-not $is64bit)
{
    Write-Error "This script is only intended for use with 64-bit OS"
    break
}

$hasBrokenEntries = $false
$hasExplicitDriverEntries = $false
$properDriverEntries = $false
$hasIncompatibleLayers = $false

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
            if (@('BasicDisplay', 'WUDFRd') -inotcontains $item.Service)
            {
                $name = @($item.DeviceDesc -split ';')[-1]
                Write-Host "Found inactive GPU entry: $name"
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
    $keys = @(Get-ChildItem $gpuEntryPath)
    foreach ($subkey in $keys)
    {
        $output = Split-Path $subkey.Name -Leaf
        if ($output -notmatch '\d{4}')
        {
            continue
        }

        Write-Host "`t`tOutput $output"
        $registeredVulkanDevice = $false
        foreach ($prop in @('VulkanDriverName', 'VulkanDriverNameWoW', 'VulkanImplicitLayers', 'VulkanImplicitLayersWow'))
        {
            if ((Get-Item -LiteralPath "$gpuEntryPath\$output").Property -contains $prop)
            {
                $propValue = Get-ItemPropertyValue -LiteralPath "$gpuEntryPath\$output" -Name $prop
                if (Test-Path -LiteralPath $propValue)
                {
                    if ($prop.StartsWith('VulkanDriver'))
                    {
                        $registeredVulkanDevice = $true
                    }
                }
                else
                {
                    Write-Host "`t`t`tInvalid value for property $($prop): $propValue"
                }
            }
        }
        if ($registeredVulkanDevice)
        {
            $properDriverEntries = $true
            Write-Host "`t`t`tValid Vulkan registration"
        }
        else
        {
            Write-Host "`t`t`tNot a Vulkan device"
        }
    }
}
if (-not $properDriverEntries)
{
    $cleanExplicitReg = $false
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
$knownProblematicLayers = @('obs-vulkan64.json', 'MirillisActionVulkanLayer.json')
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
    if (Test-Path -LiteralPath $keyPath)
    {
        $key = Get-Item -LiteralPath $keyPath
        if ($key.ValueCount -gt 0)
        {
            Write-Host "`tChecking explicit Vulkan driver entries..."
            foreach ($prop in $key.Property)
            {
                if ($cleanExplicitReg)
                {
                    Request-ContextElevation
                    Remove-ItemProperty -LiteralPath $keyPath -Name $prop
                    Write-Host "`t`t$prop`: removed"
                }
                elseif (Test-Path -LiteralPath $prop)
                {
                    $hasExplicitDriverEntries = $true
                    $state = "enabled"
                    if ((Get-ItemPropertyValue $keyPath -Name $prop) -eq 1)
                    {
                        $state = "disabled"
                    }
                    Write-Host "`t`t$prop`: $state"
                }
                else
                {
                    if ($autofix)
                    {
                        Request-ContextElevation
                        Remove-ItemProperty -LiteralPath $keyPath -Name $prop
                        Write-Host "`t`t$prop`: removed"
                    }
                    else
                    {
                        $hasBrokenEntries = $true
                        Write-Host "`t`t$prop`: BROKEN"
                    }
                }
            }
        }
    }
    $keyPath = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE$node\Khronos\Vulkan\ImplicitLayers"
    if (Test-Path -LiteralPath $keyPath)
    {
        $key = Get-Item -LiteralPath $keyPath
        if ($key.ValueCount -gt 0)
        {
            Write-Host "`tChecking implicit Vulkan layers..."
            foreach ($prop in $key.Property)
            {
                $name = Split-Path $prop -Leaf
                $value = Get-ItemPropertyValue -LiteralPath $keyPath -Name $prop
                if ((Test-Path $prop) -and ($name.ToLower().EndsWith('.json')))
                {
                    if (($value -eq 0) -and ($knownProblematicLayers -contains $name))
                    {
                        if ($disableBadLayers)
                        {
                            Request-ContextElevation
                            Set-ItemProperty -LiteralPath $keyPath -Name $prop -Value 1
                            Write-Host "`t`t$name`: potentially incompatible, disabled"
                        }
                        else
                        {
                            $hasIncompatibleLayers = $true
                            Write-Host "`t`t$name`: potentially incompatible"
                        }
                    }
                    else
                    {
                        if ($value -eq 0)
                        {
                            Write-Host "`t`t$($name): ok, enabled" 
                        }
                        else
                        {
                            Write-Host "`t`t$($name): ok, disabled" 
                        }
                    }
                }
                else
                {
                    if ($autofix)
                    {
                        Request-ContextElevation
                        Remove-ItemProperty -LiteralPath $keyPath -Name $prop
                        Write-Host "`t`t$($name): removed"
                    }
                    else
                    {
                        $hasBrokenEntries = $true
                        Write-Host "`t`t$($name): BROKEN"
                    }
                }
            }
        }
    }
}

if (-not $properDriverEntries)
{
    # if drivers are too old and used explicit vulkan driver registration, allow it
    $hasExplicitDriverEntries = $false
}
if ($hasExplicitDriverEntries -or $hasBrokenEntries -or $hasIncompatibleLayers)
{
    $prompt = "`nWhat would you like to do?`n"
    $options = 0
    if ($hasExplicitDriverEntries)
    {
        $prompt = "$prompt[c] Clean explicit driver registration`n"
        $options++
    }
    if ($hasIncompatibleLayers)
    {
        $prompt = "$prompt[d] Disable potentially incompatible layers`n"
        $options++
    }
    if ($hasBrokenEntries)
    {
        $prompt = "$prompt[r] Remove broken entries`n"
        $options++
    }
    if ($options -gt 1)
    {
        $prompt = "$prompt[a] All of the above`n"
    }
    $prompt = "$prompt[n] Do nothing (default)`n"
    $choice = Read-Host -Prompt $prompt
    $tryToFix = $true
    switch ($choice)
    {
        'a' { $argList = "$argList -autofix -cleanExplicitReg -disableBadLayers" }
        'r' { $argList = "$argList -autofix" }
        'c' { $argList = "$argList -cleanExplicitReg" }
        'd' { $argList = "$argList -disableBadLayers" }
        Default { $tryToFix = $false }
    }
    if ($tryToFix)
    {
        Request-ContextElevation
    }
}

Pause