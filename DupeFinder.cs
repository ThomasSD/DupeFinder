using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Ew.Tools.DuplicateFileLinker;

// ===== Models =====

/// <summary>
/// Identifies a file on NTFS by volume serial and file index.
/// </summary>
public readonly struct NtfsId : IEquatable<NtfsId>
{
    public readonly uint VolumeSerialNumber;
    public readonly ulong FileIndex; // High<<32 | Low

    /// <summary>Creates a new NTFS identity value.</summary>
    public NtfsId(uint vol, ulong idx) { VolumeSerialNumber = vol; FileIndex = idx; }

    /// <inheritdoc/>
    public bool Equals(NtfsId other)
        => VolumeSerialNumber == other.VolumeSerialNumber && FileIndex == other.FileIndex;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is NtfsId o && Equals(o);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(VolumeSerialNumber, FileIndex);

    /// <inheritdoc/>
    public override string ToString() => $"{VolumeSerialNumber:X8}:{FileIndex:X16}";
}

/// <summary>
/// File metadata captured during scanning.
/// </summary>
public class FileItem
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string ParentDirectory { get; init; } = "";
    public long Size { get; init; }
    public DateTime CreationTimeUtc { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }

    public NtfsId NtfsId { get; init; }       // own NTFS identity
    public NtfsId LinkNtfsId { get; init; }   // identity of source; default == self (not a link)

    public bool IsLink => !LinkNtfsId.Equals(default(NtfsId));
}

/// <summary>
/// A group of duplicate files with same size and prefix hash.
/// </summary>
public class DupeSet
{
    public int DupeId { get; init; }               // sequential id
    public long FileSize { get; init; }            // bytes
    public string PrefixHash { get; init; } = "";  // 1 MiB SHA-256 (HEX)
    public string? MasterFile { get; init; }       // preferred path
    public List<FileItem> Files { get; } = new();  // items in this dupe group
}

// ===== Finder =====

/// <summary>
/// Scans a directory, groups duplicates, and chooses candidate masters.
/// </summary>
public sealed class DupeFinder
{
    private const int PREFIX_BYTES = 1 * 1024 * 1024; // 1 MiB
    private static readonly StringComparer PathCmp = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Finds duplicate sets under <paramref name="rootDir"/> for files >= <paramref name="minBytes"/>.
    /// Master selection prefers <paramref name="masterDirectory"/>.
    /// </summary>
    /// <returns>Duplicate sets ordered by size desc, then id.</returns>
    public List<DupeSet> Find(string rootDir, long minBytes, string masterDirectory, List<string>? extensionPatterns = null)
    {
        string root = Path.GetFullPath(rootDir);
        string masterDir = Path.GetFullPath(masterDirectory);

        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);

        // 1) Collect files incl. hardlink identity
        var (allFiles, sourcePathByNtfsId) = EnumerateFilesWithLinks(root, minBytes, extensionPatterns);

        // 2) Consider sizes with >= 2 items
        var bySize = allFiles
            .GroupBy(f => f.Size)
            .Where(g => g.Count() >= 2)
            .ToDictionary(g => g.Key, g => g.ToList());

        CP.PrintInfo($"Found {allFiles.Count:N0} files; {bySize.Count:N0} sizes with >= 2 files.");

        // 3) Compute prefix hash
        var bySizeAndHash = new Dictionary<(long size, string hash), List<FileItem>>();
        CP.PrintInfo("Computing 1 MiB hash for equal-sized files...");

        foreach (var kv in bySize)
        {
            foreach (var f in kv.Value)
            {
                string hash;
                try { hash = HashFirstBytes(f.Path, PREFIX_BYTES); }
                catch (Exception ex)
                {
                    WriteError($"[ERR] Could not hash {f.Path}: {ex.Message}");
                    continue;
                }

                var key = (kv.Key, hash);
                if (!bySizeAndHash.TryGetValue(key, out var bucket))
                    bySizeAndHash[key] = bucket = new List<FileItem>();
                bucket.Add(f);
            }
        }

        // 4) Build dupe sets
        var result = new List<DupeSet>();
        int nextId = 1;

        foreach (var kv in bySizeAndHash)
        {
            var items = kv.Value;
            if (items.Count < 2) continue;

            // Choose master
            string? master = items
                .Where(f => IsUnder(f.Path, masterDir))
                .OrderBy(f => f.CreationTimeUtc)
                .ThenBy(f => f.Path, PathCmp)
                .Select(f => f.Path)
                .FirstOrDefault();

            if (master is null)
            {
                foreach (var f in items)
                {
                    if (f.IsLink && sourcePathByNtfsId.TryGetValue(f.LinkNtfsId, out var srcPath))
                    {
                        master = srcPath;
                        break;
                    }
                }
            }

            var ds = new DupeSet
            {
                DupeId = nextId++,
                FileSize = kv.Key.size,
                PrefixHash = kv.Key.hash,
                MasterFile = master
            };
            ds.Files.AddRange(items.OrderBy(f => f.CreationTimeUtc).ThenBy(f => f.Path, PathCmp));
            result.Add(ds);
        }

        // Potentially saved space if duplicates were consolidated
        long totalSavedSpace = 0;
        foreach (var ds in result)
        {
            int distinctCount = ds.Files.Select(f => f.NtfsId).Distinct().Count();
            totalSavedSpace += ds.FileSize * (distinctCount - 1);
        }

        CP.PrintInfo(
            $"Potential space saved: {Program.HumanSize(totalSavedSpace)} ({totalSavedSpace:N0} bytes).");

