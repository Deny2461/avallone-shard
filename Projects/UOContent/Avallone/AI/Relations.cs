using System;
using System.Collections.Generic;
using System.Linq;
using Server.Avallone.Named;
using Server.Items;
using Server.Misc;
using Server.Mobiles;

namespace Server.Avallone.AI;

// Step 5c-2 2b (relationship dials) - the shard side. Design: wiki Projects/Avallone-Memory-Affinity-Design.md,
// "Step 2b - relationship dials: DESIGN v1", amended by B12 (Codex 2b design review, 2026-11-01).
// - ONE npc id for every dial hook and read (B12 #2): a named NPC's own AiId (Mirabel's serialized _aiId), a wanderer's
//   AiId, else the AiIdentity townsfolk already use. Companions are excluded before any evidence is written.
// - The standing block a conversation request carries (B3): karma (trust), fame (respect), game hour + moon phase at the
//   NPC (ambience "now", not a clock), the NPC's rumour town. The brain works trust/respect out from it; nothing stored.
// - Daily evidence (B12 #5, #7, #8): "visited" for townsfolk and wanderers once a reply was really said (named NPCs keep
//   theirs in NamedNpcs.Ask), "traded" after a real vendor transfer - at most one of each per NPC + character + Central
//   day per shard run. A restart can repeat a day's row; the brain counts distinct days, so a repeat changes nothing.
public static class Relations
{
    public static bool Enabled { get; private set; } = true;

    // Tests swap this; the shard writes through the saved outbox.
    // Codex 2b code review F1 + round 2 A: this run reconciled successfully (a definite flag, never inferred from Full),
    // and memory is not off. A seam for tests, which have no brain - set it only through LedgerReadyForTests.
    internal static Func<bool> LedgerReady = DefaultLedgerReady;

    private static bool DefaultLedgerReady() =>
        AiLedger.Reconciled && AiLedger.State is AiLedger.LedgerState.Live or AiLedger.LedgerState.Full && AiLedger.Epoch != 0;

    // Codex round 2 (test isolation): scoped, so a test never leaves an always-ready gate behind.
    internal static IDisposable LedgerReadyForTests(bool ready)
    {
        LedgerReady = () => ready;
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => LedgerReady = DefaultLedgerReady;
    }

    // Codex 2b code review F3: success-returning, so a day is marked only once its row really entered the outbox.
    internal static Func<Guid, Mobile, string, object, bool> Recorder = AiLedger.TryRecord;

    private const int MaxHeld = 4096;
    private static readonly Dictionary<(Guid Npc, Guid Chr, string Kind), string> _daily = new();

    public static void Configure() =>
        Enabled = ServerConfiguration.GetOrUpdateSetting("avallone.relations.enabled", true);

    // The id every 2b hook and read uses for this NPC; Guid.Empty = not dial-eligible.
    public static Guid NpcId(Mobile npc) => npc switch
    {
        null                             => Guid.Empty,
        { Deleted: true }                => Guid.Empty,
        BaseCompanion                    => Guid.Empty,
        INamedNpc n                      => n.AiId,
        BaseWanderer w                   => w.AiId,
        _ when TownsfolkTalk.IsTownsperson(npc) => AiIdentity.GetOrCreate(npc),
        _                                => Guid.Empty
    };

    // `town` overrides the region lookup (Mirabel hears Britain's gossip wherever she stands).
    public static StandingAsk Standing(Mobile npc, Mobile player, string town = null)
    {
        if (!Enabled || player is not PlayerMobile || npc?.Map == null || npc.Map == Map.Internal || NpcId(npc) == Guid.Empty)
        {
            return null;
        }

        // Codex 2b code review F1: no relationship reads until this run's ledger has reconciled with the brain (Live or
        // Full); while Starting or Off the NPC simply talks without dials.
        if (!LedgerReady())
        {
            return null;
        }

        Clock.GetTime(npc.Map, npc.X, npc.Y, out int hours, out int _);
        return new StandingAsk(
            Math.Clamp(player.Karma, Titles.MinKarma, Titles.MaxKarma),
            Math.Clamp(player.Fame, 0, Titles.MaxFame),
            hours,
            Clock.GetMoonPhase(npc.Map, npc.X, npc.Y).ToString(),
            town ?? Server.Avallone.Rumours.RumourSpeech.TownOf(npc),
            Server.Avallone.Rumours.PlayedClock.Seconds,
            Server.Avallone.Rumours.PlayedClock.MemorySeconds,
            Gifts.LikesFor(npc).Say // what "what do you like?" can truly be answered with (the owner: players must be able to learn it)
        ) { Epoch = AiLedger.Epoch };
    }

    // After a townsperson or wanderer really said a reply.
    public static void Visited(Mobile npc, Mobile player)
    {
        if (npc is not INamedNpc)
        {
            Daily(npc, player, "visited");
        }
    }

    // 2b (design B4): after the NPC really answered a line on the narrow slur list - warmth -20, once per NPC + character + day.
    public static void Conduct(Mobile npc, Mobile player, AiReply reply)
    {
        if (reply.Conduct == "hateful_language")
        {
            Daily(npc, player, "player_used_hateful_language");
        }
    }

    // After a vendor transfer that moved goods (BaseVendor.OnBuyItems / OnSellItems, past their no-op exits).
    public static void Traded(Mobile npc, Mobile player) => Daily(npc, player, "traded");

    private static void Daily(Mobile npc, Mobile player, string kind)
    {
        if (!Enabled || player is not PlayerMobile || player.Deleted)
        {
            return;
        }

        var id = NpcId(npc);
        if (id == Guid.Empty)
        {
            return;
        }

        var today = NamedNpcs.Today(Core.Now);
        var key = (id, AiIdentity.GetOrCreate(player), kind);
        if (_daily.TryGetValue(key, out var day) && day == today)
        {
            return;
        }

        if (_daily.Count >= MaxHeld)
        {
            foreach (var old in _daily.Where(p => p.Value != today).Select(p => p.Key).ToList())
            {
                _daily.Remove(old);
            }
        }

        if (Recorder(id, player, kind, new { day = today, mpt = Server.Avallone.Rumours.PlayedClock.MemorySeconds }))
        {
            _daily[key] = today;   // a full or off outbox leaves the day open: the next qualifying moment tries again
        }
    }

    public static int DaysHeld => _daily.Count;

    internal static void ClearForTests() => _daily.Clear();
}
