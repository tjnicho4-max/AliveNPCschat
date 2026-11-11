using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using StardewModdingAPI;
using StardewValley;

namespace AliveNpcsChat;

/// <summary>
/// Reads AliveNpcs' per-save data files (read-only) so chat prompts can be grounded in real
/// history: villager conversations with the farmer, gifts, village gossip, and the NPC-to-NPC
/// social web. Defensive by design: a missing or changed file just means less context,
/// never a crash.
/// </summary>
internal sealed class AliveNpcsDataStore
{
    private readonly IMonitor _monitor;
    private readonly string? _dataDir;

    public AliveNpcsDataStore(IAliveNpcsApi api, IMonitor monitor)
    {
        _monitor = monitor;
        if (api.GetModDirectoryPath() is string modDir)
            _dataDir = Path.Combine(modDir, "Data");
    }

    /*********
    ** Helpers
    *********/

    /// <summary>
    /// The Data sub-folders for the loaded save, e.g. "Alice_452779986". AliveNpcs names them
    /// farmerName_saveId, and a renamed farmer leaves old folders behind, so we match on the
    /// save-id suffix and merge them. Other saves (and the messenger-photos folder) are excluded.
    /// </summary>
    private List<string> SaveFolders()
    {
        var result = new List<string>();
        if (_dataDir == null || !Directory.Exists(_dataDir) || !Context.IsWorldReady)
            return result;

        string suffix = "_" + Game1.uniqueIDForThisGame;
        foreach (string dir in Directory.EnumerateDirectories(_dataDir))
        {
            if (Path.GetFileName(dir).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                result.Add(dir);
        }
        return result;
    }

    /// <summary>Parses a JSON file, or null when missing/unreadable/corrupt.</summary>
    private static JsonNode? ReadJson(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads a JSON value as a string, whatever type it was stored as.</summary>
    private static string Scalar(JsonNode? node)
    {
        if (node == null)
            return "";
        try { return node.GetValue<string>(); }
        catch { }
        try { return node.GetValue<int>().ToString(); }
        catch { }
        try { return node.GetValue<long>().ToString(); }
        catch { }
        try { return node.GetValue<double>().ToString(); }
        catch { }
        try { return node.GetValue<bool>().ToString(); }
        catch { }
        return node.ToJsonString() ?? "";
    }

    /// <summary>True when the value reads as a boolean 'true' (bool or "true" text).</summary>
    private static bool IsTrue(JsonNode? node)
        => string.Equals(Scalar(node), "true", StringComparison.OrdinalIgnoreCase);

    private static string Trim(string? s, int max = 260)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static string Cap(string? s)
    {
        s = (s ?? "").Trim();
        return s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
    }

    /// <summary>Formats a 0..1 raw value as a percentage string.</summary>
    private static string Pct(string? raw)
        => double.TryParse(raw, out double v) ? (int)Math.Round(v * 100) + "%" : "0%";

    /*********
    ** Public readers
    *********/

    /// <summary>The villager's most recent real conversations with the farmer (oldest first).</summary>
    public List<string> RecentTalksWithFarmer(string npcName, int max)
    {
        var newestFirst = new List<string>();
        try
        {
            foreach (string folder in SaveFolders())
            {
                string path = Path.Combine(folder, "conversations", npcName + ".json");
                if (ReadJson(path)?["ConversationHistory"] is not JsonArray history)
                    continue;

                foreach (var entry in history.OfType<JsonObject>().Reverse())
                {
                    string msg = Scalar(entry["NpcMessage"]);
                    string resp = Scalar(entry["PlayerResponse"]);
                    string react = Scalar(entry["NpcReaction"]);
                    if (msg.Length == 0 && resp.Length == 0)
                        continue;

                    string season = Cap(Scalar(entry["Season"]));
                    string day = Scalar(entry["Day"]);
                    string year = Scalar(entry["Year"]);
                    string ctx = Scalar(entry["ContextName"]).Length > 0
                        ? Scalar(entry["ContextName"])
                        : Scalar(entry["ContextType"]);
                    string emotion = Scalar(entry["Emotion"]);

                    var sb = new StringBuilder();
                    sb.Append($"[{season} {day}, Year {year}{(ctx.Length > 0 ? ", " + ctx : "")}]");
                    if (msg.Length > 0)
                        sb.Append($" {npcName}: \"{Trim(msg)}\"");
                    if (resp.Length > 0)
                        sb.Append($" The farmer: \"{Trim(resp)}\"");
                    if (react.Length > 0)
                        sb.Append($" {npcName}: \"{Trim(react)}\"");
                    if (emotion.Length > 0)
                        sb.Append($" ({emotion})");

                    newestFirst.Add(sb.ToString());
                    if (newestFirst.Count >= max)
                        break;
                }
                if (newestFirst.Count >= max)
                    break;
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"Couldn't read conversations for {npcName}: {ex.Message}", LogLevel.Debug);
        }

        newestFirst.Reverse();
        return newestFirst;
    }

    /// <summary>Recent gifts the farmer gave this villager (newest first).</summary>
    public List<string> RecentGifts(string npcName, int max)
    {
        var result = new List<string>();
        try
        {
            foreach (string folder in SaveFolders())
            {
                string path = Path.Combine(folder, "gift_history.json");
                if (ReadJson(path)?["data"]?[npcName] is not JsonArray gifts)
                    continue;

                foreach (var gift in gifts.OfType<JsonObject>().Reverse())
                {
                    result.Add(
                        $"{Scalar(gift["item_name"])} ({Scalar(gift["taste"])}), "
                        + $"{Cap(Scalar(gift["season"]))} {Scalar(gift["day"])} Year {Scalar(gift["year"])}"
                        + (IsTrue(gift["is_birthday"]) ? " — their birthday" : ""));
                    if (result.Count >= max)
                        break;
                }
                if (result.Count >= max)
                    break;
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"Couldn't read gift history for {npcName}: {ex.Message}", LogLevel.Debug);
        }
        return result;
    }

    /// <summary>Village gossip that every one of the named villagers knows about.</summary>
    public List<string> GossipKnownBy(List<string> npcs, int max)
    {
        var result = new List<string>();
        try
        {
            foreach (string folder in SaveFolders())
            {
                string path = Path.Combine(folder, "village_gossip.json");
                if (ReadJson(path) is not JsonArray entries)
                    continue;

                foreach (var entry in entries.OfType<JsonObject>())
                {
                    if (entry["aware_npcs"] is not JsonArray aware)
                        continue;

                    var names = aware.Select(Scalar).Where(s => s.Length > 0).ToHashSet();
                    if (!npcs.All(n => names.Contains(n)))
                        continue;

                    result.Add(Trim(Scalar(entry["gossip_text"]), 400));
                    if (result.Count >= max)
                        break;
                }
                if (result.Count >= max)
                    break;
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"Couldn't read village gossip: {ex.Message}", LogLevel.Debug);
        }
        return result;
    }

    /// <summary>How two villagers feel about each other, with shared memories. Null if unknown.</summary>
    public string? RelationshipBetween(string a, string b)
    {
        try
        {
            foreach (string folder in SaveFolders())
            {
                string path = Path.Combine(folder, "npc_social_web.json");
                if (ReadJson(path)?["data"]?["Edges"] is not JsonArray edges)
                    continue;

                foreach (var edge in edges.OfType<JsonObject>())
                {
                    string na = Scalar(edge["NpcA"]);
                    string nb = Scalar(edge["NpcB"]);
                    if (!((na == a && nb == b) || (na == b && nb == a)))
                        continue;

                    var sb = new StringBuilder();
                    sb.Append($"{Scalar(edge["State"])} "
                              + $"(familiarity {Pct(Scalar(edge["Familiarity"]))}, "
                              + $"affection {Pct(Scalar(edge["Affinity"]))}, "
                              + $"trust {Pct(Scalar(edge["Trust"]))}, "
                              + $"tension {Pct(Scalar(edge["Tension"]))})");

                    if (edge["Tags"] is JsonArray tags)
                    {
                        var list = tags.Select(Scalar).Where(s => s.Length > 0).ToList();
                        if (list.Count > 0)
                            sb.Append("; they share: " + string.Join(", ", list));
                    }

                    if (edge["Memories"] is JsonArray memories)
                    {
                        var summaries = memories.OfType<JsonObject>()
                            .Select(m => Scalar(m["Summary"]))
                            .Where(s => s.Length > 0)
                            .TakeLast(2)
                            .ToList();
                        if (summaries.Count > 0)
                            sb.Append("\nShared memories between them:\n"
                                       + string.Join("\n", summaries.Select(s => "- " + Trim(s, 300))));
                    }

                    return sb.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"Couldn't read the social web: {ex.Message}", LogLevel.Debug);
        }
        return null;
    }
}
