using System.Text;
using System.Text.RegularExpressions;
using StardewModdingAPI;
using StardewValley;

namespace AliveNpcsChat;

/// <summary>
/// A one-off reconnaissance tool. Writes chat-probe-report.txt into this mod's folder,
/// describing where AliveNpcs keeps its per-save data (memories of talks with the farmer)
/// and where the Personality Editor keeps its custom NPC profiles — with small samples of
/// each file, so the formats can be learned and integrated. Nothing is modified: read-only.
/// </summary>
internal static class ProbeReport
{
    private static readonly Regex InterestingFile = new(
        "memor|conversation|dialog|chat|gossip|histor|relationship",
        RegexOptions.IgnoreCase);

    public static void Write(IAliveNpcsApi api, IModHelper helper, IMonitor monitor)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AliveNpcs Chat addon — probe report");
        sb.AppendLine($"Game version: {Game1.version}");
        sb.AppendLine($"This mod's folder: {helper.DirectoryPath}");
        sb.AppendLine();

        // Part 1: the AliveNpcs folder and its Data folder.
        string? aliveDir = api.GetModDirectoryPath();
        sb.AppendLine($"== AliveNpcs mod folder: {aliveDir ?? "(null)"}");
        if (aliveDir != null && Directory.Exists(aliveDir))
        {
            sb.AppendLine("-- top level:");
            ListTree(sb, new DirectoryInfo(aliveDir), depth: 0, indent: "  ");

            string dataDir = Path.Combine(aliveDir, "Data");
            if (Directory.Exists(dataDir))
            {
                sb.AppendLine("-- Data folder tree:");
                ListTree(sb, new DirectoryInfo(dataDir), depth: 3, indent: "  ");
                sb.AppendLine("-- samples from interesting-looking files under Data:");
                SampleFiles(sb, dataDir);
            }
            else
            {
                sb.AppendLine("(no Data folder — maybe no save has been played with AliveNpcs yet)");
            }
        }
        sb.AppendLine();

        // Part 2: the Personality Editor (find it by its manifest, in the Mods folder).
        sb.AppendLine("== Personality Editor lookup");
        string modsDir = Path.GetDirectoryName(helper.DirectoryPath)!;
        bool found = false;
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(modsDir))
            {
                string manifest = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifest))
                    continue;

                string text;
                try { text = File.ReadAllText(manifest); }
                catch { continue; }

                if (!text.Contains("PersonalityEditor", StringComparison.OrdinalIgnoreCase))
                    continue;

                found = true;
                sb.AppendLine($"Found: {dir}");
                ListTree(sb, new DirectoryInfo(dir), depth: 2, indent: "  ");

                foreach (string sub in new[] { "overrides", "presets" })
                {
                    string p = Path.Combine(dir, sub);
                    if (Directory.Exists(p))
                    {
                        sb.AppendLine($"-- samples from {sub}:");
                        SampleFiles(sb, p);
                    }
                }

                string cfg = Path.Combine(dir, "config.json");
                if (File.Exists(cfg))
                {
                    sb.AppendLine("-- config.json:");
                    sb.AppendLine(ReadHead(cfg, 1200));
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(error scanning the Mods folder: {ex.Message})");
        }
        if (!found)
            sb.AppendLine("Not found in the Mods folder. Is the Personality Editor installed?");

        string outPath = Path.Combine(helper.DirectoryPath, "chat-probe-report.txt");
        try
        {
            File.WriteAllText(outPath, sb.ToString());
            monitor.Log(
                $"Probe report written to:\n{outPath}\n"
                + "Open it and paste its contents to your assistant (redact anything personal first). "
                + "It's read-only reconnaissance: nothing in AliveNpcs or the Editor was changed.",
                LogLevel.Info);
        }
        catch (Exception ex)
        {
            monitor.Log($"Couldn't write the probe report: {ex}", LogLevel.Error);
        }
    }

    /// <summary>Lists a folder's contents as a small tree. depth 0 = this folder only.</summary>
    private static void ListTree(StringBuilder sb, DirectoryInfo dir, int depth, string indent)
    {
        const int maxEntries = 30;

        DirectoryInfo[] dirs;
        FileInfo[] files;
        try { dirs = dir.GetDirectories(); files = dir.GetFiles(); }
        catch
        {
            sb.AppendLine(indent + "(unreadable)");
            return;
        }

        foreach (DirectoryInfo d in dirs.Take(maxEntries))
        {
            sb.AppendLine($"{indent}[{d.Name}]");
            if (depth > 0)
                ListTree(sb, d, depth - 1, indent + "  ");
        }
        if (dirs.Length > maxEntries)
            sb.AppendLine($"{indent}... {dirs.Length - maxEntries} more folders");

        foreach (FileInfo f in files.Take(maxEntries))
            sb.AppendLine($"{indent}{f.Name}  ({f.Length} bytes)");
        if (files.Length > maxEntries)
            sb.AppendLine($"{indent}... {files.Length - maxEntries} more files");
    }

    /// <summary>Appends the first ~1500 characters of a few interesting files.</summary>
    private static void SampleFiles(StringBuilder sb, string root)
    {
        List<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => InterestingFile.IsMatch(Path.GetFileName(f)))
                .Take(3)
                .ToList();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (couldn't scan: {ex.Message})");
            return;
        }

        if (candidates.Count == 0)
            sb.AppendLine("  (no files with memory/conversation/dialogue-like names found)");

        foreach (string f in candidates)
        {
            sb.AppendLine($"  -- sample from {f}:");
            sb.AppendLine(ReadHead(f, 1500));
            sb.AppendLine();
        }
    }

    private static string ReadHead(string path, int maxChars)
    {
        try
        {
            string text = File.ReadAllText(path);
            return text.Length <= maxChars ? text : text[..maxChars] + "\n... (cut)";
        }
        catch (Exception ex)
        {
            return $"(couldn't read: {ex.Message})";
        }
    }
}
