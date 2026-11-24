using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MemTidy;

/// <summary>
/// Lists which files occupy the standby list (the cache), like RAMMap's File Summary.
///
/// Uses the same UNDOCUMENTED interfaces as RAMMap / Process Hacker's MemoryExtPlugin:
///   1. NtQuerySystemInformation(SystemSuperfetchInformation) → physical memory ranges and, for each
///      physical page, which list it is on (standby…), what it is used for (file…) and its file key;
///   2. the FileInfo filter driver's log (\Device\FileInfo, IOCTL 0x22000F) → file key → file name.
/// Requires running as administrator. Layouts follow zodiacon's MemInfo.h / Process Hacker's pfn.c and
/// may change with Windows updates, so every step fails soft with a readable error.
/// </summary>
static class StandbyFiles
{
    public sealed record Report(long StandbyBytes, long FileBytes, long NamedBytes, List<(string Path, long Bytes)> Files,
                                long[] StandbyByPriority, TimeSpan Elapsed, string? Error);

    const int PageSize = 4096;
    const int SystemSuperfetchInformation = 79;
    const int SuperfetchPfnQuery = 6, SuperfetchMemoryRangesQuery = 17;
    const uint SuperfetchVersion = 45, SuperfetchMagic = 0x6B756843; // 'kuhC'
    const int StatusBufferTooSmall = unchecked((int)0xC0000023), StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    const int ListStandby = 2, UseFile = 1;
    const int PfnRequestHeader = 192, PfnIdentitySize = 24; // PF_PFN_PRIO_REQUEST.PageData offset, sizeof(MMPFN_IDENTITY)
    const int PfnBatch = 65536;

    [StructLayout(LayoutKind.Sequential)]
    struct SUPERFETCH_INFORMATION
    {
        public uint Version;
        public uint Magic;
        public int InfoClass;
        public IntPtr Data;
        public uint Length;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int infoClass, ref SUPERFETCH_INFORMATION info, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    static extern int RtlAdjustPrivilege(int privilege, bool enable, bool currentThread, out bool wasEnabled);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int QueryDosDeviceW(string deviceName, StringBuilder target, int max);

    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static int Superfetch(int infoClass, IntPtr data, int length)
    {
        var info = new SUPERFETCH_INFORMATION { Version = SuperfetchVersion, Magic = SuperfetchMagic, InfoClass = infoClass, Data = data, Length = (uint)length };
        return NtQuerySystemInformation(SystemSuperfetchInformation, ref info, Marshal.SizeOf<SUPERFETCH_INFORMATION>(), out _);
    }

    public static Report Query(int top = 500)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var byPriority = new long[8];
        Report Fail(string error) => new(0, 0, 0, [], byPriority, sw.Elapsed, error);

        if (!IsAdmin) return Fail("需要以系統管理員身分執行");
        RtlAdjustPrivilege(13, true, false, out _); // SeProfileSingleProcessPrivilege
        RtlAdjustPrivilege(20, true, false, out _); // SeDebugPrivilege

        // Names first, as Process Hacker does: the FileInfo log maps file keys to paths.
        var names = ReadFileNames(out var nameError);

        var ranges = ReadRanges(out var rangeError);
        if (ranges == null) return Fail($"無法取得實體記憶體範圍：{rangeError}");

