using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Server.Avallone.AI;
using Server.Commands;
using Server.Logging;
using Server.Items;
using Server.Mobiles;

namespace Server.Avallone.Rumours;

// Phase 5a - GM view of the rumour network, and the once-a-minute "where has news reached" log.
// Rumours are computed by the brain from the deed events and the played clock; nobody speaks them yet (that is 5b).
public static class RumourCommands
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(RumourCommands));
    private static HttpClient _http;
    private static bool _inFlight;

    public static void Configure()
    {
        var url = ServerConfiguration.GetOrUpdateSetting("avallone.ai.sidecarUrl", "http://127.0.0.1:8765/");
        _http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(5) };
        CommandSystem.Register("Rumour", AccessLevel.Administrator, OnCommand);
        CommandSystem.Register("Rumor", AccessLevel.Administrator, OnCommand);   // the owner, chat, 2026-11-02: the US spelling
    }

    public static void Initialize()
    {
        Timer.StartTimer(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), Tick);
        Timer.StartTimer(TimeSpan.FromSeconds(5), () =>
        {
            // Startup handshake: deeds are recorded only once the brain has answered the rumour call.
            _inFlight = true;
            _ = TickAsync();
        });

        // Integration testing on a throwaway dev shard only (a copy of the live saves). Never set on the live service.
        if (Environment.GetEnvironmentVariable("AVALLONE_RUMOUR_SELFTEST") == "1")
        {
            logger.Warning("Avallone rumours: SELFTEST - real hooks on a dev copy, clock +0.5 h every 30 s");
            Timer.StartTimer(TimeSpan.FromSeconds(20), SelfTest);
            Timer.StartTimer(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), () =>
            {
                PlayedClock.Advance(0.5);
                _inFlight = true;
                _ = TickAsync();
            });
        }
    }

    private static void SelfTest()
    {
        PlayerMobile racer = null;
        foreach (var m in World.Mobiles.Values)
        {
            if (m is PlayerMobile { AccessLevel: AccessLevel.Player } pm && (racer == null || pm.Name == "Racer X"))
            {
                racer = pm;
            }
        }

        if (racer == null)
        {
            logger.Warning("Avallone rumours: SELFTEST found no player character");
            return;
        }

        // A real dragon, killed through the real OnDeath hook, in Britain's graveyard (Trammel).
        var dragon = new Dragon();
        var at = new Point3D(1384, 1492, Map.Trammel.GetAverageZ(1384, 1492));
        dragon.MoveToWorld(at, Map.Trammel);
        dragon.Damage(dragon.HitsMax * 3, racer);
        Corpse corpse = null;
        foreach (var item in Map.Trammel.GetItemsInRange(at, 3))
        {
            if (item is Corpse c && c.Owner == dragon)
            {
                corpse = c;
            }
        }

        logger.Information(
            "Avallone rumours: SELFTEST dragon killed by {Name} (dead {Dead}); corpse {Corpse} with {Items} item(s)",
            racer.Name, dragon.Deleted || !dragon.Alive, corpse != null, corpse?.Items.Count ?? 0
        );

        // The funny ones, through the same Report path the hooks use, at a real public spot (Racer X is logged out).
        var marker = new Item(0x1) { Visible = false, Movable = false };
        marker.MoveToWorld(at, Map.Trammel);
        Deeds.Report(racer, "died_pathetic", "chicken", at: marker, bypassCooldown: true);
        Deeds.Report(racer, "fished_boots", "old boots", count: 3, at: marker, bypassCooldown: true);
        Deeds.Report(racer, "gm_planted", "", text: "They say the bells under Vesper rang twice at midnight.",
            extra: new() { ["town"] = "Vesper" }, at: marker, bypassCooldown: true);
        marker.Delete();

        // The privacy gate: a deed inside any house is refused.
        foreach (var item in World.Items.Values)
        {
            if (item is Server.Multis.BaseHouse house && house.Map != null && house.Map != Map.Internal)
            {
                var ok = Deeds.Report(racer, "drunk", "", at: house, bypassCooldown: true);
                logger.Information("Avallone rumours: SELFTEST deed inside a house admitted: {Ok} (expected False)", ok);
                break;
            }
        }
    }

    // Once a minute while someone plays: ask the brain which rumours newly reached a town, and log each one.
    private static void Tick()
    {
        if (_inFlight || !Deeds.Enabled || !PlayedClock.AnyPlayerOnline())
        {
            return;
        }

        _inFlight = true;
        _ = TickAsync();
    }

    private static async Task TickAsync()
    {
        try
        {
            var response = await _http.PostAsJsonAsync("v1/rumours/tick", new { pt = PlayedClock.Seconds });
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // An older brain: it would reject deed events and the outbox would drop them (Codex code review
                // 2026-10-28 finding 1). Stop recording deeds until a brain that knows rumours answers again.
                Deeds.SetBrainReady(false, "brain has no rumour support (older brain?)");
                return;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.GetProperty("ok").GetBoolean())
            {
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
            }

            Deeds.SetBrainReady(true, "brain answered the rumour call");
            foreach (var a in doc.RootElement.GetProperty("arrivals").EnumerateArray())
            {
                logger.Information(
                    "Rumour reached {Town}: \"{Text}\" ({Hops} hop(s), id {Id})",
                    a.GetProperty("town").GetString(), a.GetProperty("text").GetString(),
                    a.GetProperty("hops").GetInt32(), a.GetProperty("id").GetString()
                );
            }
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Avallone rumours: tick failed");
        }
        finally
        {
            _inFlight = false;
        }
    }

    private static async Task<JsonDocument> PostAsync(string path, object body)
    {
        var response = await _http.PostAsJsonAsync(path, body);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!doc.RootElement.GetProperty("ok").GetBoolean())
        {
            var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : $"HTTP {(int)response.StatusCode}";
            doc.Dispose();
            throw new InvalidOperationException(err);
        }

        return doc;
    }

    [Usage("Rumour [status|on|off|speak [on|off]|clock [add <hours>]|list [town]|fire <type> [subject]|plant <town> <text>|drop <id>]")]
    [Description("Avallone 5a: the rumour network. list asks the brain what each town has heard now (played clock).")]
    // "[unease=<-1..1>] text" -> (value, text, error). Two decimals at most; anything else is an error, never silently 0.
    internal static (double Value, string Text, string Error) PlantUnease(string text)
    {
        text = (text ?? "").Trim();
        if (!text.StartsWith("unease=", StringComparison.OrdinalIgnoreCase))
        {
            // the owner's dev test 2026-11-02 20:27 CST: was planted as talk WITH that text. A story that
            // starts with "unease" but not "unease=" is a typo - refuse it, never plant it.
            return text.StartsWith("unease", StringComparison.OrdinalIgnoreCase)
                ? (0, "", "Not planted: write unease=0.8 (or unease=-0.8 to calm the town), with = and no space.")
                : (0, text, null);
        }

        var cut = text.IndexOf(' ');
        var raw = (cut < 0 ? text : text[..cut])["unease=".Length..];
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ||
            double.IsNaN(v) || v < -1 || v > 1)
        {
            return (0, "", $"Not planted: unease must be a number from -1 (calming) to 1 (frightening), not \"{raw}\".");
        }

        return (Math.Round(v, 2), cut < 0 ? "" : text[(cut + 1)..].Trim(), null);
    }

    // the owner's dev tests 2026-11-02: (an underscore for the space) and (a typo too).
    // The type is the longest run of leading words within 2 letters of a real type; the rest is the subject.
    internal static (string Type, string Subject) NearestType(string typed)
    {
        var words = (typed ?? "").ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (var n = words.Length; n >= 1; n--)
        {
            var head = string.Join("_", words.Take(n));
            var best = Deeds.FireableTypes.Select(t => (t, d: Distance(head, t))).OrderBy(x => x.d).First();
            if (best.d <= 2)
            {
                return (best.t, string.Join(" ", words.Skip(n)));
            }
        }

        return (null, "");
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
        }

        return d[a.Length, b.Length];
    }

    private static void OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        var verb = e.Length > 0 ? e.GetString(0).ToLowerInvariant() : "status";
        switch (verb)
        {
            case "on" or "off":
                Deeds.SetEnabled(verb == "on");
                from.SendMessage($"Deeds are {(Deeds.Enabled ? "ON" : "OFF")} (saved).");
                break;
            case "speak":
                if (e.Length >= 2 && e.GetString(1).ToLowerInvariant() is "on" or "off")
                {
                    RumourSpeech.SetEnabled(e.GetString(1).ToLowerInvariant() == "on");
                }

                from.SendMessage($"Townsfolk gossip: {RumourSpeech.Describe()} (saved).");
                break;
            case "clock":
                if (e.Length >= 3 && e.GetString(1).ToLowerInvariant() == "add")
                {
                    if (!PlayedClock.Advance(e.GetDouble(2)))
                    {
                        from.SendMessage("Forward only: a number of hours above 0 and up to 1000.");
                        break;
                    }

                    logger.Warning("Avallone rumours: played clock moved forward {Hours} h by {Who}", e.GetDouble(2), from.Name);
                }

                from.SendMessage($"Played clock: {PlayedClock.Hours:F2} h.");
                break;
            case "list":
                _ = ListAsync(from, e.Length > 1 ? e.ArgString[(e.ArgString.IndexOf(' ') + 1)..].Trim() : null);
                break;
            case "fire" when e.Length >= 2:
                var pm = from as PlayerMobile;
                var fireType = e.GetString(1);
                if (!Deeds.FireableTypes.Contains(fireType))
                {   // say why (the owner's dev test 2026-11-02: five silent for kill_notable_dragon and friends)
                    var (near, rest) = NearestType(fireType);
                    from.SendMessage(
                        near != null
                            ? $"Not recorded: did you mean [Rumour fire {near}{(rest.Length > 0 ? " " + rest : "")}?"
                            : $"Not recorded: \"{fireType}\" is not a deed type. [Rumour fire <type> [subject] - types: {string.Join(", ", Deeds.FireableTypes)}"
                    );
                    break;
                }

                var ok = Deeds.Report(pm, fireType, e.Length > 2 ? e.GetString(2) : "", bypassCooldown: true);
                from.SendMessage(ok ? "Deed recorded (as you)." : "Not recorded - the reason is in the shard log (\"Avallone rumours\").");
                break;
            case "plant" when e.Length >= 3:
                var town = e.GetString(1);
                var text = e.ArgString[(e.ArgString.IndexOf(town, StringComparison.Ordinal) + town.Length)..].Trim();
                // 2b (design B3): "[Rumour plant <town> unease=0.5 <text>" - how the story moves the town, -1 calming .. 1
                // frightening; without it the story is talk only (0).
                var (uneaseValue, plantText, uneaseError) = PlantUnease(text);
                if (uneaseError != null)
                {
                    from.SendMessage(uneaseError);
                    break;
                }

                var plantExtra = new System.Collections.Generic.Dictionary<string, object> { ["town"] = town };
                if (uneaseValue != 0)
                {
                    plantExtra["unease"] = uneaseValue;
                }

                var planted = plantText.Length > 0 && Deeds.Report(
                    from as PlayerMobile, "gm_planted", "", text: plantText, bypassCooldown: true, extra: plantExtra
                );
                from.SendMessage(planted
                    ? $"Planted in {town}{(uneaseValue != 0 ? $" (town unease {uneaseValue:+0.##;-0.##})" : "")}."
                    : "Not planted. [Rumour plant <town> [unease=-1..1] <what folk are saying>");
                break;
            case "drop" when e.Length >= 2:
                _ = DropAsync(from, e.GetString(1));
                break;
            default:
                from.SendMessage(
                    $"Deeds {(Deeds.Enabled ? "ON" : "OFF")}; gossip {(RumourSpeech.Enabled ? "ON" : "OFF")}; played clock {PlayedClock.Hours:F2} h; ledger: {AiLedger.Describe()}"
                );
                break;
        }
    }

    private static async Task ListAsync(Mobile from, string town)
    {
        try
        {
            using var doc = await PostAsync("v1/rumours/list", new { pt = PlayedClock.Seconds, town });
            var sb = new StringBuilder();
            var n = 0;
            foreach (var r in doc.RootElement.GetProperty("rumours").EnumerateArray())
            {
                n++;
                from.SendMessage(
                    $"[{r.GetProperty("town").GetString()}] {r.GetProperty("text").GetString()} " +
                    $"(id {r.GetProperty("id").GetString()}, {r.GetProperty("hops").GetInt32()} hop(s))"
                );
            }

            from.SendMessage(n == 0 ? "No live rumours." : $"{n} live rumour(s) at {PlayedClock.Hours:F2} h.");
        }
        catch (Exception ex)
        {
            from.SendMessage("Brain: " + ex.Message);
        }
    }

    private static async Task DropAsync(Mobile from, string id)
    {
        try
        {
            using var doc = await PostAsync("v1/rumours/drop", new { id });
            from.SendMessage($"Dropped {id}.");
            logger.Warning("Avallone rumours: {Who} dropped rumour {Id}", from.Name, id);
        }
        catch (Exception ex)
        {
            from.SendMessage("Brain: " + ex.Message);
        }
    }
}
