using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace VkDiag.Interop;

public static unsafe class PackageManager
{
    public static List<string> FindPackagesByPackageFamily(string packageFamily)
    {
        uint count = 0, bufferLength = 0;
        var result = PInvoke.FindPackagesByPackageFamily(
            packageFamily,
            PInvoke.PACKAGE_FILTER_HEAD,
            ref count,
            default,
            ref bufferLength,
            default,
            default
        );
        if (result is not WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            throw new Win32Exception((int)result, "Failed to query package list");
        
        if (count is 0)
            return [];
        
        var pkgFullNameList = new PWSTR[count];
        var charBuf = new char[bufferLength];
        uint props = 0;
        fixed (PWSTR* pPkgFullNames = pkgFullNameList)
        fixed (char* pBuf = charBuf)
        {
            var pkgNamesBuf = new PWSTR(pBuf);
            result = PInvoke.FindPackagesByPackageFamily(
                packageFamily,
                PInvoke.PACKAGE_FILTER_HEAD,
                ref count,
                pPkgFullNames,
                ref bufferLength,
                pkgNamesBuf,
                &props
            );
        }
        if (result is not WIN32_ERROR.NO_ERROR)
            throw new Win32Exception((int)result, "Failed to retrieve package names");
        
        return pkgFullNameList.Select(n => n.ToString()).ToList();
    }

    public static string GetPackageVersion(string packageFullName, string defaultVersion)
    {
        if (PInvoke.OpenPackageInfoByFullName(packageFullName, out var pkgRef) is WIN32_ERROR.NO_ERROR)
            try
            {
                var handle = Process.GetCurrentProcess().SafeHandle;
                uint bufLen = 0;
                var result = PInvoke.GetPackageInfo(
                    pkgRef,
                    PInvoke.PACKAGE_INFORMATION_BASIC,
                    &bufLen
                );
                if (result is not WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
                    return defaultVersion;

                var buf = new byte[bufLen];
                uint count = 0;
                fixed (byte* pBuf = buf)
                {
                    result = PInvoke.GetPackageInfo(
                        pkgRef,
                        PInvoke.PACKAGE_INFORMATION_BASIC,
                        &bufLen,
                        pBuf,
                        &count
                    );
                }
                if (result is not WIN32_ERROR.NO_ERROR)
                    return defaultVersion;
                // PACKAGE_INFO https://learn.microsoft.com/en-us/windows/win32/api/appmodel/ns-appmodel-package_info

                var pkgIdOffset = 4 + 4 + IntPtr.Size * 3;
                var revision = BitConverter.ToUInt16(buf, pkgIdOffset + 8);
                var build = BitConverter.ToUInt16(buf, pkgIdOffset + 10);
                var minor = BitConverter.ToUInt16(buf, pkgIdOffset + 12);
                var major = BitConverter.ToUInt16(buf, pkgIdOffset + 14);
                return $" v{major}.{minor}.{build}.{revision}";
            }
            finally
            {
                PInvoke.ClosePackageInfo(pkgRef);
            }
        return defaultVersion;
    }

    public static string GetAppStoreName(string packageFullName, string defaultName)
    {
        Span<char> staticBuf = stackalloc char[512];
        var source = $"@{{{packageFullName}?ms-resource://Microsoft.D3DMappingLayers/Resources/AppStoreName}}";
        fixed (char* pBuf = staticBuf)
        {
            var output = new PWSTR(pBuf);
            if (PInvoke.SHLoadIndirectString(source, output, (uint)staticBuf.Length).Succeeded)
                return output.ToString();
        }
        return defaultName;
    }
}