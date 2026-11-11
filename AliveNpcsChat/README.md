# AliveNpcs Chat & Memories (v0.7)

A manual-trigger addon for [AliveNpcs](https://www.nexusmods.com/stardewvalley/mods/43475):
you pick the villagers, they chat about their own lives — and their relationships
with each other evolve on their own, with the farmer as a natural side note and
romance allowed when true to the characters.

## What it does

- Nothing happens automatically. You choose who talks, through the SMAPI console.
- Chats are VILLAGER-CENTRIC with a natural place for the farmer: the prompt centers
  on the villagers' lives and their bond; the farmer may come up when natural,
  grounded by a small slice of real context (2 recent talks + 2 gifts each),
  never dominating.
- Grounding, read from AliveNpcs (read-only):
  - each villager's AliveNpcs personality — preferring your Personality Editor
    custom version when you made one
  - their pair relationship arc from earlier chats
  - how AliveNpcs' social web says they feel about each other
  - village gossip every participant already knows
  - the chats this mod itself has run before
- ROMANCE: villager relationships may develop toward romance — gradual,
  reciprocated, only when true to both characters, never forced. The prompt knows
  the farmer's partners (married/dating/engaged) and that this valley allows
  multiple partners, so bonds between the farmer's partners matter. Toggle with
  `chat_romance [on|off]` (saved to config.json, default on).
- RELATIONSHIP ARCS: each chat ends with a RELATION line (where the pair's bond
  stands after the talk); arcs accumulate per pair (up to 10 beats) and feed back
  into every future chat. See them with `chat_pairs`.
- After each chat: it's saved with summary/topics/relation, a gossip note with
  overheard quotes goes into AliveNpcs' end-of-day gossip analysis, and a dynamic
  prompt block feeds a digest of recent chats (with bond notes) into AliveNpcs'
  DIALOGUE prompts — so villagers can bring them up when you talk to them.
  Deliberately NOT injected into greetings.
- Every AI call uses the provider and quota you set up in AliveNpcs.

## Build

Needs the .NET SDK 6 or later. From the project folder:

    dotnet build

`ModBuildConfig` copies the built mod into your game's `Mods` folder automatically
(set GamePath if your game is in a non-default location).

Since v0.5 the mod compiles directly against `AliveNpcs.dll` (for the prompt-block
API), so:

- AliveNpcs is a required dependency (see manifest.json).
- The .csproj references the DLL at
  `Mods\AliveNpcs\AliveNpcs\AliveNpcs.dll` — if a mod manager moves AliveNpcs,
  update the HintPath in the .csproj.
- The reference is compile-time only (Private=false): never ship AliveNpcs.dll.

## Commands (SMAPI console, in a loaded save)

- `chat_test Abigail Sebastian` — those two chat. Up to 4 villagers:
  `chat_test Abigail Sebastian Penny Emily`
- `chat_random` — two random villagers chat
- `chat_list` — who can chat + memory/pair counts + romance state
- `chat_memories [count]` — print recent chat summaries, newest first
- `chat_pairs` — print every pair's relationship arc
- `chat_romance [on|off]` — toggle romantic development (saved to config.json)
- `chat_probe` — write `chat-probe-report.txt` (data-file reconnaissance)
- `chat_probe_api` — write `chat-api-report.txt` (AliveNpcs.dll API surface)

## Notes

- AliveNpcs' own data files are read read-only, best-effort: if a future AliveNpcs
  update changes a file's format, chats keep working with less grounding.
- Personal-use mod. The API interface is from the MIT-licensed
  https://github.com/LucasGatica/alivenpcs-api — if you ever publish the addon,
  don't include AliveNpcs' files in your download, and tell the author about it.
