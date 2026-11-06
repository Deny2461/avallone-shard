using System.Text.Json.Serialization;
using System;
using Server.Logging;
using Server.Regions;
using Server.Mobiles;

namespace Server.Engines.Quests;

public class QuestOfferRegion : BaseRegion
{
    [JsonConstructor] // Don't include parent, since it is special (restored for Avallone SE, 2026-09-30)
    public QuestOfferRegion(string name, Map map, int priority, params Rectangle3D[] area) : base(name, map, priority, area)
    {
    }

    private static readonly ILogger logger = LogFactory.GetLogger(typeof(QuestOfferRegion));

    public QuestOfferRegion(string name, Map map, Region parent, int priority, params Rectangle3D[] area) : base(name, map, parent, priority, area)
    {
    }

    public QuestOfferRegion(string name, Map map, Region parent, params Rectangle3D[] area) : base(name, map, parent, area)
    {
    }

    public Type Quest { get; set; }

    public override void OnEnter(Mobile m)
    {
        base.OnEnter(m);

        if (Quest == null)
        {
            return;
        }

        if (m is PlayerMobile player && player.Quest == null && QuestSystem.CanOfferQuest(m, Quest))
        {
            try
            {
                var qs = Quest.CreateInstance<QuestSystem>(player);
                qs.SendOffer();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error creating quest {Quest}", Quest);
            }
        }
    }
}
