using System.Globalization;

namespace Ew.Tools.DuplicateFileLinker;

/// <summary>
/// Entry point for Duplicate File Linker.
/// </summary>
internal static class Program
{
    private const int DEFAULT_MIN_MB = 100;

    /// <summary>
    /// Main entry method.
    /// </summary>
    /// <param name="args">
    /// Args: &lt;ScanDir&gt; -MasterDir:&lt;Path&gt; [-MinMb:&lt;int&gt;] [-dryrun] [-report]
    /// </param>
    /// <returns>0 on success; non-zero on error.</returns>
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2 || args.Contains("-h") || args.Contains("--help"))
            {
                PrintHelp();
                return 1;
            }

            string scanDir = Path.GetFullPath(args[0]);
            if (!Directory.Exists(scanDir))
            {
                CP.PrintError($"[ERR] ScanDir not found: {scanDir}");
                return 2;
            }

            string? masterDir = null;
            int minMb = DEFAULT_MIN_MB;
            bool dryRun = false;
            bool report = false;
            List<string>? extensionPatterns = null;

            // Flags in format -Key[:Value]
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (!a.StartsWith("-", StringComparison.Ordinal))
                {
                    CP.PrintError($"[ERR] Invalid argument: {a}");
                    return 2;
                }

                // Split into key + optional value
                var parts = a.Substring(1).Split(':', 2);
                var key = parts[0];
                var val = parts.Length > 1 ? parts[1] : null;

                switch (key.ToLowerInvariant())
                {
                    case "masterdir":
                        if (string.IsNullOrWhiteSpace(val))
                        {
                            CP.PrintError("[ERR] -MasterDir requires a path.");
                            return 2;
                        }
                        masterDir = Path.GetFullPath(val);
                        break;
                    case "minmb":
                        if (string.IsNullOrWhiteSpace(val) ||
                            !int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out minMb) ||
                            minMb < 1)
                        {
                            CP.PrintError($"[ERR] -MinMb must be integer >= 1 (got '{val}')");
                            return 2;
                        }
                        break;
                    case "ext":
                        if (string.IsNullOrWhiteSpace(val))
                        {
                            CP.PrintError("[ERR] -Ext requires a pattern (e.g. *.safesensors or *.jpg;*.png).");
                            return 2;
                        }
                        extensionPatterns = [.. val.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                        break;
                    case "dryrun":
                        dryRun = true;
                        break;
                    case "report":
                        report = true;
                        break;
                    default:
                        CP.PrintError($"[ERR] Unknown switch: -{key}");
                        return 2;
                }
            }

            // Ensure MasterDir is on the same volume as ScanDir for NTFS hardlinks
            if (masterDir != null && Path.GetPathRoot(scanDir) != Path.GetPathRoot(masterDir))
            {
                CP.PrintError("[ERR] ScanDir and MasterDir must be on the same volume.");
                return 2;
            }

            if (string.IsNullOrWhiteSpace(masterDir))
            {
                CP.PrintError("[ERR] -MasterDir:<path> is required.");
                return 2;
            }
            if (!Directory.Exists(masterDir))
            {
                CP.PrintError($"[ERR] MasterDir not found: {masterDir}");
                return 2;
            }

            long minBytes = (long)minMb * 1024L * 1024L;

            CP.PrintHeader("Duplicate File Linker V0.51");
            CP.PrintInfo($"Scan:       {scanDir}");
            CP.PrintInfo($"MasterDir:  {masterDir}");
            CP.PrintInfo($"Min Size:   {HumanSize(minBytes)} ({minBytes:N0} bytes)");
            CP.PrintInfo($"DryRun:     {(dryRun ? "Yes" : "No")}");
            CP.PrintInfo($"Report:     {(report ? "Yes" : "No")}");

            var finder = new DupeFinder();
            var sets = finder.Find(scanDir, minBytes, masterDir, extensionPatterns);

            if (report)
                PrintReport(sets);

            // Apply: respects DryRun
            if (!report)
                DupeHardlinkApplier.Apply(sets, masterDir, dryRun);

            return 0;
        }
        catch (Exception ex)
        {
            CP.PrintError("[ERR] " + ex.Message);
            return 2;
        }
    }

    /// <summary>
    /// Prints the CLI help/usage.
    /// </summary>
    public static void PrintHelp()
    {
        CP.PrintHeader("Duplicate File Linker V0.51");
        CP.PrintInfo("Find duplicate files and consolidate them using NTFS hardlinks.");
        CP.PrintInfo("");
        CP.PrintInfo("How it works:");
        CP.PrintInfo("  - Scans files in the given directory larger than a minimum size.");
        CP.PrintInfo("  - Groups files with identical content as duplicates.");
        CP.PrintInfo("  - Keeps one master file per group.");
        CP.PrintInfo("  - Replaces other duplicates with hardlinks to the master.");
        CP.PrintInfo("");
        CP.PrintInfo("  Names, paths and timestamps remain; only one physical copy is stored.");
        CP.PrintInfo("  Windows/NTFS only. Hardlinks cannot span volumes.");
        CP.PrintInfo("");
        CP.PrintSeperator();
        CP.PrintInfo("Usage:");
        CP.PrintInfo(@"  duplink <ScanDir> -MasterDir:<Path> [-MinMb:<int>] [-dryrun] [-report]");
        CP.PrintInfo("");
        CP.PrintInfo("Options:");
        CP.PrintInfo("  -MasterDir:<Path>  Preferred directory for master files.");
        CP.PrintInfo("  -MinMb:<int>       Minimum file size (default: 100).");
        CP.PrintInfo("  -ext:<patterns>    Limit by extensions (wildcards, ';'-separated).");
        CP.PrintInfo("                     Example: -ext:*.jpg;*.png;*.safesensors");
        CP.PrintInfo("  -dryrun            Show actions without changing files.");
        CP.PrintInfo("  -report            Print a detailed duplicate report only.");
        CP.PrintInfo("");
        CP.PrintInfo("Example:");
        CP.PrintInfo(@"  duplink c:\ai\comfyui -masterdir:c:\ai\models -minmb:200 -dryrun");
        CP.PrintInfo("");
        CP.PrintInfo("  Scans C:\\AI\\comfyui for files >= 200 MB and shows which files would");
        CP.PrintInfo("  be hardlinked and/or moved into c:\\ai\\models.");
        CP.PrintInfo("");
    }

    /// <summary>
    /// Formats a byte size in human-readable units (B, KB, MB, GB, TB).
    /// </summary>
    public static string HumanSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.##} {units[u]}";
    }

    /// <summary>
    /// Prints a human-friendly report of duplicate sets.
    /// </summary>
    public static void PrintReport(IReadOnlyList<DupeSet> sets)
    {
        if (sets.Count == 0)
        {
            CP.PrintSection("Result");
            CP.PrintInfo("No duplicates found.");
            return;
        }

        CP.PrintSection($"Result – {sets.Count:N0} duplicate groups");

        foreach (var ds in sets)
        {
            CP.PrintInfo($"DupeId:     {ds.DupeId}");
            CP.PrintInfo($"File Size:  {HumanSize(ds.FileSize)} ({ds.FileSize:N0} bytes)");
            CP.PrintInfo($"1MiB Hash:  {ds.PrefixHash}");
            CP.PrintInfo($"Master:     {ds.MasterFile ?? "(null)"}");
            CP.PrintInfo($"Files ({ds.Files.Count}):");

            foreach (var f in ds.Files)
            {
                CP.PrintInfo($"  {f.Path}");
                CP.PrintInfo($"      name:      {f.Name}");
                CP.PrintInfo($"      parent:    {f.ParentDirectory}");
                CP.PrintInfo($"      created:   {f.CreationTimeUtc:yyyy-MM-dd HH:mm:ss}Z");
                CP.PrintInfo($"      modified:  {f.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss}Z");
                CP.PrintInfo($"      ntfsId:    {f.NtfsId}");
                if (f.IsLink)
                    CP.PrintInfo($"      linkOf:    {f.LinkNtfsId}");
            }
            CP.PrintSeperator();
        }
    }
}
