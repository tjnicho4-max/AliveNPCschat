using AliveNpcs.Api;
using StardewModdingAPI;

namespace AliveNpcsChat;

/// <summary>
/// Wires our chat memories into AliveNpcs' own dialogue prompts through a dynamic prompt
/// block: every time AliveNpcs builds a dialogue prompt for any villager, it calls our
/// provider, and we hand it a digest of the villagers' recent chats (with their evolving
/// pair bonds) from this mod. That's what makes a villager bring up what they discussed
/// with another villager when the farmer talks to them.
///
/// Notes on the mechanism (from the AliveNpcs API surface, v1.7.4):
///  - The provider receives an ExperimentalRuntimeHookContext with date/time/player info,
///    but NOT the NPC being prompted — so the digest lists chats by name and lets
///    AliveNpcs' model pick what's relevant for whoever it's writing.
///  - The provider may run on a background thread: it only touches locked state and a
///    precomputed name map, never live game state. A provider that throws three times in
///    a row is disabled for the session, so ours returns null on any glitch instead.
///  - Deliberately dialogue-only: opening greetings are NOT injected (per design choice).
///  - Registering these types needs a direct compile-time reference to AliveNpcs.dll
///    (they can't be bridged through SMAPI), which makes AliveNpcs required.
/// </summary>
internal static class PromptBlockIntegration
{
    public static void TryRegister(
        IModRegistry registry,
        string ownerModId,
        VillagerChatService chats,
        IMonitor monitor)
    {
        IAliveNpcsExperimentalContentApi? ec = null;
        try
        {
            // With the direct DLL reference this returns AliveNpcs' real API object —
            // no proxy bridge, so the AliveNpcs-typed registration classes work.
            ec = registry.GetApi<IAliveNpcsExperimentalContentApi>("Lucas.AliveNpcs");
        }
        catch (Exception ex)
        {
            monitor.Log($"Couldn't get AliveNpcs' experimental API: {ex.Message}", LogLevel.Warn);
        }

        if (ec == null)
        {
            monitor.Log(
                "AliveNpcs' experimental API is unavailable; chat memories won't appear in "
                + "villager dialogue (everything else still works).",
                LogLevel.Info);
            return;
        }

        try
        {
            var caps = ec.GetCapabilities();
            if (!caps.DynamicPromptBlocks)
            {
                monitor.Log("This AliveNpcs build doesn't support dynamic prompt blocks.", LogLevel.Warn);
                return;
            }
        }
        catch (Exception ex)
        {
            monitor.Log($"Couldn't read AliveNpcs capabilities: {ex.Message}", LogLevel.Warn);
        }

        // Dialogue prompts only: the back-and-forth conversation turns.
        try
        {
            _ = ec.RegisterDynamicPromptBlock(new DynamicPromptBlockRegistration
            {
                OwnerModId = ownerModId,
                BlockId = "villager-chat-memories",
                ModeId = "",          // empty: runs in every mode, even when none is active
                Target = "dialogue",
                MaxChars = 1800,
                Provider = _ => chats.BuildVillageChatDigest(maxChars: 1800, maxChats: 6)
            });

            monitor.Log(
                "Chat-memories dialogue block registered: villagers can now bring up their "
                + "chats (and pair bonds) from this addon in their own dialogue.",
                LogLevel.Info);
        }
        catch (Exception ex)
        {
            monitor.Log($"Couldn't register the prompt block: {ex.Message}", LogLevel.Error);
        }
    }
}
