# Avallónë — roadmap

From one talking innkeeper to a world that runs itself. Each phase ends in something a player can walk up
to and judge, and the later, bigger phases each get their own go/no-go decision once the earlier ones prove
fun.

**Key:** ✅ live on the shard · 🔧 built, in testing · ⏸️ built, shelved · 📐 designed, not built · 💡 planned idea

```mermaid
flowchart LR
    P0["0 · Harness ✅"] --> P1["1 · First talking NPC ✅"] --> P2["2 · Personas & lore ✅"] --> P3["3 · A companion ✅"]
    P3 --> P4["4 · Memory that lasts ✅"] --> P5["5 · Named NPCs & rumours<br/>(mostly ✅, more coming)"]
    P5 --> P6["6 · Enemies 💡"] --> P7["7 · Daily life 💡"] --> P8["8 · Quests 💡"]
    P8 --> P9["9 · Economy 💡"] --> P10["10 · Society 💡"] --> P11["11 · Settlements 💡"]
```

---

## Done

### Phase 0 — The harness ✅
The plumbing, before anyone says a word: the gateway, priority queue, deadlines, circuit breaker, kill
switch, stable AI identities, and metrics.
*Done when:* the shard played normally with the model machine unplugged, and nobody could tell.

### Phase 1 — The first talking NPC ✅
**Mirabel**, innkeeper of the Blue Boar in Britain. Short memory, structured replies, stale replies thrown
away, and no actions that change the world.
*Done when:* a natural five-minute conversation, and sensible fallbacks with the model switched off.

### Phase 2 — Personas and lore ✅
Personalities as data files the brain reloads without a restart. Lore comes in from an approved, canon-only
source. Admin commands came too (`[ai status`, `[ai on|off`…).

### Phase 3 — A permanent companion ✅
A dedicated companion type (the stock hireling deletes itself when unpaid, so it was unsuitable), with
deterministic trust, follow / wait / go home, its own horse, and healing its owner.

### Townsfolk talk ✅
Every shopkeeper and townsperson answers through one shared townsperson persona, with real shop words left to
the game.

### Phase 4 — Memory that lasts, and a personality that grows ✅

| Step | Delivers | |
|---|---|---|
| 4a1 | Restore-safe event ledger (epochs, outbox, acknowledgements) | ✅ |
| 4a2 | Memory in conversation (episode recall, restart-safe history) | ✅ |
| 4b | Tiered secrets for the first companion | ✅ |
| 4c1 | Turning points and personality traits | ✅ |
| 4c2 | Reflection and beliefs, first in shadow mode, then live | ✅ |
| 4d | Soak: deliberate failure tests in the brain and in game, plus fixes | ✅ |
| 4e | Companion gear, and typo-tolerant names for every NPC | ✅ |

Along the way: **answering without the name** (a short conversation window plus a real game-command
list; replaced in Phase 5 by the conversation lock) ✅, and **NPCs answer only what is meant for them** ✅.

### The wanderer prototype ✅
One sellsword who travels, hunts, sells, banks and rests on his own, with route planning: the first step
toward NPCs who "do their own thing".

## In progress

### Phase 5 — Named NPCs and rumours

| Step | Delivers | |
|---|---|---|
| 5a | Deed feed, played-time clock, the rumour network | ✅ |
| 5b | Townsfolk and the innkeeper tell the live rumours | ✅ |
| 5c-1 | Shared layer for named NPCs; durable memory for the innkeeper | ✅ |
| 5c-2 · 2a-0 | A decency rule for every NPC | ✅ |
| 5c-2 · 2a | Structured memory cards | ✅ |
| 5c-2 · shop fix | Buying and selling lines that name nobody stay with the game | ✅ |
| 5c-2 · 2b | Relationship dials, gifts and likes, **the two moons**, companions travel on Recall | ✅ |
| 5c-2 · guard | Money talk kept out of what NPCs remember, so a claimed debt can't be built up over visits (replaced an always-on "no money promises" line) | ✅ |
| 5c-2 · 2c | **NPC mood** (one per NPC per day), an automatic shop hint, reflection v2 | ✅ |
| 5c-2 · step B | A **nature** for every townsperson, a **conversation lock** (who you are talking to), a repeat catcher | ✅ |
| 5c-2 · stage A1 | NPCs notice the companions and pets travelling with you, and now and then speak to or about them | ✅ |
| 5c-2 · stage A2 | Your companion and an NPC trade a line or two (a shared two-speaker engine, later reused for villagers talking to each other) | 📐 |
| 5c-2 · promise guard | A stronger guard that spots a claimed promise and rewrites a reply that gives in | ⏸️ |
| 5c-2 · 2d | Rumours that drift, get corrected, and carry how sure the teller is | 📐 |
| 5c-2 · 2e | Rumours on the same memory model, plus a town-wide reputation | 📐 |
| 5c-2 · rest | More named characters in town, with their own placement file | 📐 |
| 5c-3 | Six new named NPCs | 📐 |
| 5d | A storyline that moves on its own over months of played time, surfacing as rumours | 📐 |
| 5e | Soak test, and the "done" test | 💡 |

*Phase 5 is done when* something a player did in the morning is mentioned, second-hand, by an innkeeper in
another town that evening, and the named NPCs feel like people who know you.

**Also queued for companions:** secrets that surface at a fitting moment instead of only when asked ·
companions as pack mules · jewellery and magic gear, one property at a time · sound-alike name matching
(logged only today).

**Model work:** the brain moved to a newer home model machine, and after a trial of its own, to the larger
Gemma 4 26B model (November 2026). Mood went live on it.

## Coming next

### Phase 6 — Enemies with personality 💡
Brigand leaders, orc captains and liches who taunt, bargain, threaten, parley and surrender, aware of who
they are fighting. A high-level tactical advisor (flee, call allies, focus the healer) on top of the stock
combat AI. Later: named villains who remember their defeats and come back.

### Phase 7 — Daily life 💡
NPC schedules (sleep, work, tavern, temple), driven by cheap deterministic code, with the model adding
colour. This includes **villagers talking to each other** within earshot, built so a player's words always
come first and NPC speech never sets off an endless chain.

### Phase 8 — Quests from templates 💡
The model writes the story and the dialogue. The server picks the template (fetch, slay, escort,
deliver), the target and the reward from validated tables. No made-up rewards. Quest givers remember who
helped them.

## Further out (each its own go/no-go)

### Phase 9 — Economy 💡
Prices that follow supply and demand the server tracks, and craftspeople producing over time.

### Phase 10 — Society 💡
Factions and towns with relationships, plus NPC friendships, rivalries and feuds. The goal is **a couple of
hundred NPCs visibly living their lives**: most of them cheap and scripted, with the model reserved for the
ones a player is actually talking to.

### Phase 11 — Settlements 💡
Settlers who move and build: the server picks a valid plot (no blocking roads, nowhere near players' houses,
a cap per region), places a house from an approved template, and the NPC moves in. A slow "world
director" schedules festivals, raids and disasters.
*Done when:* a player comes back after a week away to find things changed that nobody scripted, and that
make sense.

## Ideas being weighed

- Should background townsfolk answer at all when spoken to, or stay part of the scenery?
- Background decisions could move to a second, CPU-only model machine once there are many wanderers.
- A meaning-level moderation check, on top of the word list.
- Embedding search for memory, but only if the full-text recall visibly misses things.
- Talking guards.
