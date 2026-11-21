using System;
using System.Collections.Generic;

namespace Server.Avallone.AI;

// Stable AI IDs for mobiles (NPCs and player characters). The AI's memory is keyed by these, never by
// serials or account names, so memory is per character and survives anything that renumbers serials.
//
// Saved as its own file in world saves ("AvalloneAi"). Never delete this type once it is in saves —
// retire it in place (plan v2, save compatibility).
public class AiIdentity : GenericPersistence
{
    private const int Version = 0;

    private static AiIdentity _instance;
    private static readonly Dictionary<Mobile, Guid> _ids = [];
    private static readonly Dictionary<Guid, Mobile> _byId = []; // Phase 5b: id -> character, for the current name

    // Townsfolk step (Codex code R1 F2): ids that were loaded with MORE THAN ONE owner. The reverse map can hold only one
    // of them, so anything that asks "who else holds this id?" must look at every forward entry for these.
    private static readonly HashSet<Guid> _shared = [];

    public AiIdentity() : base("AvalloneAi", 10)
    {
    }

    public static int Count => _ids.Count;

    public static void Configure()
    {
        _instance = new AiIdentity();
    }

    public static Guid GetOrCreate(Mobile m)
    {
        if (m == null || m.Deleted)
        {
            return Guid.Empty;
        }

        // Townsfolk step (design 3.4a, Quarantine): an NPC whose seat ownership is unresolved has no AI id made or used.
        if (Server.Avallone.Townsfolk.Seats.IsQuarantined(m))
        {
            return Guid.Empty;
        }

        if (!_ids.TryGetValue(m, out var id))
        {
            id = Guid.NewGuid();
            _ids[m] = id;
            _byId[id] = m;
        }

        return id;
    }

    public static bool TryGet(Mobile m, out Guid id) => _ids.TryGetValue(m, out id);

    // Townsfolk step (design 3.3): give a seat's AI id to its occupant. One operation over both maps. Refuses - and
    // changes nothing - when the id is held by a player (ever) or by another live Mobile, or when this Mobile already
    // carries a different id. A deleted NPC holder is retired first, so an id never has two owners.
    public static bool Rebind(Mobile m, Guid id)
    {
        if (m == null || m.Deleted || id == Guid.Empty)
        {
            return false;
        }

        if (!IsSoleHolder(m, id))
        {
            return false; // someone else holds it - including an owner the reverse map does not show (F2)
        }

        if (_ids.TryGetValue(m, out var mine) && mine != id)
        {
            return false;
        }

        if (_byId.TryGetValue(id, out var holder) && holder != m)
        {
            if (holder is Server.Mobiles.PlayerMobile || !holder.Deleted)
            {
                return false;
            }

            _ids.Remove(holder);
        }

        _ids[m] = id;
        _byId[id] = m;
        return true;
    }

    // Townsfolk step (Codex code R1 F2): true when nobody but m holds this id - no player (ever, even deleted) and no
    // other Mobile that is not deleted. Looks at EVERY forward entry when the id was loaded with several owners, so the
    // answer does not depend on which owner the reverse map happens to show.
    public static bool IsSoleHolder(Mobile m, Guid id)
    {
        if (id == Guid.Empty)
        {
            return false;
        }

        if (_byId.TryGetValue(id, out var shown) && shown != m && (shown is Server.Mobiles.PlayerMobile || !shown.Deleted))
        {
            return false;
        }

        if (!_shared.Contains(id))
        {
            return true;
        }

        foreach (var (other, held) in _ids)
        {
            if (held == id && other != m && (other is Server.Mobiles.PlayerMobile || !other.Deleted))
            {
                return false;
            }
        }

        return true;
    }

    // Townsfolk step (Codex code R2 #3): EVERY Mobile that holds this id. One for an ordinary id (the reverse map);
    // every forward owner for an id that was loaded with several - never just the one the reverse map happens to show.
    public static IEnumerable<Mobile> Holders(Guid id)
    {
        if (id == Guid.Empty)
        {
            yield break;
        }

        if (!_shared.Contains(id))
        {
            if (_byId.TryGetValue(id, out var only))
            {
                yield return only;
            }

            yield break;
        }

        foreach (var (m, held) in _ids)
        {
            if (held == id)
            {
                yield return m;
            }
        }
    }

    // Are there any ids with more than one owner at all? (Almost always no: lets callers skip the question cheaply.)
    public static bool HasShared => _shared.Count > 0;

    private static void Load(Mobile m, Guid id)
    {
        if (_byId.TryGetValue(id, out var first) && first != m)
        {
            _shared.Add(id); // a second owner of one id: remembered, never silently overwritten
        }

        _ids[m] = id;
        _byId[id] = m;
    }

    // Tests only: put entries in as a world save would, in this order.
    internal static void LoadForTests(params (Mobile Mobile, Guid Id)[] entries)
    {
        foreach (var (m, id) in entries)
        {
            Load(m, id);
        }
    }

    // Townsfolk step: a deleted seat occupant gives up its id, so the seat's next occupant can carry it.
    public static void Release(Mobile m)
    {
        if (m != null && _ids.Remove(m, out var id) && _byId.TryGetValue(id, out var holder) && holder == m)
        {
            _byId.Remove(id);
        }
    }

    // Townsfolk step: every id held by a Mobile that is not deleted, for the load reconcile's checks.
    public static List<(Guid Id, Mobile Holder)> Snapshot()
    {
        var list = new List<(Guid, Mobile)>(_ids.Count);

        foreach (var (m, id) in _ids)
        {
            if (!m.Deleted)
            {
                list.Add((id, m));
            }
        }

        return list;
    }

    // Only a live, non-deleted player character counts; anything else (deleted, unknown, an NPC) is "gone".
    public static bool TryGetPlayer(Guid id, out Server.Mobiles.PlayerMobile pm)
    {
        pm = _byId.TryGetValue(id, out var m) ? m as Server.Mobiles.PlayerMobile : null;
        if (pm is { Deleted: false })
        {
            return true;
        }

        pm = null;
        return false;
    }

    public override void Serialize(IGenericWriter writer)
    {
        writer.WriteEncodedInt(Version);

        var count = 0;
        foreach (var m in _ids.Keys)
        {
            if (!m.Deleted)
            {
                count++;
            }
        }

        writer.WriteEncodedInt(count);
        foreach (var (m, id) in _ids)
        {
            if (!m.Deleted)
            {
                writer.Write(m);
                writer.Write(id);
            }
        }
    }

    public override void Deserialize(IGenericReader reader)
    {
        reader.ReadEncodedInt(); // version

        var count = reader.ReadEncodedInt();
        for (var i = 0; i < count; i++)
        {
            var m = reader.ReadEntity<Mobile>();
            var id = reader.ReadGuid();

            if (m != null)
            {
                Load(m, id);
            }
        }
    }
}
