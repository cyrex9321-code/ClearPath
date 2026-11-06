// ClearPath M2 capability probes. Runs only inside throwaway sandbox directories passed on the command line.
// Usage: probe <sandboxOnVolumeA> <sandboxOnVolumeB>
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32.SafeHandles;

unsafe
{
    var a = args[0]; var b = args[1];
    Directory.CreateDirectory(a); Directory.CreateDirectory(b);
    var results = new List<(string Id, string Probe, string Outcome)>();
    void R(string id, string probe, string outcome) { results.Add((id, probe, outcome)); Console.WriteLine($"{id} | {probe} | {outcome}"); }
    string Try(Func<string> f) { try { return f(); } catch (Exception ex) { return "EXCEPTION " + ex.GetType().Name + ": " + ex.Message; } }

    // ---------- helpers ----------
    const uint DELETE = 0x10000, READ_CONTROL = 0x20000, WRITE_DAC = 0x40000, WRITE_OWNER = 0x80000, SYNCHRONIZE = 0x100000;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_READ_ATTRIBUTES = 0x80, FILE_WRITE_ATTRIBUTES = 0x100;
    const uint FILE_LIST_DIRECTORY = 1, FILE_ADD_FILE = 2, FILE_TRAVERSE = 0x20;
    const uint SHARE_ALL = 7, SHARE_NONE = 0, OPEN_EXISTING = 3, CREATE_NEW = 1;
    const uint BACKUP = 0x02000000, OPEN_REPARSE = 0x00200000;

    SafeFileHandle Open(string path, uint access, uint share, uint disp = OPEN_EXISTING, uint flags = OPEN_REPARSE)
    {
        var h = N.CreateFileW(path, access, share, IntPtr.Zero, disp, flags, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile " + path);
        return h;
    }
    void Write(string path, string content) => File.WriteAllText(path, content);
    N.FILE_BASIC_INFO Basic(SafeFileHandle h) { N.FILE_BASIC_INFO i; if (!N.GetFileInformationByHandleEx(h, 0, &i, (uint)sizeof(N.FILE_BASIC_INFO))) throw new Win32Exception(); return i; }
    int RenameEx(SafeFileHandle h, IntPtr root, string target, uint flags, int cls)
    {
        var nameBytes = Encoding.Unicode.GetBytes(target);
        var size = 20 + nameBytes.Length + 2;
        var buf = new byte[size];
        fixed (byte* p = buf)
        {
            *(uint*)p = flags;              // union Flags / ReplaceIfExists
            *(IntPtr*)(p + 8) = root;
            *(uint*)(p + 16) = (uint)nameBytes.Length;
            Marshal.Copy(nameBytes, 0, (IntPtr)(p + 20), nameBytes.Length);
            return N.SetFileInformationByHandle(h, cls, p, (uint)size) ? 0 : Marshal.GetLastWin32Error();
        }
    }
    int NtRename(SafeFileHandle h, IntPtr root, string target, uint flagsOrReplace, int cls)
    {
        var nameBytes = Encoding.Unicode.GetBytes(target);
        var size = 20 + nameBytes.Length + 2;
        var buf = new byte[size];
        fixed (byte* p = buf)
        {
            *(uint*)p = flagsOrReplace;
            *(IntPtr*)(p + 8) = root;
            *(uint*)(p + 16) = (uint)nameBytes.Length;
            Marshal.Copy(nameBytes, 0, (IntPtr)(p + 20), nameBytes.Length);
            return N.NtSetInformationFile(h, out _, p, (uint)size, cls);
        }
    }

    // ---------- P1-P3: rename no-overwrite ----------
    var src = Path.Combine(a, "src1.txt"); var dst = Path.Combine(a, "dst1.txt");
    Write(src, "SOURCE"); Write(dst, "EXISTING");
    using (var h = Open(src, DELETE | SYNCHRONIZE | FILE_READ_ATTRIBUTES, SHARE_NONE))
    {
        var e1 = RenameEx(h, IntPtr.Zero, dst, 0, 22);
        R("P1", "SetFileInformationByHandle(FileRenameInfoEx, Flags=0) onto existing", $"win32 {e1}; dest content '{File.ReadAllText(dst)}'");
        var e2 = RenameEx(h, IntPtr.Zero, dst, 0, 3);
        R("P2", "SetFileInformationByHandle(FileRenameInfo, ReplaceIfExists=FALSE) onto existing", $"win32 {e2}; dest content '{File.ReadAllText(dst)}'");
        var e3 = RenameEx(h, IntPtr.Zero, dst, 0x2, 22);
        R("P3", "FileRenameInfoEx Flags=POSIX_SEMANTICS (no REPLACE) onto existing", $"win32 {e3}; dest content '{File.ReadAllText(dst)}'");
    }

    // ---------- P4: rename with RootDirectory = destination directory handle ----------
    var subA = Path.Combine(a, "destdir"); Directory.CreateDirectory(subA);
    var src4 = Path.Combine(a, "src4.txt"); Write(src4, "S4");
    using (var dir = Open(subA, FILE_LIST_DIRECTORY | FILE_ADD_FILE | FILE_TRAVERSE | SYNCHRONIZE, SHARE_ALL & ~4u, OPEN_EXISTING, BACKUP | OPEN_REPARSE))
    using (var h = Open(src4, DELETE | SYNCHRONIZE | FILE_READ_ATTRIBUTES, SHARE_NONE))
    {
        var w32 = RenameEx(h, dir.DangerousGetHandle(), "moved4.txt", 0, 22);
        R("P4a", "SetFileInformationByHandle(FileRenameInfoEx) with RootDirectory=dir handle, relative name", $"win32 {w32}; exists in dir: {File.Exists(Path.Combine(subA, "moved4.txt"))}");
        if (w32 != 0)
        {
            var st = NtRename(h, dir.DangerousGetHandle(), "moved4.txt", 0, 65);
            R("P4b", "NtSetInformationFile(FileRenameInformationEx=65) with RootDirectory, Flags=0", $"NTSTATUS 0x{st:X8}; exists in dir: {File.Exists(Path.Combine(subA, "moved4.txt"))}");
            if (st != 0)
            {
                var st2 = NtRename(h, dir.DangerousGetHandle(), "moved4.txt", 0, 10);
                R("P4c", "NtSetInformationFile(FileRenameInformation=10) with RootDirectory, ReplaceIfExists=FALSE", $"NTSTATUS 0x{st2:X8}; exists in dir: {File.Exists(Path.Combine(subA, "moved4.txt"))}");
            }
        }
    }
    // relative rename onto existing name in dir
    var src4d = Path.Combine(a, "src4d.txt"); Write(src4d, "S4D");
    using (var dir = Open(subA, FILE_LIST_DIRECTORY | FILE_ADD_FILE | FILE_TRAVERSE | SYNCHRONIZE, SHARE_ALL & ~4u, OPEN_EXISTING, BACKUP | OPEN_REPARSE))
    using (var h = Open(src4d, DELETE | SYNCHRONIZE | FILE_READ_ATTRIBUTES, SHARE_NONE))
    {
        var st = NtRename(h, dir.DangerousGetHandle(), "moved4.txt", 0, 65);
        R("P4d", "NtSetInformationFile(FileRenameInformationEx) relative onto EXISTING name, Flags=0", $"NTSTATUS 0x{st:X8}; existing content '{File.ReadAllText(Path.Combine(subA, "moved4.txt"))}'");
    }

    // ---------- P5: rename across volumes via handle ----------
    var src5 = Path.Combine(a, "src5.txt"); Write(src5, "S5");
    using (var h = Open(src5, DELETE | SYNCHRONIZE | FILE_READ_ATTRIBUTES, SHARE_NONE))
    {
        var e = RenameEx(h, IntPtr.Zero, Path.Combine(b, "x5.txt"), 0, 22);
        R("P5", "FileRenameInfoEx to another volume", $"win32 {e} (17 = ERROR_NOT_SAME_DEVICE)");
    }

    // ---------- P6-P7: delete disposition and post-close verification ----------
    var del = Path.Combine(a, "del6.txt"); Write(del, "D6");
    using (var other = Open(del, FILE_READ_ATTRIBUTES, SHARE_ALL))
    {
        using (var h = Open(del, DELETE | SYNCHRONIZE | FILE_READ_ATTRIBUTES, SHARE_ALL))
        {
            uint flags = 0x1; // DELETE only
            var ok = N.SetFileInformationByHandle(h, 21, &flags, 4);
            R("P6a", "FileDispositionInfoEx(DELETE) while another handle is open", ok ? "ok" : $"win32 {Marshal.GetLastWin32Error()}");
        }
        R("P6b", "Entry visible after deleting handle closed but other handle still open", $"File.Exists={File.Exists(del)}");
    }
    R("P6c", "Entry visible after all handles closed", $"File.Exists={File.Exists(del)}");
    var del7 = Path.Combine(a, "del7.txt"); Write(del7, "D7");
    using (var h = Open(del7, DELETE | SYNCHRONIZE, SHARE_NONE))
    {
        byte d = 1;
        var ok = N.SetFileInformationByHandle(h, 4, &d, 1);
        R("P7a", "FileDispositionInfo(DeleteFile=TRUE)", ok ? "ok" : $"win32 {Marshal.GetLastWin32Error()}");
    }
    R("P7b", "Entry after close", $"File.Exists={File.Exists(del7)}");

    // ---------- P8: timestamps incl. ChangeTime; LastAccess on read ----------
    var ts = Path.Combine(b, "ts8.txt"); Write(ts, "TIMESTAMPS");
    using (var h = Open(ts, FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | SYNCHRONIZE, SHARE_NONE))
    {
        var target = new N.FILE_BASIC_INFO { CreationTime = 130000000000000000, LastAccessTime = 130000000010000000, LastWriteTime = 130000000020000000, ChangeTime = 130000000030000000, FileAttributes = 0 };
        var ok = N.SetFileInformationByHandle(h, 0, &target, (uint)sizeof(N.FILE_BASIC_INFO));
        var back = Basic(h);
        R("P8a", "Set FileBasicInfo all four times, read back on same handle", ok ? $"C={back.CreationTime == target.CreationTime} A={back.LastAccessTime == target.LastAccessTime} W={back.LastWriteTime == target.LastWriteTime} Change={back.ChangeTime == target.ChangeTime} (ChangeTime read {back.ChangeTime})" : $"win32 {Marshal.GetLastWin32Error()}");
    }
    using (var h = Open(ts, FILE_READ_ATTRIBUTES | SYNCHRONIZE, SHARE_ALL))
    {
        var back = Basic(h);
        R("P8b", "Times after handle close + reopen", $"C={back.CreationTime == 130000000000000000} A={back.LastAccessTime == 130000000010000000} W={back.LastWriteTime == 130000000020000000} Change={back.ChangeTime == 130000000030000000}");
    }
    var la = Path.Combine(a, "la8.txt"); Write(la, new string('x', 100000));
    long before;
    using (var h = Open(la, FILE_READ_ATTRIBUTES | SYNCHRONIZE, SHARE_ALL)) before = Basic(h).LastAccessTime;
    Thread.Sleep(1500);
    using (var fs = new FileStream(la, FileMode.Open, FileAccess.Read)) { var buf = new byte[100000]; fs.ReadExactly(buf); }
    using (var h = Open(la, FILE_READ_ATTRIBUTES | SYNCHRONIZE, SHARE_ALL)) R("P8c", "LastAccessTime changes when content is read", $"changed={Basic(h).LastAccessTime != before}");
    R("P8d", "fsutil disablelastaccess", Try(() => { var psi = new System.Diagnostics.ProcessStartInfo("fsutil", "behavior query disablelastaccess") { RedirectStandardOutput = true }; using var pr = System.Diagnostics.Process.Start(psi)!; return pr.StandardOutput.ReadToEnd().Trim(); }));

    // ---------- P9: security descriptor read/apply as non-admin ----------
    var sdSrc = Path.Combine(a, "sd9.txt"); Write(sdSrc, "SD");
    var sdDst = Path.Combine(b, "sd9.txt"); Write(sdDst, "SD");
    // Give the source an explicit protected DACL so it differs from the destination's inherited one.
    var fsSec = new FileInfo(sdSrc).GetAccessControl();
    fsSec.SetAccessRuleProtection(true, true); new FileInfo(sdSrc).SetAccessControl(fsSec);
    string Sddl(SafeFileHandle h, uint info)
    {
        var rc = N.GetSecurityInfo(h, 1, info, out _, out _, out _, out _, out var psd);
        if (rc != 0) return "ERR " + rc;
        var len = N.GetSecurityDescriptorLength(psd); var bytes = new byte[len]; Marshal.Copy(psd, bytes, 0, (int)len); N.LocalFree(psd);
        return new RawSecurityDescriptor(bytes, 0).GetSddlForm(AccessControlSections.All);
    }
    using (var s = Open(sdSrc, READ_CONTROL | SYNCHRONIZE, SHARE_ALL))
    {
        R("P9a", "GetSecurityInfo OWNER|GROUP|DACL|LABEL with READ_CONTROL", Sddl(s, 1 | 2 | 4 | 0x10));
        R("P9b", "GetSecurityInfo SACL without SeSecurityPrivilege", Sddl(s, 8));
        R("P9c", "Open with ACCESS_SYSTEM_SECURITY (0x01000000) non-admin", Try(() => { using var x = Open(sdSrc, 0x01000000 | READ_CONTROL, SHARE_ALL); return "opened"; }));
        var rc = N.GetSecurityInfo(s, 1, 1 | 2 | 4, out var owner, out var group, out var dacl, out _, out var psd);
        var ctrl = (uint)0; N.GetSecurityDescriptorControl(psd, out var c, out _); ctrl = c;
        var prot = (ctrl & 0x1000) != 0; // SE_DACL_PROTECTED
        R("P9g", "Open other-volume dest with WRITE_OWNER (non-admin)", Try(() => { using var x = Open(sdDst, READ_CONTROL | WRITE_OWNER, SHARE_ALL); return "opened"; }));
        using var d = Open(sdDst, READ_CONTROL | WRITE_DAC | SYNCHRONIZE, SHARE_ALL);
        var before9 = Sddl(d, 1 | 2 | 4);
        var ownerGroupEqualBefore = Sddl(d, 1 | 2) == Sddl(s, 1 | 2);
        uint si = 4 | (prot ? 0x80000000u : 0x20000000u);
        var rc2 = N.SetSecurityInfo(d, 1, si, IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
        var srcS = Sddl(s, 1 | 2 | 4 | 0x10); var dstS = Sddl(d, 1 | 2 | 4 | 0x10);
        R("P9d", "Owner+group already equal on newly created dest (creator = owner)", $"{ownerGroupEqualBefore}");
        R("P9e", "SetSecurityInfo DACL(+PROTECTED flag) with WRITE_DAC only, then compare full SD incl. label", $"rc {rc2}; equal-after={srcS == dstS}; dest-before-differed={before9 != Sddl(s, 1 | 2 | 4)}");
        R("P9f", "dest SDDL after", dstS);
        N.LocalFree(psd);
    }

    // ---------- P10: streams, EA, link count, attributes ----------
    var st10 = Path.Combine(a, "ads10.txt"); Write(st10, "MAIN"); File.WriteAllText(st10 + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3");
    using (var h = Open(st10, FILE_READ_ATTRIBUTES | SYNCHRONIZE, SHARE_ALL))
    {
        var buf = new byte[4096];
        fixed (byte* p = buf)
        {
            var ok = N.GetFileInformationByHandleEx(h, 7, p, (uint)buf.Length);
            var names = new List<string>(); var off = 0;
            while (ok) { var next = *(uint*)(p + off); var nl = *(uint*)(p + off + 4); names.Add(new string((char*)(p + off + 24), 0, (int)nl / 2)); if (next == 0) break; off += (int)next; }
            R("P10a", "GetFileInformationByHandleEx(FileStreamInfo) lists ADS", ok ? string.Join(", ", names) : $"win32 {Marshal.GetLastWin32Error()}");
        }
        uint ea; var st = N.NtQueryInformationFile(h, out _, &ea, 4, 7);
        R("P10b", "NtQueryInformationFile(FileEaInformation)", $"NTSTATUS 0x{st:X8} EaSize={ea}");
        var std = stackalloc byte[24];
        N.GetFileInformationByHandleEx(h, 1, std, 24);
        R("P10c", "FileStandardInfo.NumberOfLinks (1 link)", $"{*(uint*)(std + 16)}");
    }
    var hl = Path.Combine(a, "hl10.txt"); Write(hl, "HL"); N.CreateHardLinkW(Path.Combine(a, "hl10b.txt"), hl, IntPtr.Zero);
    using (var h = Open(hl, FILE_READ_ATTRIBUTES | SYNCHRONIZE, SHARE_ALL)) { var std = stackalloc byte[24]; N.GetFileInformationByHandleEx(h, 1, std, 24); R("P10d", "NumberOfLinks after CreateHardLink", $"{*(uint*)(std + 16)}"); }

    // ---------- P11: handle-based directory enumeration ----------
    using (var dir = Open(a, FILE_LIST_DIRECTORY | SYNCHRONIZE, SHARE_ALL, OPEN_EXISTING, BACKUP | OPEN_REPARSE))
    {
        var buf = new byte[64 * 1024]; var count = 0; var hasId = false;
        fixed (byte* p = buf)
        {
            var cls = 20; // FileIdExtdDirectoryRestartInfo
            while (N.GetFileInformationByHandleEx(dir, cls, p, (uint)buf.Length))
            {
                var off = 0;
                while (true) { count++; if (*(ulong*)(p + off + 72) != 0 || *(ulong*)(p + off + 80) != 0) hasId = true; var next = *(uint*)(p + off); if (next == 0) break; off += (int)next; }
                cls = 19;
            }
            var err = Marshal.GetLastWin32Error();
            R("P11", "Enumerate via GetFileInformationByHandleEx(FileIdExtdDirectoryInfo) on dir handle", $"entries={count} (incl . and ..) 128-bit ids present={hasId} end-error={err} (18 = NO_MORE_FILES)");
        }
    }

    // ---------- P12: FILE_CREATE relative to dir handle ----------
    using (var dir = Open(a, FILE_LIST_DIRECTORY | FILE_ADD_FILE | FILE_TRAVERSE | SYNCHRONIZE, SHARE_ALL & ~4u, OPEN_EXISTING, BACKUP | OPEN_REPARSE))
    {
        int Create(string name)
        {
            fixed (char* n = name)
            {
                var us = new N.UNICODE_STRING { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = n };
                var oa = new N.OBJECT_ATTRIBUTES { Length = (uint)sizeof(N.OBJECT_ATTRIBUTES), RootDirectory = dir.DangerousGetHandle(), ObjectName = &us, Attributes = 0x40 };
                var st = N.NtCreateFile(out var fh, GENERIC_WRITE | SYNCHRONIZE, &oa, out _, null, 0x80, 0, 2 /*FILE_CREATE*/, 0x20 | 0x40 /*SYNC_NONALERT|NON_DIRECTORY*/, IntPtr.Zero, 0);
                if (st == 0) N.CloseHandle(fh);
                return st;
            }
        }
        R("P12a", "NtCreateFile(FILE_CREATE) relative new name", $"NTSTATUS 0x{Create("created12.partial"):X8}");
        R("P12b", "NtCreateFile(FILE_CREATE) relative existing name", $"NTSTATUS 0x{Create("created12.partial"):X8} (0xC0000035 = NAME_COLLISION)");
    }

    // ---------- P13: GetFinalPathNameByHandle long path ----------
    var deep = a; for (var i = 0; i < 12; i++) deep = Path.Combine(deep, new string((char)('a' + i), 30));
    Directory.CreateDirectory(deep); var lf = Path.Combine(deep, "long.txt"); File.WriteAllText(lf, "L");
    using (var h = Open(@"\\?\" + lf, FILE_READ_ATTRIBUTES | SYNCHRONIZE, SHARE_ALL))
    {
        var sb = new char[2048]; uint n;
        fixed (char* q = sb) n = N.GetFinalPathNameByHandleW(h, q, 2048, 0);
        var fp = new string(sb, 0, (int)n);
        R("P13", "GetFinalPathNameByHandle on >260-char path", $"len={fp.Length} prefix={fp[..4]}");
    }

    File.WriteAllLines(Path.Combine(a, "..", "probe-results.txt"), results.Select(r => $"{r.Id}\t{r.Probe}\t{r.Outcome}"));
}

static unsafe partial class N
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetFileInformationByHandle(SafeFileHandle h, int c, void* b, uint s);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int c, void* b, uint s);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, char* b, uint l, uint f);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool CreateHardLinkW(string n, string e, IntPtr sa);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr h);
    [DllImport("ntdll.dll")] public static extern int NtSetInformationFile(SafeFileHandle h, out IO_STATUS_BLOCK io, void* b, uint l, int c);
    [DllImport("ntdll.dll")] public static extern int NtQueryInformationFile(SafeFileHandle h, out IO_STATUS_BLOCK io, void* b, uint l, int c);
    [DllImport("ntdll.dll")] public static extern int NtCreateFile(out IntPtr h, uint a, OBJECT_ATTRIBUTES* oa, out IO_STATUS_BLOCK io, long* al, uint fa, uint sa, uint cd, uint co, IntPtr ea, uint el);
    [DllImport("advapi32.dll")] public static extern uint GetSecurityInfo(SafeFileHandle h, int t, uint si, out IntPtr o, out IntPtr g, out IntPtr d, out IntPtr s, out IntPtr sd);
    [DllImport("advapi32.dll")] public static extern uint SetSecurityInfo(SafeFileHandle h, int t, uint si, IntPtr o, IntPtr g, IntPtr d, IntPtr s);
    [DllImport("advapi32.dll")] public static extern uint GetSecurityDescriptorLength(IntPtr sd);
    [DllImport("advapi32.dll")] public static extern bool GetSecurityDescriptorControl(IntPtr sd, out ushort c, out uint r);
    [StructLayout(LayoutKind.Sequential)] public struct FILE_BASIC_INFO { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint FileAttributes; }
    [StructLayout(LayoutKind.Sequential)] public struct IO_STATUS_BLOCK { public IntPtr Status, Information; }
    [StructLayout(LayoutKind.Sequential)] public struct UNICODE_STRING { public ushort Length, MaximumLength; public char* Buffer; }
    [StructLayout(LayoutKind.Sequential)] public struct OBJECT_ATTRIBUTES { public uint Length; public IntPtr RootDirectory; public UNICODE_STRING* ObjectName; public uint Attributes; public IntPtr SD, SQOS; }
}
