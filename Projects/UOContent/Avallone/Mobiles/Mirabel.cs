using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ModernUO.Serialization;
using Server.Avallone.AI;

namespace Server.Mobiles;

// Mirabel Ashgrove, keeper of the Blue Boar (Britain) - Avallone's first talking NPC (AI plan, Phase 1).
// An ordinary innkeeper (buying and selling work as usual) who also answers speech through the AI gateway.
// Persona lives in the sidecar (brain/personas/mirabel.txt), built from canon Lore/Avallone pages.
//
// Phase 1 rules: short memory in RAM only (lost on restart), no world-changing actions, replies dropped
// if the player has walked away, and failures degrade to silence (one emote per player every few minutes).
[SerializationGenerator(0, false)]
public partial class Mirabel : InnKeeper, Server.Avallone.Named.INamedNpc
{
    private const string PersonaKey = "mirabel";
    private const int HearRange = 4;
    private const int StaleRange = 10;
    private const int HistoryLines = 8;

    private static readonly TimeSpan PerPlayerCooldown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FallbackCooldown = TimeSpan.FromMinutes(3);

    private static readonly string[] FallbackEmotes =
    [
        "*wipes down the bar, too busy to talk just now*",
        "*is called away to the kitchen*",
        "*nods absently, counting coins*"
    ];

    [SerializableField(0)]
    private Guid _aiId;

    private readonly Dictionary<Mobile, List<AiLine>> _history = new();
    private readonly Dictionary<Mobile, string> _followRumour = new(); // Phase 5b: the rumour just told (id only)
    private readonly Dictionary<Mobile, DateTime> _nextHeard = new();
    private readonly Dictionary<Mobile, DateTime> _nextFallback = new();

    [Constructible]
    public Mirabel()
    {
        _aiId = Guid.NewGuid();
        Name = "Mirabel Ashgrove";
    }

    public override bool GetGender() => true;

    // Phase 5c: INamedNpc.AiId is the generated property of her serialized _aiId - one id for requests AND memories
    // (Codex finding 2), never a new one.

    public string NamedKey => PersonaKey;

    [AfterDeserialization]
    private void AfterDeserialization()
    {
        if (_aiId == Guid.Empty)
        {
            _aiId = Guid.NewGuid();
        }
    }

    public override bool HandlesOnSpeech(Mobile from) =>
        from is PlayerMobile && from.InRange(this, HearRange) || base.HandlesOnSpeech(from);

