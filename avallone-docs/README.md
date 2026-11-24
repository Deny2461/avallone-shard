# Avallónë — an Ultima Online shard where the townsfolk and NPCs come to life

Avallónë is a small, private [Ultima Online](https://uo.com) shard built on
[ModernUO](https://github.com/modernuo/ModernUO). On top of the stock game it adds NPCs that hold real
conversations. Innkeepers remember you, a travelling companion grows to trust you, and townsfolk pass on
gossip about what players have actually done. The talking comes from an open-weight language model running
on a machine in the same house, with no cloud service involved.

The whole project follows one rule: **the AI adds flavour and life; it never gets to break the game.**

| Read this | For |
|---|---|
| [DESIGN.md](DESIGN.md) | How it works: the guiding rules, the architecture, and each system (conversation, memory, trust, rumours, moons…), with diagrams |
| [ROADMAP.md](ROADMAP.md) | What is live, what is being built, and what comes next |
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
  Real shop words still work as normal (`vendor buy`, `bank`, `stable`). "Bank" counts as the bank command only
  near a banker, and the town crier only hears "news" from a few steps away, so "Mady, any news?" goes to Mady.
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
  the story spreads town to town along the roads. Big deeds travel further and faster than small ones. A funny story
  grows a little as it goes; a death, a murder or a great deed is told as it happened. All of it fades with time.
- **They know what you have already heard.** An NPC greeting you never brings up a story anyone has already told you.
  Ask for news and one you heard in the last ten minutes is not told again; one you heard earlier is a quarter as
  likely to come up, but can. A story that has changed since counts as new.
- **You can set the record straight.** When someone has just told you a story about yourself, your next line can
  correct a detail: how many, what, or where ("It was four, not forty."). They take your word for it, and that is the
  version told from then on. A murder or a death cannot be talked away.
- **Idle gossip travels too.** The made-up talk of the towns (the price of bread, a wagon with a broken axle) now moves
  from town to town as stories you can ask more about, alongside the real news.
- **Ask about yourself, or ask them not to.** Ask what people say about you and you hear the stories going round about
  you, a different one each time you ask, until they have no more. Ask an NPC not to gossip and it keeps quiet: the NPC
  decides whether to tell, and the code never forces a story into a reply it declined.
- **A town has an opinion of you.** The stories that reached a town over the last week of play shape how it sees you
  (renowned, feared, or a figure of fun), and someone who has never met you may have heard of you.
- **Everyone has a past.** Every townsperson has a home, a former life and one past event, fixed the first time it is
  needed and never changed. Ask them where they are from.
- **Townsfolk have lives.** Everyone who lives and works in a town has an age, years in the town and at the trade, a
  family and a household, worked out once by code and stored. Ask a baker about her family and you get the same answer
  next week; if her sister keeps the stable across town, the sister says so too.
- **They know their town.** Ask where the bank, the healer or the smith is, and a townsperson gives a compass
  direction and a rough distance to the nearest one in their own town, worked out from where people actually stand.
- **They ask favours.** A townsperson who has come to know you a little may, now and then, ask you to carry word to a
  relative, to ask after kin in another town and bring the answer back, or to look in on someone they know and say who
  sent you. Answer yes or no in your own words. The asking sentence, the message and the pay are written by code, not
  by the model, so the favour is word for word the same when you ask to hear it again. A message errand pays a small
  fixed sum in gold (more when it takes you to another town), and both townsfolk remember the kindness. `[Errands`
  lists what you are carrying.
- **The place keeps the person.** A townsperson who is killed and respawns, or comes back after the world is restored
  from an older save, is the same person: same name, same looks, same memory of you.
- **They know where they are.** Town, kingdom and world are kept apart, so someone asked where they are from names
  their town and not the kingdom.
- **Some love to gossip.** Some townsfolk volunteer gossip readily, some never on a greeting, and a sceptic may add a line
  of doubt after a story.
- **The moons matter.** When both of Sosaria's moons are bright, the superstitious folk get jumpy and trust
  strangers less.
- **They notice who you travel with.** Townsfolk and the innkeeper can see your companion and your pets, and now
  and then speak to your companion or remark on your horse, by the name you gave it.
- **Your companion answers for herself.** When a townsperson speaks to your companion by name, the two may trade a few
  lines of their own: usually one to three, once in a while a longer chat. They stop as soon as you speak. After a piece
  of gossip she may add one line of her own.
- **They remember having met, and roughly what about.** A townsperson who has chatted with your companion knows her next
  time, and she knows them. They remember that they talked and the broad subjects (food, family, trade), not what was
  said, and they are nudged toward something they have not covered yet.
- **One weather a day.** Ask around town about the weather and you get the same answer from everyone that day. When it
  is really raining or snowing where they stand, that is what they tell you.
- **You can tell who is talking by colour.** Townsfolk and creatures speak in green; companions and the wandering
  sellsword in yellow.
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
| `Townsfolk/` | The **identity lock**: a seat for every resident townsperson, so name, looks and AI identity survive a respawn or a restore (a register kept outside the saves, a reconcile at every start, quarantine instead of guessing, the `[Seats` operator commands); the **town roster** the shard sends to the brain; a read-only census |
| `Errands/` | **Errands:** when a townsperson may ask a favour, the offer and its closed yes/no lists, delivery, return and pay, the saved errand log, the switches and the `[Errands` commands for players and operators |
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
| Reputation and pasts | `reputation.py` (a town's view of a player), `backgrounds.py` + `backgrounds.json` (townsfolk pasts) |
| Townsfolk lives | `people.py` (ages, years, families: pure functions with their own audit), `people_store.py` (storage, and the code-written lines about an NPC's own life), `people_rules.json`, `people_tags.json`, `people_lists.json`, `people_names.json`, `townroster.py` (the town list the shard sends), `town_knowledge.py` (the nearest places, by direction), `world.py` + `world.json` (town, kingdom, world) |
| Errands | `errands.py` (who may be asked to carry what to whom, and the proof the shard checks at every step), `errands_people.py` (cousins in other towns and people a townsperson knows: drawn once, fingerprinted, audited at every start), `errands_text.py` + `errands_lists.json` (the fixed sentences and the closed word lists) |
| Guards | `decency.py`, `shop_hint.py`, `variety.py` (repeat catcher) |
| Personas | `personas/` (sample personalities) |
| Knowledge | `lore.py`, `companion_secrets.py` (the code only; the secret text lives outside the repository) |
| Trials | `model_trial.py`, `speed_trial.py` (model checks against the real request path) |

### Data and tests

- `Distribution/Data/Avallone/`: companion home spots, gift likes, extra spawners, turning-point rules
  (sample) and the word list used for name matching.
- `Projects/UOContent.Tests/Tests/Avallone/`: about 545 C# tests across 47 files. The shard's content test
  suite runs about 2,380 tests.
- `brain/test_*.py`: about 1,100 Python tests across 60 files. Run them **one file at a time**. A
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
[DESIGN.md](DESIGN.md#what-it-runs-on).

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
| **Seat** | A resident townsperson's permanent place. The identity (name, looks, AI id) belongs to the seat, and whoever the spawner puts there next takes it over. |
| **Roster** | The list of who lives in each town, sent by the shard to the brain. Every request carries the list's stamp, so the brain never answers from a stale list. |
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
  and documents, and **Codex (OpenAI) doing a great deal of the heavy lifting as the independent reviewer**: it
  reviews every design and change, writes its own test probes against the code, and has found and reproduced many of
  the bugs fixed here, normally before a change goes live (a few small changes went live on the owner's call before
  their review round). From November 2026 on, the public snapshots it has reviewed carry a `Reviewed-by: Codex (OpenAI)` line.
- The conversation runs on open-weight models served locally through Ollama.
- The decency filter's real word list is kept private. This copy has a short placeholder list
  (`brain/decency_terms.json`) so the code runs.
- The word list used for name matching (`Distribution/Data/Avallone/words.txt`) comes from **SCOWL**
  (Spell Checker Oriented Word Lists, by Kevin Atkinson), as packaged in Debian's `wamerican`, plus 11
  chat-slang words. Its licence notice is in `Distribution/Data/Avallone/words-LICENSE.txt`.

- ModernUO's own README is kept as [README-ModernUO.md](../README-ModernUO.md).
