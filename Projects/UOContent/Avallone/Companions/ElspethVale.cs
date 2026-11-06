using Server.Avallone.Companions;
using ModernUO.Serialization;
using Server.Items;

namespace Server.Mobiles;

// Elspeth Vale, field healer of Britain - Avallone's first companion (AI plan, Phase 3; the owner, chat, 2026-10-01).
// Lore: Lore/Avallone/People/Elspeth-Vale.md. Persona: brain/personas/elspeth.md. Home spot:
// Data/Avallone/companions.json. She heals her companion with bandages (BaseCreature owner healing) and
// defends herself, but never starts a fight.
[SerializationGenerator(0, false)]
public partial class ElspethVale : BaseCompanion
{
    // Must match the items added in the constructor below (Phase 4e starter record).
    // G5: her own damage whenever she is not holding a weapon a player gave her (same as SetDamage in the constructor).
    public override (int Min, int Max)? OwnDamage => (4, 8);

    public override StarterGear[] StarterLoadout =>
    [
        new(typeof(PlainDress), Layer.OuterTorso, 0x1BB),
        new(typeof(HalfApron), Layer.Waist, 0),
        new(typeof(Sandals), Layer.Shoes, 0x75A),
        new(typeof(Dagger), Layer.OneHanded, 0)
    ];

    // Phase 4e (the owner, chat, 2026-10-20): a field healer turns down plate and great two-handed weapons (staves are fine).
    // Shields are left alone - they count as plate in the stock code but the owner only ruled out plate armour.
    public override GearRefusal RefuseGear(Item item) => item switch
    {
        BaseShield => null,
        BaseArmor { MaterialType: ArmorMaterialType.Plate } => GearRefusal.Item(
            "plate",
            "Elspeth won't wear plate.",
            "you are a field healer and plate is too heavy to kneel and bandage in"
        ),
        BaseWeapon { Layer: Layer.TwoHanded } and not BaseStaff => GearRefusal.Item(
            "two-handed",
            "Elspeth won't carry a great two-handed weapon.",
            "a great two-handed weapon is no use to a healer who needs a free hand for bandages"
        ),
        _ => null
    };

    [Constructible]
    public ElspethVale() : base("elspeth")
    {
        Name = "Elspeth Vale";
        Title = "the field healer";
        Body = 0x191;
        Female = true;
        Hue = Race.Human.RandomSkinHue();
        HairItemID = 0x203D; // pony tail
        HairHue = 1635;      // red, from the hair-dye red range 1602-1654 (0x6A1 came out dark - the owner, 2026-10-01)

        SetStr(70, 75);
        SetDex(70, 75);
        SetInt(80, 85);
        SetHits(140);
        SetDamage(4, 8);

        SetSkill(SkillName.Healing, 90.0);
        SetSkill(SkillName.Anatomy, 90.0);
        SetSkill(SkillName.Fencing, 60.0);
        SetSkill(SkillName.Tactics, 60.0);
        SetSkill(SkillName.Wrestling, 60.0);
        SetSkill(SkillName.MagicResist, 50.0);

        Fame = 0;
        Karma = 2500;
        VirtualArmor = 20;

        AddItem(new PlainDress(0x1BB));
        AddItem(new HalfApron());
        AddItem(new Sandals(0x75A));
        AddItem(new Dagger { Movable = false });
    }

    public override string PersonaKey => "elspeth";

    public override string LeaveLine => "I'll not be part of this. Find yourself another healer.";

    public override string[] FallbackEmotes =>
    [
        "*is busy rolling bandages*",
        "*frowns at a torn sleeve and says nothing*",
        "*counts her supplies under her breath*"
    ];

    // the owner, 2026-10-17: more variety, emotes too, and nothing that reacts to whether he asked or told her something.
    public override string[] ThinkingLines =>
    [
        "Let a healer think.",
        "*counts out a few herbs*",
        "*wraps a strip of linen around one hand*",
        "Hmph. Give me a breath.",
        "*glances along the road*",
        "*straightens a sleeve*",
        "Patience is half of healing.",
        "*sorts through a small pouch*",
        "*frowns at a bruise on your arm*",
        "One thing at a time.",
        "*rubs tired eyes*",
        "Now let me think this through.",
        "*taps the lid of a salve jar*",
        "*smells a sprig of mint*",
        "Hmm. Not yet.",
        "*kneels to retie a boot*",
        "Wait a breath.",
        "*tucks a vial away*",
        "Easy, easy.",
        "*looks you over for cuts*",
        ..CommonThinkingLines
    ];
}