    public override void OnSpeech(SpeechEventArgs e)
    {
        NameResolver.BeginSpeech(e); // one name computation per spoken line, shared by every handler (4e, Codex round 3)
        // A shop COMMAND ("buy", "vendor sell", "Mirabel buy") goes to the vendor code as before; a sentence that only
        // mentions buying ("Mirabel, where can I buy a sword?") is hers to answer, so the shop window is not opened
        // (the owner, chat, 2026-10-30).
        // 2026-11-01: a nameless "I want to buy some cheap wine" and any "sell me…" are hers to answer too (they were
        // silent: no shop opens without a name). Named lines stay shop commands. See GameCommands.OnlyTradeTalk.
        var tradeTalk = IsTradeKeyword(e) && GameCommands.OnlyTradeTalk(
            e.Keywords,
            e.Speech,
            StartsWithFirstName(e.Speech) || TownsfolkTalk.NamesSomeoneNearby(e.Mobile, e.Speech)
        );
        if (!tradeTalk)
        {
            base.OnSpeech(e); // vendor buy / sell keep working
        }

        var from = e.Mobile;

        // Stock vendors count as named only on their FULL name ("Mirabel Ashgrove buy"); "Mirabel buy" is hers too
        // (dev play test 5, 2026-10-30). Same distance as the stock vendor code (1 tile under AOS).
        // 2026-11-01: also her name later in the line or a near spelling ("mirable buy", "I'd buy wine, Mirabel") - the
        // same name resolver the townsfolk shopkeepers use (dev play test 2, 2026-10-31).
        if (!e.Handled && !tradeTalk && from is PlayerMobile && from.Alive && from.Map == Map &&
            (e.HasKeyword(0x171) || e.HasKeyword(0x177)) && NamesHer(from, e.Speech))
        {
            e.Handled = true;
            if (!from.InRange(this, Core.AOS ? 1 : 4))
            {
                SayTo(from, "Step up to the counter if you want to trade, traveller.");
            }
            else if (e.HasKeyword(0x177))
            {
                VendorSell(from);
            }
            else
            {
                VendorBuy(from);
            }

            return;
        }

        if (e.Handled || Deleted || from is not PlayerMobile || !from.Alive || from.Map != Map ||
            !from.InRange(this, HearRange) || IsTradeKeyword(e) && !tradeTalk)
        {
            return;
        }

        var text = e.Speech?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Spoken to a companion standing nearby (named, or mid-conversation with her) - not to Mirabel.
        // Phase 4e N: also a line that names someone else nearby (exactly or close enough), or fits two names equally.
        // Step B: while the player is talking to someone else (Conversation), a line naming nobody is theirs.
        if (TownsfolkTalk.IsStaffCommand(text) || BaseCompanion.IsTalkingToCompanion(from, text) ||
            BaseWanderer.IsTalkingToWanderer(from, text) || TownsfolkTalk.NamesSomeoneNearby(from, text, this) ||
            !NamesHer(from, text) && Conversation.HeldByOther(from, this))
        {
            return;
        }

        var now = Core.Now;
        if (_nextHeard.TryGetValue(from, out var next) && now < next)
        {
            return;
        }

        _nextHeard[from] = now + PerPlayerCooldown;
        Conversation.Note(from, this, text);   // Step B: the conversation lock
        _ = RespondAsync(from, text);
    }

    private bool NamesHer(Mobile from, string speech)
    {
        if (StartsWithFirstName(speech))
        {
            return true;
        }

        var r = NameResolver.Resolve(from, speech);
        return !r.Ambiguous && r.Winner == this;
    }

    private bool StartsWithFirstName(string speech)
    {
        var first = Name?.Split(' ')[0];
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(speech))
        {
            return false;
        }

