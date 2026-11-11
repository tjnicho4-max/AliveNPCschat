#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AliveNpcsChat;

/// <summary>
/// A copy of the AliveNpcs API interface (v1.7.0). SMAPI maps this local copy onto the real
/// API at runtime. Rules: keep method names and signatures exactly as they are, delete
/// methods you don't use, and never add methods that use AliveNpcs' own classes.
/// Source: https://github.com/LucasGatica/alivenpcs-api (MIT license)
/// </summary>
public interface IAliveNpcsApi
{
    /// <summary>The villagers AliveNpcs runs AI features for (respects the player's settings).</summary>
    IEnumerable<string> GetAvailableNpcNames();

    /// <summary>The default personality text AliveNpcs writes into its prompts for a villager.</summary>
    string GetDefaultPersonality(string npcName);

    /// <summary>
    /// The farmer's character sheet as [whoAmI, whyMovedHere, extraInfo, atAGlanceDetails],
    /// or null when no save is loaded. Empty fields are empty strings.
    /// </summary>
    string[]? GetCharacterSheet();

    /// <summary>
    /// Asks the AI provider the player set up in AliveNpcs. Pass null (or "freeform") to send
    /// your own systemPrompt + userPrompt. Returns null if AI is unavailable or the call failed.
    /// The code after await may run on a background thread: hand results back to the game thread.
    /// </summary>
    Task<string?> GenerateOutputAsync(
        string? templateName,
        string? systemPrompt,
        string? userPrompt,
        string? npcName = null,
        string? customContext = null);

    /// <summary>
    /// Adds a note to tonight's end-of-day gossip analysis. Villagers can pick it up as gossip
    /// and it can appear in the farmer's diary. Returns false if the player turned third-party
    /// gossip off or the daily limit was reached — don't retry.
    /// </summary>
    bool InjectGossip(string ownerModId, string text);

    /// <summary>The full path of the AliveNpcs mod folder. Treat its files as read-only.</summary>
    string? GetModDirectoryPath();
}