        var perFile = new Dictionary<ulong, long>();
        long standbyPages = 0, filePages = 0;
        var buf = Marshal.AllocHGlobal(PfnRequestHeader + PfnBatch * PfnIdentitySize);
        try
        {
            var batch = new List<ulong>(PfnBatch);
            foreach (var (basePfn, count) in ranges)
            {
                for (ulong pfn = basePfn; pfn < basePfn + count; pfn++)
                {
                    batch.Add(pfn);
                    if (batch.Count == PfnBatch && !Flush()) return Fail(pfnError!);
                }
            }
            if (batch.Count > 0 && !Flush()) return Fail(pfnError!);

            bool Flush()
            {
                int n = batch.Count, size = PfnRequestHeader + n * PfnIdentitySize;
                unsafe
                {
                    new Span<byte>((void*)buf, size).Clear();
                }
                Marshal.WriteInt32(buf, 0, 1);        // Version
                Marshal.WriteInt32(buf, 4, 1);        // RequestFlags
                Marshal.WriteInt64(buf, 8, n);        // PfnCount
                for (int i = 0; i < n; i++)
                    Marshal.WriteInt64(buf, PfnRequestHeader + i * PfnIdentitySize + 8, (long)batch[i]); // PageFrameIndex
                int status = Superfetch(SuperfetchPfnQuery, buf, size);
                if (status < 0) { pfnError = $"查詢實體頁面失敗（0x{status:X8}）"; return false; }

                for (int i = 0; i < n; i++)
                {
                    int off = PfnRequestHeader + i * PfnIdentitySize;
                    ulong u1 = (ulong)Marshal.ReadInt64(buf, off);
                    int use = (int)(u1 & 0xF), list = (int)((u1 >> 4) & 0x7), priority = (int)((u1 >> 57) & 0x7);
                    if (list != ListStandby) continue;
                    standbyPages++;
                    byPriority[priority] += PageSize;
                    if (use != UseFile) continue;
                    filePages++;
                    ulong key = (ulong)Marshal.ReadInt64(buf, off + 16) & ~1UL;
                    if (key != 0) perFile[key] = perFile.GetValueOrDefault(key) + 1;
                }
                batch.Clear();
                return true;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }

        long named = 0;
        var files = new List<(string, long)>();
        long unnamed = 0;
        foreach (var (key, pages) in perFile)
        {
            if (names.TryGetValue(key, out var path)) { files.Add((path, pages * PageSize)); named += pages * PageSize; }
            else unnamed += pages * PageSize;
        }
        // Several keys can resolve to the same path (e.g. image vs. data sections): merge them.
        files = files.GroupBy(f => f.Item1, StringComparer.OrdinalIgnoreCase)
                     .Select(g => (g.Key, g.Sum(x => x.Item2)))
                     .OrderByDescending(f => f.Item2).ToList();
        if (unnamed > 0) files.Add(("（無法對應檔名）", unnamed));
        if (files.Count > top) files = files.Take(top).ToList();

        string? warn = names.Count == 0 ? $"無法取得檔名：{nameError}" : null;
        return new Report(standbyPages * PageSize, filePages * PageSize, named, files, byPriority, sw.Elapsed, warn);
    }

    static string? pfnError;