        var s = GameCommands.WithoutGreeting(speech).TrimStart();   // "Hello Mirabel buy" too (Codex round 2 finding 4)
        return s.StartsWith(first, StringComparison.OrdinalIgnoreCase) &&
               (s.Length == first.Length || !char.IsLetterOrDigit(s[first.Length]));
    }

    // *vendor sell*, *vendor buy*, *sell*, *buy* - left to the normal vendor code.
    private static bool IsTradeKeyword(SpeechEventArgs e) =>
        e.HasKeyword(0x14D) || e.HasKeyword(0x3C) || e.HasKeyword(0x177) || e.HasKeyword(0x171);

    private async Task RespondAsync(Mobile from, string text)
    {
        if (!_history.TryGetValue(from, out var history))
        {
            PruneGone();
            _history[from] = history = [];
        }

        _followRumour.TryGetValue(from, out var follow);
        if (history.Count == 0 || history[^1].Text != Server.Avallone.Rumours.RumourSpeech.Marker)
        {
            follow = null;
        }

        var openingRevision = Server.Avallone.AI.Exchange.RevisionOf(from);   // Codex A2-R1: see TownsfolkTalk
        var fix = Server.Avallone.Rumours.Corrections.Take(this, from, openingRevision); // rumour step, Part A

        var request = new AiRequest(_aiId, AiIdentity.GetOrCreate(from), null, text, AiPriority.Conversation)
        {
            PersonaKey = PersonaKey,
            History = Server.Avallone.Rumours.RumourSpeech.History(history),
            // Phase 5b: Mirabel keeps the Blue Boar in Britain - she hears Britain's gossip wherever she stands.
            Rumours = Server.Avallone.Rumours.RumourSpeech.Ask(this, from, text, history.Count == 0, follow, "Britain"),
            Named = Server.Avallone.Named.NamedNpcs.Ask(this, from), // Phase 5c: her memories of this visitor
            Standing = Relations.Standing(this, from, "Britain"), // 5c-2 2b: same town as her gossip
            Party = Server.Avallone.AI.TravelParty.For(this, from), // Stage A1: companions + pets she can see
            Falling = RealWeather.Word(this), // rumour step C3
            Correction = Server.Avallone.Rumours.Corrections.AskFor(fix)
        };

        Direction = GetDirectionTo(from);

        var reply = await AiGateway.RequestAsync(request);

        // Stale-reply protection: the player logged out, died, or walked off while she was thinking.
        if (Deleted || from.Deleted || from.NetState == null || !from.Alive || from.Map != Map ||
            !from.InRange(this, StaleRange))
        {
            return;
        }

        if (reply.Ok)
        {
            var spoken = Server.Avallone.Rumours.RumourSpeech.Fill(this, from, reply, "Britain", request.Rumours, bind: true);
            if (spoken == null)
            {
                return; // a gossip line that failed its name check is not said
            }

            // Part A: the accepting line only once the correction is in the saved outbox; else the ordinary reply.
            spoken = Server.Avallone.Rumours.Corrections.Commit(this, from, fix, reply) ?? spoken;
            Direction = GetDirectionTo(from);
            Say(spoken);
            Server.Avallone.AI.TravelParty.Note(from, request.Party, spoken, reply.Rumour != null || reply.Conduct != null || reply.Kind == "decency");
            // Stage A2: if she spoke to the player's companion by name, the companion may answer (switch avallone.ai.exchanges).
            Server.Avallone.AI.Exchange.AfterReply(
                this, from, request.Party, spoken, reply.Rumour != null || reply.Conduct != null || reply.Kind is "decency" or "shop",
                openingRevision, Server.Avallone.AI.Exchange.ReactSortOf(reply) // rumour step C1
            );
            Remember(history, "player", text);
            Remember(history, "npc", Server.Avallone.AI.TravelParty.ForHistory(Server.Avallone.Rumours.RumourSpeech.HistoryText(reply, spoken), request.Party));
            Server.Avallone.Named.NamedNpcs.OnExchange(this, from, text); // the visitor's own words, for this visit's notes
            Server.Avallone.Named.NamedNpcs.OnDelivered(this, from, reply.MemoryCards); // 5c-2 2a: which cards were offered
            Server.Avallone.AI.Relations.Conduct(this, from, reply); // 2b: warmth -20 for a slur, once a day
            _followRumour[from] = Server.Avallone.Rumours.RumourSpeech.FollowId(reply);
            Server.Avallone.Rumours.Corrections.AfterReply(this, from, reply, openingRevision); // Part A: a ticket, or none
            return;
        }

        if (reply.Outcome is AiOutcome.Busy or AiOutcome.Disabled)
        {
            return;
        }

        var now = Core.Now;
        if (_nextFallback.TryGetValue(from, out var next) && now < next)
        {
            return;
        }

        _nextFallback[from] = now + FallbackCooldown;
        Emote(FallbackEmotes[Utility.Random(FallbackEmotes.Length)]);
    }

    private static void Remember(List<AiLine> history, string speaker, string text)
    {
        history.Add(new AiLine(speaker, text));
        if (history.Count > HistoryLines)
        {
            history.RemoveRange(0, history.Count - HistoryLines);
        }
    }

    private void PruneGone()
    {
        if (_history.Count < 32)
        {
            return;
        }

        var gone = new List<Mobile>();
        foreach (var m in _history.Keys)
        {
            if (m.Deleted || m.NetState == null)
            {
                gone.Add(m);
            }
        }

        foreach (var m in gone)
        {
            _followRumour.Remove(m);
            _history.Remove(m);
            _nextHeard.Remove(m);
            _nextFallback.Remove(m);
        }
    }
}
