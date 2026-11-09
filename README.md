# Avallónë — an Ultima Online shard where the townsfolk and NPCs come to life

Avallónë is a small, private [Ultima Online](https://uo.com) shard built on
[ModernUO](https://github.com/modernuo/ModernUO). On top of the stock game it adds NPCs that hold real
conversations. Innkeepers remember you, a travelling companion grows to trust you, and townsfolk pass on
gossip about what players have actually done. The talking comes from an open-weight language model running
on a machine in the same house, with no cloud service involved.

The whole project follows one rule: **the AI adds flavour and life; it never gets to break the game.**

| Read this | For |
|---|---|
| [DESIGN.md](avallone-docs/DESIGN.md) | How it works: the guiding rules, the architecture, and each system (conversation, memory, trust, rumours, moons…), with diagrams |
| [ROADMAP.md](avallone-docs/ROADMAP.md) | What is live, what is being built, and what comes next |
| This page | A quick tour, the map of the code, and a glossary |

> **About this copy.** This is a snapshot of the shard's code, not its working history. The story content
> is replaced with short **made-up samples**: the named characters' personalities, the gift preferences, the
> personality turning points and the small talk. Character background that was written into the code itself
> is reworded to match. So the code runs and shows how everything fits together without telling the world's
> story. Companion secrets, storyline stages and lore were never stored in the code; they live in a separate
> store that is not published.

---

## A quick tour: what a player notices

- **You can just talk.** Walk up to Mirabel, innkeeper of the Blue Boar in Britain, and say anything. She
  answers in character, in the Samurai Empire era, in a sentence or two.
- **Every shopkeeper has a voice.** Ordinary townsfolk answer too, through one shared townsperson persona.
  Real shop words still work as normal (`vendor buy`, `bank`, `stable`).
- **You don't have to say their name.** Once you start talking with someone, lines that name nobody keep
  going to them until you name someone else, say goodbye, walk away or go quiet for a few minutes. With no
  conversation going, the nearest person answers.
- **Townsfolk are not all alike.** Every ordinary NPC has a nature of its own (one of 64), a speech habit and
  a pet subject, and one mood for the day that is the same for everyone who talks to them. About one in six
  is prickly and may toss you a mild, in-character jab.
- **A companion travels with you.** A permanent companion can be recruited, follows you, fights beside you,
  heals you and rides with you. It starts out wary. Trust is earned over days of real play, and with trust
  come stories it would not tell a stranger.
- **People remember.** Named NPCs remember what you told them, what you gave them and how often you visit.
  A regular is greeted differently from a stranger.
- **News travels.** Kill something notable, get drunk in the tavern or come back after a long absence, and
  the story spreads town to town along the roads. It gets a little garbled as it goes, and fades with time.
- **The moons matter.** When both of Sosaria's moons are bright, the superstitious folk get jumpy and trust
  strangers less.
- **They notice who you travel with.** Townsfolk and the innkeeper can see your companion and your pets, and now
  and then speak to your companion or remark on your horse, by the name you gave it.
- **A wandering sellsword lives his own life.** He travels, hunts, sells his loot, banks and rests, whether
  or not anyone is watching.

## Map of the code

Everything Avallónë adds lives in five places. The rest of the tree is ModernUO, lightly patched where a
hook was needed.

### `Projects/UOContent/Avallone/`: the game side (C#)

| Folder | What is in it |
|---|---|
| `AI/` | The **gateway** to the brain (queue, priorities, timeouts, circuit breaker, quiet hours, warm-up), stable AI identities, the **event ledger** and its outbox, secret gates, personality traits, the townsfolk listener, the "who was spoken to" resolver, the list of real game commands to leave alone, relationship events, gifts, and the `[ai` admin commands |
| `Companions/` | `BaseCompanion` and its gear, trust, trailing AI, home spots and gear custody box; one concrete companion |
| `Mobiles/` | Mirabel, the first talking NPC |
| `Named/` | The shared layer for named NPCs, and a GM view that explains every number |
| `Rumours/` | The **deed hooks** that turn public events into news, the played-time clock, and how rumours are spoken with names bound safely |
| `Wanderers/` | The self-directed sellsword: travel, hunt, sell, bank, rest, with route planning |
| *(top level)* | Shard rule switches (`AvalloneRules`), extra spawns, champion and serpent-pillar tweaks |

### `brain/`: the conversation service (Python)

A small HTTP service that runs beside the shard. It builds each prompt, calls the model, checks the answer,
and keeps long-term memory in SQLite.

| Area | Files |
|---|---|
| Service, prompts, filters | `brain.py` |
| Memory | `memory.py` (event ledger), `reflection.py` (beliefs), `cards.py` + `concepts.json` + `memory_rules.json` (structured memory), `named.py` |
| Rumours and small talk | `rumours.py`, `rumour_speech.py`, `smalltalk.py` (+ their `.json` data) |
| Relationships, mood, natures, moons | `dials.py` + `dials.json`, `mood.py` + `mood.json`, `nature.py` + `nature.json` |
| Guards | `decency.py`, `shop_hint.py`, `variety.py` (repeat catcher) |
| Personas | `personas/` (sample personalities) |
| Knowledge | `lore.py`, `companion_secrets.py` (the code only; the secret text lives outside the repository) |
| Trials | `model_trial.py`, `speed_trial.py` (model checks against the real request path) |

### Data and tests

- `Distribution/Data/Avallone/`: companion home spots, gift likes, extra spawners, turning-point rules
  (sample) and the word list used for name matching.
- `Projects/UOContent.Tests/Tests/Avallone/`: about 190 C# tests across 27 files. The shard's content test
  suite runs about 1,650 tests.
- `brain/test_*.py`: about 400 Python tests across 27 files. Run them **one file at a time**. A
  single-process run of all of them has known cross-file leakage.

## Running it, in outline

This is a personal project, not a packaged product, so expect to read code. The rough shape:

1. Build and run the shard as for any ModernUO server (see ModernUO's own README). You need your own
   licensed Ultima Online client data.
2. Run the brain (`brain/brain.py`) with a JSON config file. It needs an [Ollama](https://ollama.com) server
   with an open-weight chat model; set its address as `ollama_url` in the config.
3. Point the shard's AI settings at the brain, then use `[ai status` in game to see the gateway, and
   `[ai off` / `[ai on` as the kill switch.
4. Keep the brain private. It has no authentication and refuses to listen anywhere but loopback. Only
   the game port should ever be reachable from a network.

With the brain switched off or unreachable, the shard plays as ordinary Ultima Online. That is by design.

**Hardware, for scale:** the shard and brain share a 2-core, 4 GB Linux container. The model runs on an
Apple Mac mini (M5 Pro, 64 GB). The model is Gemma 4 26B, a mixture-of-experts model with about 4B
parameters active per word (since November 2026; before that Gemma 4 12B at 4-bit, which also ran fine on
an older 24 GB Mac mini). On an evening of real play, a typical NPC reply took about 0.65 seconds. Details,
and how the model was chosen:
[DESIGN.md](avallone-docs/DESIGN.md#what-it-runs-on).

## Glossary

| Term | Meaning here |
|---|---|
| **Brain** | The Python service that turns "player said X to NPC Y" into a reply. The shard never talks to the model directly. |
| **Gateway** | The shard-side door to the brain: one request at a time, a priority queue, deadlines, and a circuit breaker. |
| **Persona** | A data file describing who an NPC is: role, temperament, speech, what they know. The fixed core of a character. |
| **Ledger** | The append-only record of things that happened (fights, gifts, visits, deeds) that memory is built from. |
| **Epoch** | A counter that goes up every time the shard boots. Together with the save's high-water mark it keeps memory honest after a restore. |
| **Belief** | A short conclusion an NPC draws about a player from facts, written during quiet time ("reflection"). |
| **Turning point** | A rule-based moment that permanently nudges one of a companion's personality traits. |
| **Dials** | Per-NPC, per-character relationship numbers: familiarity, warmth, trust, respect. |
| **Deed** | A public event the shard reports as possible news (a notable kill, a tame, a murder…). |
| **Rumour** | A deed as it is told in a given town at a given time, worked out fresh on every read. |
| **Exposure** | Which stories have actually reached a town. It drives effects like town unease. |
| **Played clock** | Time that only runs while a real player is online. Rumours and memory age on it. |
| **Glow** | Real moon brightness × the 18-hour moon's brightness. Superstitious NPCs are spooked when it passes their personal line. |
| **Quiet window** | A short daily slot when the brain leaves the model alone for another job. |

## Credits and licence

- Built on **[ModernUO](https://github.com/modernuo/ModernUO)** by the ModernUO Development Team, and like
  it, licensed under the **GNU General Public License v3.0** (see `LICENSE`). All upstream notices are kept.
- *Ultima Online* is a trademark of Electronic Arts. This is an unofficial fan project. It contains no
  client data, and you need your own.
- Built by the shard's owner working with AI coding assistants: Claude (Anthropic) writing most of the code
  and documents, and Codex (OpenAI) independently reviewing the designs and changes, normally before they
  went live (a few small changes went live on the owner's call before their review round).
- The conversation runs on open-weight models served locally through Ollama.
- The decency filter's real word list is kept private. This copy has a short placeholder list
  (`brain/decency_terms.json`) so the code runs.
- The word list used for name matching (`Distribution/Data/Avallone/words.txt`) comes from **SCOWL**
  (Spell Checker Oriented Word Lists, by Kevin Atkinson), as packaged in Debian's `wamerican`, plus 11
  chat-slang words. Its licence notice is in `Distribution/Data/Avallone/words-LICENSE.txt`.

- ModernUO's own README is kept as [README-ModernUO.md](README-ModernUO.md).
