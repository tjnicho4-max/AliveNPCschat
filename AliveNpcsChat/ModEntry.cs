using System.Collections.Concurrent;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace AliveNpcsChat;

/// <summary>
/// The mod's entry point: SMAPI calls Entry() once when the game starts. Here we only
/// register "when X happens, call me" hooks and the console commands. The real logic
/// lives in VillagerChatService; the AliveNpcs prompt-block wiring lives in
/// PromptBlockIntegration; the portrait-box conversation playback in ConversationDisplay.
/// </summary>
public sealed class ModEntry : Mod
{
    private const string AliveNpcsId = "Lucas.AliveNpcs";

    /// <summary>
    /// Work handed back from background threads (AI replies). The game is not thread-safe,
    /// so anything that touches the game must run on the game thread; UpdateTicked is it.
    /// </summary>
    private readonly ConcurrentQueue<Action> _gameThreadWork = new();

    private IAliveNpcsApi? _api;
    private VillagerChatService? _chatService;

    public override void Entry(IModHelper helper)
    {
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += (_, _) =>
        {
            _chatService?.OnSaveLoaded();
            ConversationDisplay.Reset(); // never carry a queued conversation across saves
        };
        helper.Events.GameLoop.UpdateTicked += (_, _) =>
        {
            DrainQueue();
            ConversationDisplay.Tick(); // feed portrait boxes one at a time
        };

        helper.ConsoleCommands.Add(
            "chat_test",
            "Make specific villagers chat.\n\nUsage: chat_test <villager1> <villager2> [villager3] [villager4]",
            (_, args) =>
            {
                if (args.Length < 2)
                {
                    Monitor.Log(
                        "Usage: chat_test <villager1> <villager2> [villager3] [villager4]",
                        LogLevel.Info);
                    return;
                }
                _chatService?.RunChat(args, random: false);
            });

        helper.ConsoleCommands.Add(
            "chat_random",
            "Make two random villagers chat right now.",
            (_, _) => _chatService?.RunChat(Array.Empty<string>(), random: true));

        helper.ConsoleCommands.Add(
            "chat_list",
            "Shows which villagers can chat and how many memories are stored.",
            (_, _) => _chatService?.PrintStatus());

        helper.ConsoleCommands.Add(
            "chat_memories",
            "Prints recent chat memories.\n\nUsage: chat_memories [count]",
            (_, args) =>
            {
                int count = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 10;
                _chatService?.PrintMemories(count);
            });

        helper.ConsoleCommands.Add(
            "chat_pairs",
            "Prints the relationship arcs built between villagers from their chats.",
            (_, _) => _chatService?.PrintPairs());

        helper.ConsoleCommands.Add(
            "chat_partners",
            "Diagnoses how the mod reads the farmer's partners: the friendship table's "
            + "runtime type, the inner dictionary inside it, every non-Friendly status, "
            + "and the spouse field. Paste the output to your assistant if partners aren't "
            + "detected correctly.",
            (_, _) => _chatService?.DiagnosePartners());

        helper.ConsoleCommands.Add(
            "chat_romance",
            "Toggles whether villager relationships may develop romantically.\n\nUsage: chat_romance [on|off]",
            (_, args) => _chatService?.ToggleRomance(args));

        helper.ConsoleCommands.Add(
            "chat_display",
            "Sets how finished chats are shown.\n\nUsage: chat_display [conversation|text] — "
            + "conversation = one portrait box per line (default), text = one text box.",
            (_, args) => _chatService?.ToggleDisplay(args));

        helper.ConsoleCommands.Add(
            "chat_probe",
            "Writes chat-probe-report.txt with info about AliveNpcs' data files and the Personality "
            + "Editor's profiles, so they can be integrated. Share the report with your assistant.",
            (_, _) =>
            {
                if (_api == null)
                {
                    Monitor.Log("AliveNpcs isn't installed.", LogLevel.Warn);
                    return;
                }
                ProbeReport.Write(_api, Helper, Monitor);
            });

        helper.ConsoleCommands.Add(
            "chat_probe_api",
            "Writes chat-api-report.txt describing AliveNpcs.dll's API surface (types, methods, "
            + "properties), so integrations can be wired up exactly. Share it with your assistant.",
            (_, _) =>
            {
                if (_api == null)
                {
                    Monitor.Log("AliveNpcs isn't installed.", LogLevel.Warn);
                    return;
                }
                ApiSurfaceProbe.Write(_api, Helper, Monitor);
            });
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        // AliveNpcs builds its API while it loads, so it can only be fetched here —
        // not in Entry. The manifest makes AliveNpcs a required dependency (the prompt
        // block compiles directly against its DLL), so this shouldn't be null.
        _api = Helper.ModRegistry.GetApi<IAliveNpcsApi>(AliveNpcsId);
        if (_api == null)
        {
            Monitor.Log("AliveNpcs' core API is unavailable, so the chat addon is off.", LogLevel.Info);
            return;
        }

        _chatService = new VillagerChatService(
            _api,
            Monitor,
            Helper.Data,
            ModManifest.UniqueID,
            work => _gameThreadWork.Enqueue(work),
            new AliveNpcsDataStore(_api, Monitor),
            new EditorProfileStore(Helper));

        // The crossover: feed our chat memories into AliveNpcs' own dialogue prompts.
        PromptBlockIntegration.TryRegister(
            Helper.ModRegistry, ModManifest.UniqueID, _chatService, Monitor);

        Monitor.Log(
            "AliveNpcs Chat & Memories v0.8 loaded. In a save, try: chat_test Abigail Sebastian",
            LogLevel.Info);
    }

    /// <summary>Runs queued background-thread work, one batch per tick, on the game thread.</summary>
    private void DrainQueue()
    {
        while (_gameThreadWork.TryDequeue(out var work))
            work();
    }
}
