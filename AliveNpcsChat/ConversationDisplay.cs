using StardewModdingAPI;
using StardewValley;

namespace AliveNpcsChat;

/// <summary>
/// Shows a saved chat as a Stardew-style conversation instead of one text lump: a header
/// box, then one portrait dialogue box per transcript line, in speaking order. The player
/// clicks through them like normal villager dialogue.
///
/// Lines are queued up-front and fed one at a time from UpdateTicked (wired in ModEntry):
/// the game only closes a dialogue box on player input, so we watch Game1.dialogueUp /
/// activeClickableMenu / eventUp and wait for them to clear, plus a short beat between
/// boxes. If a speaker can't be matched to a chat participant, or their portrait can't
/// render, the line falls back to a plain dialogue box with the original text.
/// </summary>
internal static class ConversationDisplay
{
    private sealed record Line(string NpcName, string Text);

    /// <summary>Queued boxes still to show. Ephemeral: never saved, cleared on new chats.</summary>
    private static readonly Queue<Line> _pending = new();

    /// <summary>Ticks to wait after a box closes before opening the next one.</summary>
    private static int _delayFrames;

    /// <summary>Queue a whole conversation: a header, then one box per transcript line.</summary>
    public static void Start(List<string> participants, string header, string transcript)
    {
        _pending.Clear();
        _delayFrames = 0;

        // Header first: a plain box naming who and where.
        _pending.Enqueue(new Line("", header));

        // Then each transcript line ('Name: what they said') spoken by its villager.
        string lastSpeaker = "";
        foreach (string raw in transcript.Split('\n'))
        {
            string text = raw.Trim();
            if (text.Length == 0)
                continue;

            int colon = text.IndexOf(':');
            if (colon > 0)
            {
                string? matched = MatchSpeaker(text[..colon].Trim(), participants);
                if (matched != null)
                {
                    lastSpeaker = matched;
                    _pending.Enqueue(new Line(matched, text[(colon + 1)..].Trim()));
                    continue;
                }
            }
            else if (lastSpeaker.Length > 0)
            {
                // No name on this line: a continuation of whoever spoke last.
                _pending.Enqueue(new Line(lastSpeaker, text));
                continue;
            }

            // Unmatched speaker (or no speaker yet): keep the line verbatim in a plain box.
            _pending.Enqueue(new Line("", text));
        }
    }

    /// <summary>Forget any queued lines (e.g. when a different save loads).</summary>
    public static void Reset()
    {
        _pending.Clear();
        _delayFrames = 0;
    }

    /// <summary>
    /// Feed the queue: called every tick. Opens the next box only when the player is free
    /// (world ready, no dialogue up, no menu, no cutscene) so conversations never interrupt.
    /// </summary>
    public static void Tick()
    {
        if (_pending.Count == 0)
            return;

        if (!Context.IsWorldReady || Game1.eventUp || Game1.dialogueUp || Game1.activeClickableMenu != null)
            return;
        if (_delayFrames > 0)
        {
            _delayFrames--;
            return;
        }

        Line line = _pending.Dequeue();

        // Prefer a portrait box with the speaker's name; fall back to a plain box.
        if (line.NpcName.Length > 0 && Game1.getCharacterFromName(line.NpcName) is NPC npc)
        {
            try
            {
                npc.CurrentDialogue.Clear();
                npc.CurrentDialogue.Push(new Dialogue(npc, null, line.Text));
                Game1.drawDialogue(npc);
                _delayFrames = 10;
                return;
            }
            catch
            {
                // Portrait rendering failed (e.g. a portraitless custom NPC): plain box below.
            }
        }

        Game1.drawObjectDialogue(line.Text);
        _delayFrames = 10;
    }

    /// <summary>Matches a transcript speaker label against the chat participants.</summary>
    private static string? MatchSpeaker(string namePart, List<string> participants)
    {
        foreach (string p in participants)
        {
            if (string.Equals(p, namePart, StringComparison.OrdinalIgnoreCase))
                return p;

            NPC? npc = Game1.getCharacterFromName(p);
            if (npc != null && string.Equals(npc.displayName, namePart, StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return null;
    }
}