    /// <summary>Physical memory ranges (PF_PHYSICAL_MEMORY_RANGE_INFO V2, falling back to V1).</summary>
    static List<(ulong BasePfn, ulong Count)>? ReadRanges(out string? error)
    {
        error = null;
        foreach (int version in new[] { 2, 1 })
        {
            int size = 4096;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    unsafe { new Span<byte>((void*)buf, size).Clear(); }
                    Marshal.WriteInt32(buf, 0, version);
                    int status = Superfetch(SuperfetchMemoryRangesQuery, buf, size);
                    if (status == StatusBufferTooSmall || status == StatusInfoLengthMismatch) { size *= 4; continue; }
                    if (status < 0) { error = $"0x{status:X8}"; break; }

                    // V2: Version, Flags, RangeCount @8, ranges @16.  V1: Version, RangeCount @4, ranges @8.
                    int countOff = version == 2 ? 8 : 4, first = version == 2 ? 16 : 8;
                    int count = Marshal.ReadInt32(buf, countOff);
                    if (count <= 0 || first + count * 16 > size) { error = "範圍資料不合理"; break; }
                    var list = new List<(ulong, ulong)>(count);
                    for (int i = 0; i < count; i++)
                        list.Add(((ulong)Marshal.ReadInt64(buf, first + i * 16), (ulong)Marshal.ReadInt64(buf, first + i * 16 + 8)));
                    return list;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        return null;
    }

    /// <summary>File key → full path, from the FileInfo driver's log of files and volumes it has seen.</summary>
    static Dictionary<ulong, string> ReadFileNames(out string? error)
    {
        error = null;
        var result = new Dictionary<ulong, string>();
        using var dev = CreateFileW(@"\\?\GLOBALROOT\Device\FileInfo", 0x80 | 0x100000 /* READ_ATTRIBUTES | SYNCHRONIZE */,
                                    7, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (dev.IsInvalid) { error = $"無法開啟 FileInfo（錯誤 {Marshal.GetLastWin32Error()}）"; return result; }

        const int outSize = 16 * 1024 * 1024;
        var outBuf = Marshal.AllocHGlobal(outSize);
        try
        {
            // The driver checks the request version strictly and it differs between Windows builds: 15 is known
            // for Windows 10/11; probe nearby values if that is rejected.
            int[] versions = [15, .. Enumerable.Range(10, 15).Where(v => v != 15)];
            bool ok = false;
            foreach (var v in versions)
            {
                var input = new byte[12];
                BitConverter.GetBytes(v).CopyTo(input, 0);
                BitConverter.GetBytes(3).CopyTo(input, 4);   // LogForETW | LogForSuperfetch
                BitConverter.GetBytes(1).CopyTo(input, 8);   // ETWLoggerId
                if (DeviceIoControl(dev, 0x22000F, input, input.Length, outBuf, outSize, out _, IntPtr.Zero)) { ok = true; break; }
                error = $"FileInfo 查詢失敗（錯誤 {Marshal.GetLastWin32Error()}）";
            }
            if (!ok) return result;
            error = null;

            var volumes = new Dictionary<uint, string>();
            var dosNames = DosDeviceMap();
            int bufferSize = Math.Min(Marshal.ReadInt32(outBuf, 8), outSize);
            int off = 40; // after PFFI_UNKNOWN
            while (off + 16 <= bufferSize)
            {
                uint head = (uint)Marshal.ReadInt32(outBuf, off);
                int type = (int)(head & 0x7), size = (int)((head >> 3) & 0x0FFFFFFF);
                if (size < 16 || off + size > bufferSize) break;
                int p = off + 16; // union is 8-byte aligned after the 12-byte header
                if (type == 2) // volume
                {
                    uint key = (uint)Marshal.ReadInt32(outBuf, p + 8);
                    var path = ReadString(outBuf, p + 40, off + size);
                    volumes[key] = ToDosPath(path, dosNames);
                }
                else if (type == 0) // file
                {
                    ulong key = (ulong)Marshal.ReadInt64(outBuf, p);
                    uint volKey = (uint)Marshal.ReadInt32(outBuf, p + 8);
                    var name = ReadString(outBuf, p + 28, off + size);
                    if (name.Length > 0)
                        result[key] = volumes.TryGetValue(volKey, out var vol) ? vol.TrimEnd('\\') + (name.StartsWith('\\') ? "" : "\\") + name : name;
                }
                off += size;
            }
        }
        finally { Marshal.FreeHGlobal(outBuf); }
        return result;
    }

    static string ReadString(IntPtr buf, int start, int end)
    {
        var sb = new StringBuilder();
        for (int i = start; i + 1 < end; i += 2)
        {
            char c = (char)Marshal.ReadInt16(buf, i);
            if (c == '\0') break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>"\Device\HarddiskVolume3" → "C:" for every drive letter.</summary>
    static Dictionary<string, string> DosDeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in Environment.GetLogicalDrives())
        {
            var letter = drive.TrimEnd('\\');
            var sb = new StringBuilder(512);
            if (QueryDosDeviceW(letter, sb, sb.Capacity) > 0) map[sb.ToString()] = letter;
        }
        return map;
    }

    static string ToDosPath(string device, Dictionary<string, string> map)
    {
        foreach (var (dev, letter) in map)
            if (device.StartsWith(dev, StringComparison.OrdinalIgnoreCase)) return letter + device[dev.Length..];
        return device;
    }

    /// <summary>Plain-text report, used by --cache-report.</summary>
    public static string Format(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"MemTidy 快取內容報告  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  （耗時 {r.Elapsed.TotalSeconds:0.0} 秒）");
        if (r.Error != null) sb.AppendLine($"注意：{r.Error}");
        sb.AppendLine($"快取（待命清單）總計 {MemTidy.Format.Bytes(r.StandbyBytes)}，其中檔案 {MemTidy.Format.Bytes(r.FileBytes)}，已對應檔名 {MemTidy.Format.Bytes(r.NamedBytes)}");
        sb.AppendLine("依保留優先順序（7 最優先保留）：" + string.Join("  ", r.StandbyByPriority.Select((b, i) => $"{i}:{MemTidy.Format.Bytes(b)}")));
        sb.AppendLine();
        foreach (var (path, bytes) in r.Files) sb.AppendLine($"{MemTidy.Format.Bytes(bytes),10}  {path}");
        return sb.ToString();
    }
}
