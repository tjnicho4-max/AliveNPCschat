using System.IO;
using System.Text.Json.Nodes;
using StardewModdingAPI;

namespace AliveNpcsChat;

/// <summary>
/// Loads AliveNpcs Personality Editor overrides (read-only): when the player customized a
/// villager, chats use that personality — plus the editor's lore and social tags — instead
/// of AliveNpcs' default text.
/// </summary>
internal sealed class EditorProfileStore
{
    private string? _overridesDir;

    public EditorProfileStore(IModHelper helper)
    {
        // The Editor is a sibling folder inside Mods; find it by its manifest, like chat_probe.
        if (Path.GetDirectoryName(helper.DirectoryPath) is not string modsDir)
            return;

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

                if (text.Contains("PersonalityEditor", StringComparison.OrdinalIgnoreCase))
                {
                    string overrides = Path.Combine(dir, "overrides");
                    if (Directory.Exists(overrides))
                        _overridesDir = overrides;
                    break;
                }
            }
        }
        catch
        {
            // Mods folder unreadable for some reason: no custom profiles, use defaults.
        }
    }

    /// <summary>The custom profile for a villager, or null when they weren't customized.</summary>
    public EditorProfile? Get(string npcName)
    {
        if (_overridesDir == null)
            return null;

        string path = Path.Combine(_overridesDir, npcName + ".json");
        if (!File.Exists(path))
            return null;

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            string personality = node?["canonicalPersonality"]?.GetValue<string>() ?? "";
            if (personality.Trim().Length == 0)
                return null;

            return new EditorProfile(
                Personality: personality,
                Lore: node?["lore"]?.GetValue<string>() ?? "",
                SocialTags: node?["socialTags"]?.GetValue<string>() ?? "");
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>A villager's custom profile from the AliveNpcs Personality Editor.</summary>
internal sealed record EditorProfile(string Personality, string Lore, string SocialTags);
