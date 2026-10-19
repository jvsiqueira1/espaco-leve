using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EspacoLeve.Core;

public static class PathGuard
{
    public static bool IsInside(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(candidate);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOrdinaryPath(string root, string candidate, bool includeLeaf = true)
    {
        if (!IsInside(root, candidate) && !string.Equals(Path.GetFullPath(root), Path.GetFullPath(candidate), StringComparison.OrdinalIgnoreCase))
            return false;
        var path = includeLeaf ? Path.GetFullPath(candidate) : Path.GetDirectoryName(Path.GetFullPath(candidate))!;
        // Inspect every ancestor, including the root's parents. Do not follow junctions.
        while (!string.IsNullOrEmpty(path))
        {
            try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false; }
            catch { return false; }
            path = Path.GetDirectoryName(path);
        }
        return true;
    }
}

public static class SafeFiles
{
    static readonly HashSet<string> ProtectedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vhd", ".vhdx", ".db", ".sqlite", ".sqlite3", ".mdf", ".ldf", ".pst", ".ost",
        ".kdbx", ".key", ".pem", ".pfx", ".asd", ".wbk", ".sav", ".bak", ".backup",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".csv", ".tsv", ".rtf",
        ".txt", ".md", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".conf", ".env",
        ".sql", ".py", ".cs", ".js", ".ts", ".html", ".css", ".psd", ".png", ".jpg", ".jpeg",
        ".zip", ".7z", ".rar"
    };

    public static bool NameAllowed(CleanupRule rule, string file)
    {
        if (ProtectedExtensions.Contains(Path.GetExtension(file))) return false;
        var relative = Path.GetRelativePath(rule.Root!, file);
        if (relative.Split(Path.DirectorySeparatorChar).Any(x =>
                x.Contains("autosave", StringComparison.OrdinalIgnoreCase) ||
                x.Contains("backup", StringComparison.OrdinalIgnoreCase) ||
                x.Contains("recovery", StringComparison.OrdinalIgnoreCase))) return false;
        return rule.FilePrefixes is null ||
            rule.FilePrefixes.Any(prefix => Path.GetFileName(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static ScanRow Scan(CleanupRule rule, DateTime utcNow, CancellationToken cancellation = default)
    {
        long bytes = 0;
        int count = 0, skipped = 0;
        if (rule.Root is null || !Directory.Exists(rule.Root)) return new(rule, 0, 0, 0, "Não encontrado");
        foreach (var info in EnumerateInfo(rule.Root, () => skipped++, cancellation))
        {
            try
            {
                if (rule.Kind == CleanupKind.OldFiles && !Eligible(rule, info, utcNow)) continue;
                bytes += info.Length;
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(rule, bytes, count, skipped, count == 0 ? "Nada a limpar" : rule.Automatic ? "Pode limpar automaticamente" : "Requer revisão");
    }

    public static IEnumerable<string> Enumerate(string root, Action skipped, CancellationToken cancellation) =>
        EnumerateInfo(root, skipped, cancellation).Select(x => x.FullName);

    static IEnumerable<FileInfo> EnumerateInfo(string root, Action skipped, CancellationToken cancellation)
    {
        if (!Directory.Exists(root) || !PathGuard.IsOrdinaryPath(root, root)) yield break;
        var folders = new Stack<string>();
        folders.Push(root);
        while (folders.TryPop(out var folder))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) { skipped(); continue; }
            }
            catch { skipped(); continue; }
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(folder).GetFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped(); continue; }
            foreach (var entry in entries)
            {
                cancellation.ThrowIfCancellationRequested();
                FileAttributes attrs;
                try { attrs = entry.Attributes; }
                catch { skipped(); continue; }
                if ((attrs & FileAttributes.ReparsePoint) != 0) { skipped(); continue; }
                if ((attrs & FileAttributes.Directory) != 0) folders.Push(entry.FullName);
                else if (entry is FileInfo file && PathGuard.IsInside(root, entry.FullName)) yield return file;
            }
        }
    }

    public static bool Eligible(CleanupRule rule, FileInfo file, DateTime utcNow)
    {
        var cutoff = utcNow.AddDays(-rule.RetentionDays);
        return NameAllowed(rule, file.FullName) &&
            file.LastWriteTimeUtc < cutoff && file.CreationTimeUtc < cutoff &&
            (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) == 0;
    }

    public static CleanupResult Clean(CleanupRule rule, DateTime utcNow, CancellationToken cancellation = default)
    {
        if (rule.Kind != CleanupKind.OldFiles || rule.Root is null)
            throw new InvalidOperationException("Only approved old-file rules can use the file cleaner.");
        var result = new CleanupResult { Title = rule.Title };
        if (!Directory.Exists(rule.Root)) { result.Message = "Pasta não encontrada."; return result; }
        if (!PathGuard.IsOrdinaryPath(rule.Root, rule.Root))
        { result.Success = false; result.Message = "A pasta é um link ou não pode ser verificada."; return result; }
        foreach (var info in EnumerateInfo(rule.Root, () => result.Skipped++, cancellation))
        {
            try
            {
                var file = info.FullName;
                if (!Eligible(rule, info, utcNow)) continue;
                if (!PathGuard.IsOrdinaryPath(rule.Root, file)) { result.Skipped++; continue; }
                if (TryDeleteOldFile(rule.Root, file, utcNow.AddDays(-rule.RetentionDays), out var bytes))
                { result.RemovedBytes += bytes; result.RemovedFiles++; }
                else result.Skipped++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Skipped++; }
        }
        result.Message = $"{result.RemovedFiles} arquivos removidos; {result.Skipped} itens ignorados por proteção, acesso ou uso.";
        // No recursive directory deletion, ownership changes, unlocking or elevation.
        return result;
    }

    static bool TryDeleteOldFile(string root, string path, DateTime cutoff, out long bytes)
    {
        bytes = 0;
        const uint Delete = 0x00010000, ReadAttributes = 0x80, OpenExisting = 3, OpenReparsePoint = 0x00200000;
        using var handle = CreateFileW(path, Delete | ReadAttributes, 0, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return false;
        var finalName = new System.Text.StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, finalName, (uint)finalName.Capacity, 0);
        if (length == 0 || length >= finalName.Capacity) return false;
        var actual = finalName.ToString();
        if (actual.StartsWith(@"\\?\")) actual = actual[4..];
        if (!PathGuard.IsInside(root, actual)) return false;
        if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.ReadOnly)) != 0 ||
            info.NumberOfLinks != 1) return false;
        if (DateTime.FromFileTimeUtc(info.LastWrite.ToLong()) >= cutoff ||
            DateTime.FromFileTimeUtc(info.Created.ToLong()) >= cutoff) return false;
        var disposition = new Disposition { DeleteFile = true };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<Disposition>())) return false;
        bytes = (long)(((ulong)info.SizeHigh << 32) | info.SizeLow);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileTime
    {
        public uint Low, High;
        public readonly long ToLong() => ((long)High << 32) | Low;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint Attributes;
        public FileTime Created, LastAccess, LastWrite;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Disposition { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref Disposition information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, System.Text.StringBuilder path, uint length, uint flags);
}
