using ModernUO.Serialization;
using Server.Items;

namespace Server.Mobiles;

// Bram Holloway, sellsword of Britain, saving for a farm - Avallone's first wanderer (wanderer prototype, 2026-10-14;
// Claude picked him at the owner's request, ). Lore: Lore/Avallone/People/Bram-Holloway.md (draft).
// Persona: brain/personas/bram.md. Placed with [Wanderer spawn (at the Britain inn).
[SerializationGenerator(0, false)]
public partial class BramHolloway : BaseWanderer
{
    [Constructible]
    public BramHolloway()
    {
        Name = "Bram Holloway";
        Title = "the sellsword";
        Body = 0x190;
        Female = false;
        Hue = Race.Human.RandomSkinHue();
        HairItemID = 0x203B; // short hair
        HairHue = 1149;
        FacialHairItemID = 0x204B; // short beard
        FacialHairHue = 1149;

        SetStr(95, 100);
        SetDex(75, 80);
        SetInt(45, 50);
        SetHits(260);
        SetDamage(9, 16);

        SetSkill(SkillName.Swords, 90.0);
        SetSkill(SkillName.Tactics, 90.0);
        SetSkill(SkillName.Anatomy, 80.0);
        SetSkill(SkillName.Parry, 70.0);
        SetSkill(SkillName.MagicResist, 60.0);
        SetSkill(SkillName.Wrestling, 60.0);

        Fame = 1000;
        Karma = 2500;
        VirtualArmor = 30;

        AddItem(new Broadsword { Movable = false });
        AddItem(new WoodenKiteShield { Movable = false });
        AddItem(new RingmailChest { Movable = false });
        AddItem(new LeatherLegs { Movable = false });
        AddItem(new Boots { Movable = false });
        AddItem(new Cloak(0x1BB) { Movable = false });
    }

    public override string PersonaKey => "bram";
}
