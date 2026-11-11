using System.Text;
using StardewModdingAPI;
using StardewValley;

namespace AliveNpcsChat;

/// <summary>
/// The core logic, in plain terms:
///  1. When a save loads: fetch the villagers AliveNpcs handles, load the memory book, the
///     pair-relationship arcs and the mod config.
///  2. When the player asks (console commands only — nothing runs automatically): pick the
///     villagers, then build a VILLAGER-CENTRIC prompt — their personalities (preferring the
///     Personality Editor's custom versions), their relationship arc with each other, how they
///     feel about each other (social web), village gossip, and a SMALL slice of farmer context
///     so a natural farmer mention is grounded but not dominating. Romance between villagers
///     is allowed (configurable) and develops through the pair arcs.
///  3. When the reply arrives: show it in-game — by default as a portrait-box conversation
///     (ConversationDisplay), or as one text box — store it (with summary, topics and a
///     RELATION line) in the per-save memory book, update each pair's arc, and tell
///     AliveNpcs' gossip.
///  4. AliveNpcs itself asks us for a digest of recent chats whenever it builds a dialogue
///     prompt (see PromptBlockIntegration), so villagers can bring the chats — and their pair
///     bonds — up when the farmer talks to them.
/// </summary>
internal sealed class VillagerChatService
{
    private const string MemoryKey = "tjnicho4.aliveNpcsChat.memories";
    private const string PairKey = "tjnicho4.aliveNpcsChat.pairs";

    private readonly IAliveNpcsApi _api;
    private readonly IMonitor _monitor;
    private readonly IDataHelper _data;
    private readonly string _modId;
    private readonly Action<Action> _runOnGameThread;
    private readonly AliveNpcsDataStore _aliveData;
    private readonly EditorProfileStore _editor;
    private readonly Random _random = new();

    /// <summary>Per-install settings (config.json in the mod folder).</summary>
    private ModConfig _config = new();

    /// <summary>Every past chat, newest last. Saved into the save file by SMAPI.</summary>
    private List<MemoryEntry> _memories = new();

    /// <summary>
    /// The villagers' evolving pair relationships: "A|B" (sorted) → arc. Saved into the
    /// save file. This is what lets bonds (friendly or romantic) grow on their own.
    /// </summary>
    private Dictionary<string, PairRelationship> _pairs = new();

    /// <summary>
    /// Guards _memories and _pairs: AliveNpcs calls our prompt-block provider from its own
    /// thread while the game thread appends a new chat. Cheap lock at our scale.
    /// </summary>
    private readonly object _memoryLock = new();

    /// <summary>
    /// Internal NPC name → display name, built on the game thread whenever the villager
    /// list refreshes. The prompt-block provider reads this from AliveNpcs' (possibly
    /// background) thread, so it's swapped atomically and never mutated in place.
    /// </summary>
    private Dictionary<string, string> _displayNames = new();

    private List<string> _villagers = new();
    private bool _busy;

    public VillagerChatService(
        IAliveNpcsApi api,
        IMonitor monitor,
        IDataHelper data,
        string modId,
        Action<Action> runOnGameThread,
        AliveNpcsDataStore aliveData,
        EditorProfileStore editor)
    {
        _api = api;
        _monitor = monitor;
        _data = data;
        _modId = modId;
        _runOnGameThread = runOnGameThread;
        _aliveData = aliveData;
        _editor = editor;
    }

    /*********
    ** Lifecycle (called by ModEntry)
    *********/

    public void OnSaveLoaded()
    {
        _memories = _data.ReadSaveData<List<MemoryEntry>>(MemoryKey) ?? new();
        _pairs = _data.ReadSaveData<Dictionary<string, PairRelationship>>(PairKey) ?? new();

        // Load config.json (created with defaults on first run).
        try
        {
            var cfg = _data.ReadJsonFile<ModConfig>("config.json");
            if (cfg == null)
                _data.WriteJsonFile("config.json", _config = new ModConfig());
            else
                _config = cfg;
        }
        catch (Exception ex)
        {
            _monitor.Log($"Couldn't read config.json, using defaults: {ex.Message}", LogLevel.Warn);
        }

        RefreshVillagers();
        _busy = false;

        _monitor.Log(
            $"Chat addon ready: {_villagers.Count} villagers available, {_memories.Count} memories, "
            + $"{_pairs.Count} pair relationships. "
            + $"Romantic development {(_config.AllowRomance ? "enabled" : "disabled")}, "
            + $"display {(_config.ConversationMode ? "conversation" : "text")}. "
            + "Try: chat_test Abigail Sebastian",
            LogLevel.Info);
    }

