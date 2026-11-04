using System.ComponentModel;
using System.Runtime.InteropServices;
using ClearPath.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace ClearPath.Windows.FileSystem;

/// <summary>Read-only queries against an open handle.</summary>
internal static class HandleQueries
{
    internal static unsafe uint GetAttributes(SafeFileHandle handle)
    {
        NativeMethods.FILE_BASIC_INFO info;
        if (!NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileBasicInfo, &info, (uint)sizeof(NativeMethods.FILE_BASIC_INFO)))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetFileInformationByHandleEx(FileBasicInfo) failed.");
        }
        return info.FileAttributes;
    }

    internal static unsafe (ulong VolumeSerial, UInt128 FileId) GetFileId(SafeFileHandle handle)
    {
        NativeMethods.FILE_ID_INFO info;
        if (!NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileIdInfo, &info, (uint)sizeof(NativeMethods.FILE_ID_INFO)))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetFileInformationByHandleEx(FileIdInfo) failed.");
        }
        return (info.VolumeSerialNumber, new UInt128(info.FileIdHigh, info.FileIdLow));
    }

    /// <summary>GetFinalPathNameByHandle with FILE_NAME_NORMALIZED | VOLUME_NAME_DOS (flags = 0).</summary>
    internal static unsafe string GetFinalPath(SafeFileHandle handle)
    {
        var capacity = 512u;
        while (true)
        {
            var buffer = new char[capacity];
            uint length;
            fixed (char* p = buffer)
            {
                length = NativeMethods.GetFinalPathNameByHandle(handle, p, capacity, 0);
            }

            if (length == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetFinalPathNameByHandle failed.");
            if (length < capacity) return new string(buffer, 0, (int)length);
            capacity = length + 1;
        }
    }
}
