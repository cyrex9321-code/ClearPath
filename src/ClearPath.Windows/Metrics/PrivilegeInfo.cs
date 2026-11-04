using System.Runtime.InteropServices;
using System.Security.Principal;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Models;
using ClearPath.Windows.Native;
using Microsoft.Extensions.Logging;

namespace ClearPath.Windows.Metrics;

public sealed class PrivilegeInfo(ILogger<PrivilegeInfo> logger) : IPrivilegeInfo
{
    private const string AdministratorsSid = "S-1-5-32-544";

    public Reading<PrivilegeStatus> GetCurrent()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            // Under UAC the filtered token lists Administrators as a deny-only group; membership is still informative.
            var isAdminMember = identity.Groups?.Any(g => g.Value == AdministratorsSid) == true;
            var elevated = QueryElevation(identity.Token);
            var integrity = QueryIntegrityLevel(identity.Token);

            return Reading<PrivilegeStatus>.Of(new PrivilegeStatus(elevated, isAdminMember, identity.Name, integrity));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or ExternalException)
        {
            logger.LogWarning(ex, "Token information could not be read");
            return Reading<PrivilegeStatus>.Unavailable("Token information could not be read.");
        }
    }

    private static bool QueryElevation(IntPtr token)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, buffer, sizeof(int), out _))
            {
                throw new ExternalException("GetTokenInformation(TokenElevation) failed", Marshal.GetLastPInvokeError());
            }
            return Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string QueryIntegrityLevel(IntPtr token)
    {
        NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
        if (length <= 0) return "Unknown";

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buffer, length, out _)) return "Unknown";

            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label { PSID Sid; DWORD Attributes; } }
            var sid = Marshal.ReadIntPtr(buffer);
            var count = Marshal.ReadByte(NativeMethods.GetSidSubAuthorityCount(sid));
            if (count == 0) return "Unknown";
            var rid = (uint)Marshal.ReadInt32(NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1)));
            return rid switch
            {
                < 0x1000 => "Untrusted",
                < 0x2000 => "Low",
                < 0x3000 => "Medium",
                < 0x4000 => "High",
                _ => "System",
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