    /// <summary>
    /// Re-fetches the villager list from AliveNpcs. Called when a save loads and again before
    /// each chat, because AliveNpcs may not be ready at the exact moment the save loads.
    /// Also rebuilds the display-name map (game-thread only: it reads NPC objects).
    /// </summary>
    private void RefreshVillagers()
    {
        if (!Context.IsWorldReady)
            return;

        try
        {
            var list = _api.GetAvailableNpcNames().ToList();
            if (list.Count > 0)
            {
                _villagers = list;

                // Build the whole map first, then assign: readers on other threads see
                // either the old complete map or the new complete map, never a half-built one.
                var names = new Dictionary<string, string>();
                foreach (string v in list.Distinct())
                    names[v] = Game1.getCharacterFromName(v)?.displayName ?? v;
                _displayNames = names;
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"Couldn't fetch the villager list from AliveNpcs: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>Toggles (or sets) whether villager relationships may turn romantic.</summary>
    public void ToggleRomance(string[] args)
    {
        if (args.Length == 0)
        {
            _config.AllowRomance = !_config.AllowRomance;
        }
        else if (args[0].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            _config.AllowRomance = true;
        }
        else if (args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            _config.AllowRomance = false;
        }
        else
        {
            _monitor.Log("Usage: chat_romance [on|off]", LogLevel.Info);
            return;
        }

        try
        {
            _data.WriteJsonFile("config.json", _config);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Couldn't save config.json: {ex.Message}", LogLevel.Warn);
        }

        _monitor.Log(
            $"Romantic development between villagers: {(_config.AllowRomance ? "ENABLED" : "DISABLED")}.",
            LogLevel.Info);
    }

    /// <summary>Toggles (or sets) how finished chats are shown: portrait boxes or one text box.</summary>
    public void ToggleDisplay(string[] args)
    {
        if (args.Length == 0)
        {
            _config.ConversationMode = !_config.ConversationMode;
        }
        else if (args[0].Equals("conversation", StringComparison.OrdinalIgnoreCase))
        {
            _config.ConversationMode = true;
        }
        else if (args[0].Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            _config.ConversationMode = false;
        }
        else
        {
            _monitor.Log("Usage: chat_display [conversation|text]", LogLevel.Info);
            return;
        }

        try
        {
            _data.WriteJsonFile("config.json", _config);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Couldn't save config.json: {ex.Message}", LogLevel.Warn);
        }

        // The ternary is hoisted into a local: a multi-line expression inside an
        // interpolated string's {...} hole needs C# 11 (CS8967 on net6.0's default C# 10).
        string mode = _config.ConversationMode
            ? "CONVERSATION (one portrait box per line, clicked through)"
            : "TEXT (one text box per chat)";
        _monitor.Log($"Chat display: {mode}.", LogLevel.Info);
    }

    /*********
    ** The farmer's partners
    *********/

    /// <summary>Whether a friendship status counts as a partner for our purposes.</summary>
    private static bool IsPartnerStatus(FriendshipStatus status)
        => status is FriendshipStatus.Married or FriendshipStatus.Dating or FriendshipStatus.Engaged;

    /// <summary>Collects every partner-status entry from a dictionary view into the list.</summary>
    private static void CollectPartners(IDictionary<string, Friendship> dict, List<string> into)
    {
        foreach (KeyValuePair<string, Friendship> pair in dict)
        {
            if (IsPartnerStatus(pair.Value.Status) && !into.Contains(pair.Key))
                into.Add(pair.Key);
        }
    }

    /// <summary>
    /// The farmer's current partners (married/dating/engaged).
    ///
    /// How the data actually reads on Stardew 1.6 (confirmed by chat_partners diagnostics):
    /// Game1.player.friendshipData is a Network.NetStringDictionary — a NETCODE WRAPPER that
    /// implements neither IDictionary view, which is why every cast-based read silently
    /// found nothing. But enumerating it yields exactly one item: its inner
    /// SerializableDictionary[string, Friendship], which IS a real dictionary. So:
    ///   Tier 1: a direct IDictionary cast (in case a future game build changes the wrapper).
    ///   Tier 2: enumerate the wrapper → grab the inner dictionary → read its statuses.
    ///   Always: add the vanilla Game1.player.spouse field (authoritative for at least the
    ///   primary spouse; a polyamory mod may keep the others in the table only).
    /// Every tier logs which path ran, so nothing fails silently.
    /// </summary>
    private List<string> FarmerPartners()
    {
        var partners = new List<string>();
        string strategy = "none";

        try
        {
            var data = Game1.player.friendshipData;

            // Tier 1: direct generic-dictionary cast.
            if (data is IDictionary<string, Friendship> dict)
            {
                strategy = "direct-dictionary";
                CollectPartners(dict, partners);
            }

            // Tier 2: the 1.6 netcode wrapper. Enumerate it, then read the dictionary it yields.
            if (partners.Count == 0 && data is System.Collections.IEnumerable outer)
            {
                foreach (object item in outer)
                {
                    if (item is IDictionary<string, Friendship> inner)
                    {
                        if (strategy == "none")
                            strategy = "netcode-inner-dictionary";
                        CollectPartners(inner, partners);
                    }
                    else if (item is System.Collections.IEnumerable innerEnumerable)
                    {
                        // Belt and suspenders: an item that is itself enumerable of pairs.
                        foreach (object pairObj in innerEnumerable)
                        {
                            if (pairObj is KeyValuePair<string, Friendship> kvp
                                && IsPartnerStatus(kvp.Value.Status)
                                && !partners.Contains(kvp.Key))
                            {
                                strategy = "netcode-inner-enumerable";
                                partners.Add(kvp.Key);
                            }
                            else if (pairObj is System.Collections.DictionaryEntry de
                                && de.Key is string dk
                                && de.Value is Friendship df
                                && IsPartnerStatus(df.Status)
                                && !partners.Contains(dk))
                            {
                                strategy = "netcode-inner-enumerable";
                                partners.Add(dk);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"FarmerPartners: reading the friendship table threw: {ex.Message}", LogLevel.Warn);
        }

        // Always: the vanilla spouse field. Authoritative for at least the primary spouse,
        // whatever the table read found (or didn't).
        try
        {
            string? spouse = Game1.player.spouse;
            if (!string.IsNullOrWhiteSpace(spouse) && !partners.Contains(spouse))
            {
                strategy += "+spouse-field";
                partners.Add(spouse);
            }
        }
        catch
        {
            // No spouse field readable: the table read is all we have.
        }

        _monitor.Log(
            $"FarmerPartners: strategy '{strategy}', {partners.Count} partner(s)"
            + (partners.Count > 0 ? $": {string.Join(", ", partners)}" : "") + ".",
            LogLevel.Info);

        // Map to display names at the very end.
        return partners
            .Select(n => Game1.getCharacterFromName(n)?.displayName ?? n)
            .ToList();
    }

    /// <summary>
    /// Console diagnostics (chat_partners): ground truth about the farmer's partners on this
    /// install — the wrapper type, the inner dictionary it yields, every non-Friendly status
    /// inside it, and the spouse field. Paste the output to your assistant if anything looks off.
    /// </summary>
    public void DiagnosePartners()
    {
        try
        {
            var data = Game1.player.friendshipData;

            _monitor.Log("== chat_partners diagnostics ==", LogLevel.Info);
            _monitor.Log($"friendshipData runtime type: {data.GetType().FullName}", LogLevel.Info);
            _monitor.Log(
                $"Implements IDictionary<string,Friendship>: {data is IDictionary<string, Friendship>}",
                LogLevel.Info);

            // Enumerate the wrapper and drill into whatever it yields.
            if (data is System.Collections.IEnumerable outer)
            {
                int outerCount = 0;
                foreach (object item in outer)
                {
                    outerCount++;
                    _monitor.Log($"Wrapper item #{outerCount}: {item?.GetType().FullName}", LogLevel.Info);

                    if (item is IDictionary<string, Friendship> inner)
                    {
                        _monitor.Log($"  Inner dictionary: {inner.Count} entries total.", LogLevel.Info);
                        int notable = 0;
                        foreach (KeyValuePair<string, Friendship> pair in inner)
                        {
                            if (pair.Value.Status != FriendshipStatus.Friendly)
                            {
                                notable++;
                                _monitor.Log(
                                    $"    {pair.Key}: {pair.Value.Status} ({pair.Value.Points} points)",
                                    LogLevel.Info);
                            }
                        }
                        if (notable == 0)
                            _monitor.Log(
                                "    (no Dating/Engaged/Married/Divorced entries inside)",
                                LogLevel.Info);
                    }
                    else if (item is System.Collections.IEnumerable innerEnumerable)
                    {
                        int innerCount = 0;
                        foreach (object pairObj in innerEnumerable)
                        {
                            innerCount++;
                            if (innerCount <= 5)
                                _monitor.Log(
                                    $"    inner item: {pairObj?.GetType().Name}: {pairObj}",
                                    LogLevel.Info);
                        }
                        _monitor.Log($"    (inner enumerable: {innerCount} items)", LogLevel.Info);
                    }
                }
                if (outerCount == 0)
                    _monitor.Log("Wrapper enumeration: no items.", LogLevel.Info);
            }

            // The spouse field, plainly.
            try
            {
                string spouseName = Game1.player.spouse ?? "";
                _monitor.Log($"Game1.player.spouse: '{spouseName}'.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _monitor.Log($"Game1.player.spouse: unreadable ({ex.Message}).", LogLevel.Info);
            }

            // What the chat prompt would actually use.
            var detected = FarmerPartners();
            _monitor.Log(
                $"FarmerPartners() result: {detected.Count} partner(s)"
                + (detected.Count > 0 ? $": {string.Join(", ", detected)}" : "") + ".",
                LogLevel.Info);

            _monitor.Log("== end diagnostics ==", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"chat_partners diagnostics failed: {ex}", LogLevel.Error);
        }
    }

    /*********
    ** Memory access (thread-safe: AliveNpcs' prompt callback may run off the game thread)
    *********/

    /// <summary>
    /// A digest of the most recent chats for AliveNpcs' dialogue prompts: a short header plus
    /// one line per chat, NEWEST FIRST (language models attend most to the beginnings of
    /// context), each line carrying the summary, topic keywords and the pair's bond status,
    /// capped to maxChars. Returns null when there's nothing worth injecting. Called by the
    /// prompt-block provider — must be fast, side-effect free, and never throw.
    /// </summary>
    public string? BuildVillageChatDigest(int maxChars, int maxChats)
    {
        try
        {
            if (!Context.IsWorldReady)
                return null;

            List<MemoryEntry> recent;
            lock (_memoryLock)
                recent = _memories.TakeLast(maxChats).Reverse().ToList(); // newest first

            if (recent.Count == 0)
                return null;

            var display = _displayNames; // atomic snapshot; safe to read from any thread

            var sb = new StringBuilder();
            sb.Append("Recent conversations between villagers (real events; a villager listed "
                      + "here may naturally mention one when talking with the farmer):");

            foreach (MemoryEntry m in recent)
            {
                string who = string.Join(" & ",
                    m.Who.Select(n => display.TryGetValue(n, out var d) ? d : n));

                var line = $"\n- {m.Date} at {m.Where}: {who} — {m.Summary}";

                if (m.Topics is { Length: > 0 })
                    line += $" (topics: {string.Join(", ", m.Topics)})";

                if (!string.IsNullOrWhiteSpace(m.Relation))
                {
                    string bond = m.Relation.Length <= 160 ? m.Relation : m.Relation[..160] + "…";
                    line += $" (bond: {bond})";
                }

                if (sb.Length + line.Length > maxChars)
                    break;
                sb.Append(line);
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch
        {
            return null; // never let a provider exception disable the block over a glitch
        }
    }

    /// <summary>The pair-relationship storage key: "A|B" with names sorted.</summary>
    private static string PairId(string a, string b)
        => string.Join("|", new[] { a, b }.OrderBy(n => n, StringComparer.Ordinal));

    /*********
    ** Running a chat
    *********/

    /// <summary>Runs one chat. With random=false, requested holds 2-4 villager names the player typed.</summary>
    public void RunChat(string[] requested, bool random)
    {
        if (_busy)
        {
            _monitor.Log("A chat is already being written; give it a moment.", LogLevel.Info);
            return;
        }

        if (!Context.IsWorldReady)
        {
            _monitor.Log("Load a save first, then run the chat command.", LogLevel.Warn);
            return;
        }

        // Re-fetch in case AliveNpcs wasn't ready when the save loaded.
        RefreshVillagers();

        if (_villagers.Count < 2)
        {
            _monitor.Log(
                $"Only {_villagers.Count} villagers are available for chats (need at least 2). "
                + "AliveNpcs may have none of its AI features enabled, or its compatibility "
                + "list is empty. Check AliveNpcs' own settings (F9 hub) and try chat_list.",
                LogLevel.Warn);
            return;
        }

        List<string> names;
        if (random)
        {
            var a = _villagers[_random.Next(_villagers.Count)];
            string b;
            do
            {
                b = _villagers[_random.Next(_villagers.Count)];
            } while (b == a);
            names = new List<string> { a, b };
        }
        else
        {
            names = new List<string>();
            foreach (var input in requested)
            {
                string? resolved = ResolveName(input);
                if (resolved == null)
                {
                    _monitor.Log(
                        $"I don't know a villager named '{input}'. Run chat_list to see who's available.",
                        LogLevel.Warn);
                    return;
                }
                if (!names.Contains(resolved))
                    names.Add(resolved);
            }

            if (names.Count < 2)
            {
                _monitor.Log("Pick at least two different villagers.", LogLevel.Warn);
                return;
            }
        }

        // Build the prompt NOW, on the game thread, because it reads game state and files.
        var prompt = BuildPrompt(names);
        _busy = true;
        _monitor.Log(
            $"Asking the AI for a chat between {string.Join(", ", names.Select(DisplayName))}... "
            + "(up to 30 seconds)",
            LogLevel.Info);

        _ = ChatAsync(names, prompt);
    }

    /// <summary>Matches what the player typed against internal names, then display names.</summary>
    private string? ResolveName(string input)
    {
        if (_villagers.Contains(input))
            return input;

        var ci = _villagers.FirstOrDefault(v => string.Equals(v, input, StringComparison.OrdinalIgnoreCase));
        if (ci != null)
            return ci;

        foreach (var v in _villagers)
        {
            var display = Game1.getCharacterFromName(v)?.displayName;
            if (display != null && string.Equals(display, input, StringComparison.OrdinalIgnoreCase))
                return v;
        }

        return null;
    }

    private static string DisplayName(string npcName)
        => Game1.getCharacterFromName(npcName)?.displayName ?? npcName;

    /// <summary>Where an NPC currently is, e.g. "The Stardrop Saloon", with a fallback.</summary>
    private static string WhereIs(string npcName)
    {
        var location = Game1.getCharacterFromName(npcName)?.currentLocation;
        return location?.DisplayName ?? location?.Name ?? "somewhere in the valley";
    }

    /*********
    ** Grounding: real history from the save
    *********/

    /// <summary>The player's custom profile if there is one, else AliveNpcs' default.</summary>
    private string DescribePersonality(string npc)
    {
        var profile = _editor.Get(npc);
        if (profile != null)
        {
            string text = $"{DisplayName(npc)}'s personality (the player's custom version): "
                          + profile.Personality;
            if (profile.Lore.Trim().Length > 0)
                text += $"\nLore: {profile.Lore}";
            if (profile.SocialTags.Trim().Length > 0)
                text += $"\nSocial: {profile.SocialTags}";
            return text;
        }

        return $"{DisplayName(npc)}'s personality: {_api.GetDefaultPersonality(npc)}";
    }

    /// <summary>
    /// The pair's relationship arc so far, from earlier chats between them — the engine of
    /// relationships that grow between villagers independently of the farmer.
    /// </summary>
    private string PairArcsSection(List<string> npcs)
    {
        var blocks = new List<string>();
        for (int i = 0; i < npcs.Count; i++)
        {
            for (int j = i + 1; j < npcs.Count; j++)
            {
                PairRelationship? pr;
                lock (_memoryLock)
                    _pairs.TryGetValue(PairId(npcs[i], npcs[j]), out pr);

                if (pr == null || pr.Notes.Count == 0)
                    continue;

                var beats = pr.Notes.TakeLast(4).Select(n => $"    - {n}");
                blocks.Add(
                    $"  {DisplayName(npcs[i])} & {DisplayName(npcs[j])} — now: {pr.Tone}\n"
                    + string.Join("\n", beats));
            }
        }

        return blocks.Count > 0
            ? "Their relationship so far, from earlier chats between them (real events):\n"
              + string.Join("\n", blocks)
            : "";
    }

    /// <summary>How the participating villagers feel about each other, from the social web.</summary>
    private string RelationshipsSection(List<string> npcs)
    {
        var lines = new List<string>();
        for (int i = 0; i < npcs.Count && lines.Count < 3; i++)
        {
            for (int j = i + 1; j < npcs.Count && lines.Count < 3; j++)
            {
                string? rel = _aliveData.RelationshipBetween(npcs[i], npcs[j]);
                if (rel != null)
                    lines.Add($"- {DisplayName(npcs[i])} & {DisplayName(npcs[j])}: {rel}");
            }
        }

        return lines.Count > 0
            ? "How they currently feel about each other:\n" + string.Join("\n", lines)
            : "";
    }

    /// <summary>Village gossip every participant already knows about.</summary>
    private string GossipSection(List<string> npcs)
    {
        var gossip = _aliveData.GossipKnownBy(npcs, 2);
        return gossip.Count > 0
            ? "Village gossip they already know (treat as true, past events):\n"
              + string.Join("\n", gossip.Select(g => $"- {g}"))
            : "";
    }

    /// <summary>
    /// A SMALL slice of farmer context (2 talks + 2 gifts each): enough that a natural
    /// farmer mention is grounded in what actually happened, not enough to dominate.
    /// </summary>
    private string FarmerSideSection(List<string> npcs)
    {
        var lines = new List<string>();
        foreach (string npc in npcs)
        {
            var talks = _aliveData.RecentTalksWithFarmer(npc, 2);
            var gifts = _aliveData.RecentGifts(npc, 2);
            if (talks.Count == 0 && gifts.Count == 0)
                continue;

            lines.Add($"  {DisplayName(npc)} and the farmer:");
            lines.AddRange(talks.Select(t => $"    - {t}"));
            if (gifts.Count > 0)
                lines.Add($"    - Recent gifts from the farmer: {string.Join("; ", gifts)}");
        }

        return lines.Count > 0
            ? "Recent moments with the farmer (true events — background, in case the farmer "
              + "comes up):\n" + string.Join("\n", lines)
            : "";
    }

    /*********
    ** Prompt building and the AI call
    *********/

    private (string System, string User) BuildPrompt(List<string> npcs)
    {
        string date = $"{Game1.CurrentSeasonDisplayName} {Game1.dayOfMonth}, Year {Game1.year}";
        string weather = Game1.isRaining ? "rainy" : Game1.isSnowing ? "snowy" : "sunny";
        string where = WhereIs(npcs[0]);

        string personalities = string.Join("\n\n", npcs.Select(DescribePersonality));

        // One short farmer line: they exist, this is their standing — nothing more. The
        // partner detection logs its own strategy line (FarmerPartners).
        var farmer = Game1.player;
        var hearts = npcs.Select(v =>
        {
            int points = farmer.friendshipData.TryGetValue(v, out var friendship) ? friendship.Points : 0;
            return $"{DisplayName(v)} {Math.Min(points / 250, 14)} hearts";
        }).ToList();

        var partners = FarmerPartners();
        string farmerContext =
            $"The farmer {farmer.Name} (friendship: {string.Join(", ", hearts)}) lives in the valley ";
        if (partners.Count > 0)
            farmerContext +=
                $"and is partnered with {string.Join(" & ", partners)}; "
                + "this valley allows the farmer more than one partner, and bonds between "
                + "the farmer's partners matter here.";
        else
            farmerContext += ".";

        // Shared history from this mod's own chats: the last 5 that involved at least two of today's participants.
        List<MemoryEntry> shared;
        lock (_memoryLock)
        {
            shared = _memories
                .Where(m => m.Who.Count(npcs.Contains) >= 2)
                .TakeLast(5)
                .ToList();
        }

        string history = shared.Count > 0
            ? "These villagers have talked before (in earlier chats this mod ran). Recent ones:\n"
              + string.Join("\n", shared.Select(m =>
                  $"- {m.Date} at {m.Where} ({string.Join(", ", m.Who)}): {m.Summary}"))
            : "These villagers have not talked with each other before (that the player has seen).";

        string pairArcs = PairArcsSection(npcs);
        string relationships = RelationshipsSection(npcs);
        string gossip = GossipSection(npcs);
        string farmerSide = FarmerSideSection(npcs);
        string names = string.Join(", ", npcs.Select(DisplayName));

        string romanceRule = _config.AllowRomance
            ? "- The player has explicitly enabled romance between villagers: relationships "
              + "may grow toward romance when that is true to both characters — gradual, "
              + "reciprocated, never forced or rushed. If it isn't true to them, they stay "
              + "whatever they genuinely are. Bonds here can also be polyamorous.\n"
            : "- Keep the relationships between villagers platonic: warm friendship, "
              + "rivalry, mentorship — but no romance.\n";

        string systemPrompt =
            "You write short conversations between Stardew Valley villagers, staying true "
            + "to each character's personality.\n\n"
            + personalities + "\n\n"
            + "The context lists real events. Treat every named event as something that truly "
            + "happened; don't contradict it or invent replacements.\n"
            + "Focus rules:\n"
            + "- Write what these villagers would genuinely talk about today: their own "
            + "lives, work, hobbies, opinions, plans, each other, whatever's happening "
            + "in the valley — whatever fits them.\n"
            + "- The farmer can come up when it's natural — they both know them — but doesn't "
            + "have to; keep the farmer a side note unless something specific pulls them in.\n"
            + romanceRule
            + "Format rules:\n"
            + "- 6 to 10 lines total, alternating speakers, each line exactly like 'Name: what they say'.\n"
            + "- Cozy and grounded in valley life. No emoji, no stage directions.\n"
            + "- End with 'SUMMARY:' followed by one short third-person sentence recapping "
            + "what they talked about.\n"
            + "- Then 'TOPICS:' followed by 3 to 6 short keywords or names from the "
            + "conversation, comma-separated.\n"
            + "- Then 'RELATION:' followed by one short sentence on where the relationship "
            + "BETWEEN THE VILLAGERS stands after this talk — how it changed or deepened "
            + "between them (including a romantic shift, if one genuinely happened and "
            + "romance is enabled). Never about the farmer unless their bond truly "
            + "concerns the farmer.";

        string userPrompt =
            $"Write today's conversation between {names}, centered on them.\n"
            + $"Date: {date}. Weather: {weather}. Place: {where}.\n"
            + farmerContext + "\n"
            + (pairArcs.Length > 0 ? pairArcs + "\n" : "")
            + (relationships.Length > 0 ? relationships + "\n" : "")
            + (gossip.Length > 0 ? gossip + "\n" : "")
            + (farmerSide.Length > 0 ? farmerSide + "\n" : "")
            + history + "\n"
            + (shared.Count > 0 ? "They may naturally build on those earlier talks.\n" : "")
            + $"Begin the conversation with {DisplayName(npcs[0])}.";

        return (systemPrompt, userPrompt);
    }

    private async Task ChatAsync(List<string> npcs, (string System, string User) prompt)
    {
        string? answer = null;
        try
        {
            answer = await _api.GenerateOutputAsync(null, prompt.System, prompt.User);
        }
        catch (Exception ex)
        {
            _monitor.Log($"The AI request failed: {ex.Message}", LogLevel.Error);
        }

        // From here we may be on a background thread; everything touching the game goes
        // through _runOnGameThread.
        _runOnGameThread(() =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(answer))
                    RememberChat(npcs, answer!.Trim());
                else
                    _monitor.Log(
                        "No reply from the AI. Is the provider set up in AliveNpcs' settings?",
                        LogLevel.Warn);
            }
            finally
            {
                _busy = false;
            }
        });
    }

    /// <summary>Store the chat, update the pair arcs, show it, tell the village. Game thread only.</summary>
    private void RememberChat(List<string> npcs, string answer)
    {
        // Split the transcript from the SUMMARY:, TOPICS: and RELATION: lines (found from
        // the end, so the model can put them in any order without breaking the parse).
        string[] lines = answer.Split('\n');
        int summaryIdx = -1, topicsIdx = -1, relationIdx = -1;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string t = lines[i].Trim();
            if (relationIdx < 0 && t.StartsWith("RELATION:", StringComparison.OrdinalIgnoreCase))
                relationIdx = i;
            if (topicsIdx < 0 && t.StartsWith("TOPICS:", StringComparison.OrdinalIgnoreCase))
                topicsIdx = i;
            if (summaryIdx < 0 && t.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase))
                summaryIdx = i;
        }

        int cut = new[] { summaryIdx, topicsIdx, relationIdx }
            .Where(i => i >= 0)
            .DefaultIfEmpty(-1)
            .Min();

        string transcript = cut >= 0
            ? string.Join("\n", lines.Take(cut)).Trim()
            : answer.Trim();
        string summary = summaryIdx >= 0
            ? lines[summaryIdx].Trim()["SUMMARY:".Length..].Trim()
            : "";
        string topicsRaw = topicsIdx >= 0
            ? lines[topicsIdx].Trim()["TOPICS:".Length..]
            : "";
        string[] topics = topicsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .Take(6)
            .ToArray();
        string relation = relationIdx >= 0
            ? lines[relationIdx].Trim()["RELATION:".Length..].Trim()
            : "";

        if (summary.Length == 0)
            summary = $"{string.Join(", ", npcs.Select(DisplayName))} spent some time chatting.";

        string date = $"{Game1.CurrentSeasonDisplayName} {Game1.dayOfMonth}, Year {Game1.year}";
        string where = WhereIs(npcs[0]);
        string names = string.Join(" & ", npcs.Select(DisplayName));

        // 1. Remember it and update every pair's relationship arc, then save both.
        lock (_memoryLock)
        {
            _memories.Add(new MemoryEntry
            {
                Date = date,
                Where = where,
                Who = npcs.ToArray(),
                Transcript = transcript,
                Summary = summary,
                Topics = topics,
                Relation = relation
            });

            for (int i = 0; i < npcs.Count; i++)
            {
                for (int j = i + 1; j < npcs.Count; j++)
                {
                    string id = PairId(npcs[i], npcs[j]);
                    if (!_pairs.TryGetValue(id, out var pr))
                    {
                        pr = new PairRelationship { Npcs = new[] { npcs[i], npcs[j] } };
                        _pairs[id] = pr;
                    }

                    if (relation.Length > 0)
                    {
                        pr.Tone = relation;
                        pr.Notes.Add($"{date}: {relation}");
                        if (pr.Notes.Count > 10)
                            pr.Notes.RemoveRange(0, pr.Notes.Count - 10);
                    }
                    pr.LastDate = date;
                }
            }

            _data.WriteSaveData(MemoryKey, _memories);
            _data.WriteSaveData(PairKey, _pairs);
        }

        // 2. Show it to the player. Conversation mode (default): a header box plus one
        //    portrait box per line, clicked through like real dialogue — the queue waits
        //    its turn if the player is busy. Text mode: the old single lump box, only
        //    when the player is free right now.
        string header = $"{names} — chatting at {where} ({date}):";
        if (_config.ConversationMode)
        {
            ConversationDisplay.Start(npcs, header, transcript);
        }
        else if (Context.IsPlayerFree)
        {
            Game1.drawObjectDialogue($"{header}\n\n{transcript}");
        }

        // 3. Tell the village: a factual note plus a couple of overheard lines, so the
        //    specifics (not just 'they chatted') make it into tonight's gossip analysis.
        bool accepted = _api.InjectGossip(_modId, BuildGossipNote(names, where, summary, transcript));

        _monitor.Log(
            $"Chat saved ({names} at {where}). "
            + (_config.ConversationMode ? "Conversation queued. " : "")
            + (relation.Length > 0 ? $"Relationship: {relation} " : "")
            + (accepted ? "Gossip noted." : "Gossip not accepted today (this is normal)."),
            LogLevel.Info);
    }

    /// <summary>
    /// The end-of-day gossip note: what happened, the recap, and up to two overheard lines.
    /// AliveNpcs cuts notes at 1200 characters, so we pre-trim to keep the ending clean.
    /// </summary>
    private static string BuildGossipNote(string names, string where, string summary, string transcript)
    {
        var quotes = transcript.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Take(2)
            .Select(l => l.Length <= 180 ? l : l[..180] + "…")
            .ToList();

        string note = $"{names} were seen chatting at {where}. {summary}";
        if (quotes.Count > 0)
            note += " Overheard: \"" + string.Join("\" … \"", quotes) + "\"";

        return note.Length <= 1200 ? note : note[..1200];
    }

    /*********
    ** Console output
    *********/

    public void PrintStatus()
    {
        RefreshVillagers();
        int pairCount;
        lock (_memoryLock)
            pairCount = _pairs.Count;

        _monitor.Log(
            $"{_villagers.Count} villagers available: {string.Join(", ", _villagers)}. "
            + $"{_memories.Count} memories stored, {pairCount} pair relationships. "
            + $"Romance {(_config.AllowRomance ? "on" : "off")}, "
            + $"display {(_config.ConversationMode ? "conversation" : "text")}. "
            + "Try chat_memories, chat_pairs.",
            LogLevel.Info);
    }

    public void PrintMemories(int count)
    {
        lock (_memoryLock)
        {
            if (_memories.Count == 0)
            {
                _monitor.Log("No memories stored yet for this save.", LogLevel.Info);
                return;
            }

            foreach (var m in _memories.TakeLast(count).Reverse()) // newest first
                _monitor.Log(
                    $"[{m.Date}] {string.Join(" & ", m.Who)} at {m.Where}: {m.Summary}"
                    + (m.Topics is { Length: > 0 } ? $" — topics: {string.Join(", ", m.Topics)}" : ""),
                    LogLevel.Info);
        }
    }

    /// <summary>Prints every pair relationship arc, most recently active first.</summary>
    public void PrintPairs()
    {
        lock (_memoryLock)
        {
            if (_pairs.Count == 0)
            {
                _monitor.Log(
                    "No pair relationships yet — run some chats and their arcs will grow here.",
                    LogLevel.Info);
                return;
            }

            foreach (var pair in _pairs.Values.OrderByDescending(p => p.LastDate))
            {
                string who = string.Join(" & ", pair.Npcs.Select(n => DisplayName(n)));
                _monitor.Log(
                    $"{who}: {(pair.Tone.Length > 0 ? pair.Tone : "(no tone yet)")} "
                    + $"— {pair.Notes.Count} beats, last talk {pair.LastDate}",
                    LogLevel.Info);
            }
        }
    }
}
