namespace AliveNpcsChat;

/// <summary>
/// Per-install settings, saved as config.json in the mod folder. Created with defaults
/// on first load; hand-edit while the game is closed, or use the console commands
/// (chat_romance, chat_display).
/// </summary>
public sealed class ModConfig
{
    /// <summary>
    /// Whether villager relationships may develop toward romance in chats (gradual,
    /// reciprocated, only when true to both characters). Default true.
    /// </summary>
    public bool AllowRomance { get; set; } = true;

    /// <summary>
    /// How finished chats are shown: true = conversation mode (one portrait dialogue box
    /// per line, clicked through); false = the old single text box. Default true.
    /// Toggle in game with: chat_display conversation | text
    /// </summary>
    public bool ConversationMode { get; set; } = true;
}
