namespace AliveNpcsChat;

/// <summary>
/// One remembered conversation between villagers. Instances are stored inside the
/// Stardew save file by SMAPI's IDataHelper (see VillagerChatService), so memories are
/// per-save and survive reloading the game.
/// </summary>
public sealed class MemoryEntry
{
    /// <summary>When it happened, e.g. "Summer 15, Year 2".</summary>
    public string Date { get; set; } = "";

    /// <summary>Where it happened, e.g. "The Stardrop Saloon".</summary>
    public string Where { get; set; } = "";

    /// <summary>The villagers who talked, by internal NPC name.</summary>
    public string[] Who { get; set; } = System.Array.Empty<string>();

    /// <summary>The full conversation, one 'Name: line' per speaker.</summary>
    public string Transcript { get; set; } = "";

    /// <summary>A one-sentence recap, used in prompts and gossip.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Short keywords from the conversation. Empty for chats saved before v0.5.1.</summary>
    public string[] Topics { get; set; } = System.Array.Empty<string>();

    /// <summary>
    /// The model's one-sentence read of where the pair's relationship stood after this
    /// chat. Empty for chats saved before v0.6.
    /// </summary>
    public string Relation { get; set; } = "";
}

/// <summary>
/// The evolving relationship between two villagers, built up chat by chat — independent
/// of the farmer. Keyed by "A|B" (sorted names); stored per-save alongside the memories.
/// </summary>
public sealed class PairRelationship
{
    /// <summary>The two villagers (sorted internal names).</summary>
    public string[] Npcs { get; set; } = System.Array.Empty<string>();

    /// <summary>Latest one-sentence read of the relationship (from the newest chat).</summary>
    public string Tone { get; set; } = "";

    /// <summary>Accumulated relationship beats, oldest first, capped (date + what changed).</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>When they last talked, e.g. "Summer 15, Year 2".</summary>
    public string LastDate { get; set; } = "";
}
