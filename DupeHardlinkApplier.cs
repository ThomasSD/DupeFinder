using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ew.Tools.DuplicateFileLinker;

/// <summary>
/// Applies duplicate consolidation by creating NTFS hardlinks.
/// </summary>
public static class DupeHardlinkApplier
{
    /// <summary>
    /// Applies hardlinking per duplicate set. Chooses/creates a master in or near <paramref name="masterDir"/>.
    /// Respects <paramref name="dryRun"/>.
    /// </summary>
    public static void Apply(IEnumerable<DupeSet> sets, string masterDir, bool dryRun)
    {
        string masterRoot = Path.GetFullPath(masterDir);
        if (!Directory.Exists(masterRoot))
        {
            CP.PrintError($"[ERR] MasterDir not found: {masterRoot}");
            return;
        }

        foreach (var set in sets.OrderByDescending(s => s.FileSize).ThenBy(s => s.DupeId))
        {
            CP.PrintSection($"Apply DupeId={set.DupeId} Size={Program.HumanSize(set.FileSize)} Hash={set.PrefixHash}");

            // Rule 1: Is there a file already inside MasterDir?
            var inMaster = set.Files
                .Where(f => IsUnder(f.Path, masterRoot))
                .OrderBy(f => f.CreationTimeUtc)
                .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (inMaster.Count > 0)
            {
                var master = inMaster.First().Path;
                CP.PrintInfo(dryRun
                    ? $"[R1/DRYRUN] Would use existing master in MasterDir: {master}"
                    : $"[R1] Using existing master in MasterDir: {master}");
                ReplaceOthersWithHardlinks(set.Files, master, dryRun);
                continue;
            }

            // Rule 2: If no file inside MasterDir, try placing it under an existing leaf directory there
            var leafGroups = set.Files.GroupBy(f => Path.GetFileName(f.ParentDirectory ?? string.Empty))
                                      .OrderByDescending(g => g.Count())
                                      .ToList();

            string? targetDir = null;
            foreach (var g in leafGroups)
            {
                var candidate = Path.Combine(masterRoot, g.Key ?? string.Empty);
                if (Directory.Exists(candidate))
                {
                    targetDir = candidate;
                    break;
                }
            }

            if (targetDir is not null)
            {
                var seed = set.Files.OrderBy(f => f.CreationTimeUtc).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First();
                var destPath = Path.Combine(targetDir, Path.GetFileName(seed.Path));

                if (File.Exists(destPath))
                {
                    CP.PrintWarn($"[R2] Destination exists; using it as master: {destPath}");
                    ReplaceOthersWithHardlinks(set.Files, destPath, dryRun, skipPath: destPath);
                    continue;
                }

                if (!SameRoot(seed.Path, destPath))
                {
                    CP.PrintError($"[ERR] Cross-volume move/link not supported: '{seed.Path}' -> '{destPath}'");
                    Rule3UseFirstAsMaster(set.Files, dryRun);
                    continue;
                }

                if (dryRun)
                {
                    CP.PrintInfo($"[R2/DRYRUN] Would move:");
                    CP.PrintInfo($"             {seed.Path}");
                    CP.PrintInfo($"             -> {destPath}");
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(targetDir);
                        File.Move(seed.Path, destPath);
                    }
                    catch (Exception ex)
                    {
                        CP.PrintError($"[ERR] Move failed: {ex.Message}");
                        Rule3UseFirstAsMaster(set.Files, dryRun);
                        continue;
                    }
                }

                ReplaceOthersWithHardlinks(set.Files, destPath, dryRun, skipPath: destPath);
                continue;
            }

            // Rule 3: Fallback – keep the first file as master
            Rule3UseFirstAsMaster(set.Files, dryRun);
        }
    }

    /// <summary>
    /// Fallback strategy: pick the earliest file as master and link others to it.
    /// </summary>
    private static void Rule3UseFirstAsMaster(IEnumerable<FileItem> files, bool dryRun)
    {
        var ordered = files.OrderBy(f => f.CreationTimeUtc).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var master = ordered.First().Path;
        CP.PrintInfo(dryRun ? $"[R3/DRYRUN] Would use first file as master: {master}"
                                        : $"[R3] Using first file as master: {master}");
        ReplaceOthersWithHardlinks(ordered, master, dryRun);
    }

    /// <summary>
    /// Replaces all files in <paramref name="files"/> (except the master) with hardlinks to <paramref name="masterPath"/>.
    /// Optional <paramref name="skipPath"/> will be treated as already processed.
    /// </summary>
    private static void ReplaceOthersWithHardlinks(IEnumerable<FileItem> files, string masterPath, bool dryRun, string? skipPath = null)
    {
        string masterFull = Path.GetFullPath(masterPath);
        if (!File.Exists(masterFull))
        {
            CP.PrintError($"[ERR] Master file missing: {masterFull}");
            return;
        }

        foreach (var f in files)
        {
            var path = Path.GetFullPath(f.Path);
            if (string.Equals(path, masterFull, StringComparison.OrdinalIgnoreCase)) continue;
            if (skipPath is not null && string.Equals(path, Path.GetFullPath(skipPath), StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var idA = GetNtfsId(masterFull);
                var idB = File.Exists(path) ? GetNtfsId(path) : default;
                if (!idB.Equals(default(NtfsId)) && idA.Equals(idB))
                {
                    CP.PrintInfo($"[SKIP] Already linked: {path}");
                    continue;
                }
            }
            catch (Exception ex)
            {
                CP.PrintWarn($"[WARN] Could not read file identity for '{path}': {ex.Message}");
            }

            if (!SameRoot(masterFull, path))
            {
                CP.PrintError($"[ERR] Different volumes: cannot hardlink '{path}' -> '{masterFull}'");
                continue;
            }

            try
            {
                ReplaceOneWithHardlink(masterFull, path, dryRun);
                CP.PrintInfo(dryRun
                    ? $"[DRYRUN] Would link: {path} -> {masterFull}"
                    : $"[OK] Linked: {path} -> {masterFull}");
            }
            catch (Exception ex)
            {
                CP.PrintError($"[ERR] Linking failed for '{path}' -> '{masterFull}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Replaces a single file at <paramref name="victimPath"/> with a hardlink to <paramref name="masterPath"/>.
    /// When not in dry-run, performs a safe rename-then-link with rollback.
    /// </summary>
    private static void ReplaceOneWithHardlink(string masterPath, string victimPath, bool dryRun)
    {
        var victimFull = Path.GetFullPath(victimPath);
        var masterFull = Path.GetFullPath(masterPath);

        if (!File.Exists(victimFull))
            throw new FileNotFoundException("Victim not found.", victimFull);

        var dir = Path.GetDirectoryName(victimFull)!;
        var name = Path.GetFileName(victimFull);
        string backup = Path.Combine(dir, "~" + name + ".dupe.bak");
        int n = 1;
        while (File.Exists(backup))
            backup = Path.Combine(dir, $"~{name}.dupe.bak.{n++}");

        // Show actions only in dry run
        if (dryRun)
        {
            CP.PrintInfo($"[DRYRUN] Would rename: {victimFull} -> {backup}");
            CP.PrintInfo($"[DRYRUN] Would create hardlink: {victimFull} -> {masterFull}");
            CP.PrintInfo($"[DRYRUN] Would delete backup: {backup}");
            return;
        }

        try
        {
            // Clear ReadOnly if present
            var attrs = File.GetAttributes(victimFull);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(victimFull, attrs & ~FileAttributes.ReadOnly);
        }
        catch (Exception ex)
        {
            CP.PrintWarn($"[WARN] Could not clear ReadOnly on '{victimFull}': {ex.Message}");
        }

        // 1) Rename original to backup
        File.Move(victimFull, backup);

        var linkCreated = false;
        try
        {
            // 2) Create hardlink at original path pointing to master
            EnsureParentExists(victimFull);
            CreateHardLinkOrThrow(victimFull, masterFull);
            linkCreated = true;

            // 3) Verify identity
            var idMaster = GetNtfsId(masterFull);
            var idNew = GetNtfsId(victimFull);
            if (!idMaster.Equals(idNew))
                throw new IOException("Post-check failed: file IDs differ after linking.");

            // 4) Delete backup
            File.Delete(backup);
        }
        catch
        {
            // Rollback
            try { if (linkCreated && File.Exists(victimFull)) File.Delete(victimFull); } catch { }
            try { if (File.Exists(backup)) File.Move(backup, victimFull); } catch { }
            throw;
        }
    }

    // ---- Helpers ----

    /// <summary>Returns true if both paths reside on the same root/volume.</summary>
    private static bool SameRoot(string a, string b)
    {
        var ra = Path.GetPathRoot(Path.GetFullPath(a));
        var rb = Path.GetPathRoot(Path.GetFullPath(b));
        return string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns true if <paramref name="path"/> is under or equal to <paramref name="parent"/>.</summary>
    private static bool IsUnder(string path, string parent)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\', '/');
        var par = Path.GetFullPath(parent).TrimEnd('\\', '/');
        return p.StartsWith(par + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p, par, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ensures the parent directory of <paramref name="path"/> exists.</summary>
    private static void EnsureParentExists(string path)
    {
        var d = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(d);
    }

    /// <summary>Gets the NTFS identity of the file at <paramref name="path"/>.</summary>
    private static NtfsId GetNtfsId(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(fs.SafeFileHandle, out var meta))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetFileInformationByHandle failed.");
        return new NtfsId(meta.dwVolumeSerialNumber, CombineFileIndex(meta));
    }

    /// <summary>Creates a hardlink or throws an exception with Win32 error info.</summary>
    private static void CreateHardLinkOrThrow(string newLinkPath, string existingFilePath)
    {
        if (!CreateHardLink(newLinkPath, existingFilePath, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"CreateHardLink failed ({err}). new='{newLinkPath}', existing='{existingFilePath}'");
        }
    }

    // ---- P/Invoke ----
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

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
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    private static ulong CombineFileIndex(BY_HANDLE_FILE_INFORMATION i)
        => ((ulong)i.nFileIndexHigh << 32) | i.nFileIndexLow;
}