        return result.OrderByDescending(d => d.FileSize).ThenBy(d => d.DupeId).ToList();
    }

    // ==== File enumeration ====

    /// <summary>
    /// Enumerates files >= <paramref name="minBytes"/> under <paramref name="rootDir"/>,
    /// resolving NTFS identity and existing hardlink relations.
    /// </summary>
    private static (List<FileItem> files, Dictionary<NtfsId, string> sourcePathByNtfsId)
        EnumerateFilesWithLinks(string rootDir, long minBytes, List<string>? extensionPatterns)
    {
        var files = new List<FileItem>();
        var sourcePathByNtfsId = new Dictionary<NtfsId, string>();

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };

        // Collect all matching paths (union over all patterns)
        IEnumerable<string> allPaths;

        if (extensionPatterns is null || extensionPatterns.Count == 0)
        {
            CP.PrintSection("File Scan");
            CP.PrintInfo($"Scanning {rootDir} (all files) ...");
            allPaths = Directory.EnumerateFiles(rootDir, "*", opts);
        }
        else
        {
            CP.PrintSection("File Scan");
            CP.PrintInfo($"Scanning {rootDir} with patterns: {string.Join("; ", extensionPatterns)} ...");

            var bag = new List<string>(capacity: 1024);
            foreach (var pat in extensionPatterns)
            {
                var pattern = string.IsNullOrWhiteSpace(pat) ? "*" : pat;
                try
                {
                    bag.AddRange(Directory.EnumerateFiles(rootDir, pattern, opts));
                }
                catch (Exception ex)
                {
                    CP.PrintWarn($"[WARN] Pattern '{pattern}' failed: {ex.Message}");
                }
            }
            allPaths = bag.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        var pathList = allPaths.ToList();
        long total = pathList.Count;
        long scanned = 0, kept = 0, skippedLinks = 0, tooSmall = 0, errors = 0;

        CP.PrintInfo($"Starting scan in {rootDir} – {total:N0} files queued.");

        foreach (var p in pathList)
        {
            scanned++;
            if (scanned % 1000 == 0 || scanned == total)
            {
                CP.PrintProgress(
                    $"[SCAN] {scanned:N0}/{total:N0} | kept {kept:N0} | links {skippedLinks:N0} | small {tooSmall:N0} | errors {errors:N0}"
                );
            }

            FileInfo info;
            try { info = new FileInfo(p); }
            catch (Exception ex)
            {
                CP.PrintWarn($"Skipping {p}: {ex.Message}");
                errors++;
                continue;
            }

            // Skip reparse points and Windows .lnk shortcuts
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                skippedLinks++;
                continue;
            }

            if (info.Length < minBytes)
            {
                tooSmall++;
                continue;
            }

            NtfsId id;
            uint links;
            try
            {
                using var fs = new FileStream(info.FullName, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
                if (!GetFileInformationByHandle(fs.SafeFileHandle, out var meta))
                {
                    CP.PrintWarn($"GetFileInformationByHandle failed for {info.FullName}");
                    errors++;
                    continue;
                }
                id = new NtfsId(meta.dwVolumeSerialNumber, CombineFileIndex(meta));
                links = meta.nNumberOfLinks;
            }
            catch (Exception ex)
            {
                CP.PrintWarn($"Skipping {info.FullName}: {ex.Message}");
                errors++;
                continue;
            }

            NtfsId linkId = default;
            if (links > 1)
            {
                if (!sourcePathByNtfsId.ContainsKey(id))
                    sourcePathByNtfsId[id] = info.FullName; // first seen path = source
                else
                    linkId = id; // another link to an existing source
            }

            files.Add(new FileItem
            {
                Path = info.FullName,
                Name = info.Name,
                ParentDirectory = info.DirectoryName ?? "",
                Size = info.Length,
                CreationTimeUtc = info.CreationTimeUtc,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                NtfsId = id,
                LinkNtfsId = linkId
            });

            kept++;
        }

        CP.PrintInfo("");
        CP.PrintInfo($"Done. {kept:N0} files >= {Program.HumanSize(minBytes)}.");
        CP.PrintInfo($"Skipped: links={skippedLinks:N0}, small={tooSmall:N0}, errors={errors:N0}");

        return (files, sourcePathByNtfsId);
    }

    // ==== 1 MiB hash ====

    /// <summary>
    /// Computes a SHA-256 over the first <paramref name="prefixBytes"/> bytes of <paramref name="path"/>.
    /// </summary>
    private static string HashFirstBytes(string path, int prefixBytes)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                      bufferSize: 64 * 1024, options: FileOptions.SequentialScan);
        int toRead = (int)Math.Min(prefixBytes, fs.Length);
        if (toRead <= 0)
            return Convert.ToHexString(sha.ComputeHash(Array.Empty<byte>()));

        var buffer = new byte[toRead];
        int total = 0;
        while (total < toRead)
        {
            int read = fs.Read(buffer, total, toRead - total);
            if (read <= 0) break;
            total += read;
        }
        return Convert.ToHexString(sha.ComputeHash(buffer, 0, total));
    }

    // ==== Helpers ====

    /// <summary>Returns true if <paramref name="path"/> is under or equal to <paramref name="parent"/>.</summary>
    private static bool IsUnder(string path, string parent)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\', '/');
        var par = Path.GetFullPath(parent).TrimEnd('\\', '/');
        return p.StartsWith(par + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p, par, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes an error line in red directly to <see cref="Console.Error"/>.</summary>
    private static void WriteError(string message)
    {
        var old = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(message);
        }
        finally
        {
            Console.ForegroundColor = old;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    private static ulong CombineFileIndex(BY_HANDLE_FILE_INFORMATION i)
        => ((ulong)i.nFileIndexHigh << 32) | i.nFileIndexLow;
}
