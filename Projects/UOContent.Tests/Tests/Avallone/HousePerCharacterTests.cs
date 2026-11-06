using Server;
using Server.Accounting;
using Server.Accounting.Security;
using Server.Avallone;
using Server.Mobiles;
using Server.Multis;
using Xunit;

namespace UOContent.Tests;

// the owner, 2026-10-13: one house for each character instead of one for the whole account.
[Collection("Sequential UOContent Tests")]
public class HousePerCharacterTests
{
    private static PlayerMobile NewCharacter(Account account, int slot, int x)
    {
        var m = new PlayerMobile(World.NewMobile);
        m.DefaultMobileInit();
        m.MoveToWorld(new Point3D(x, 1400, 0), Map.Felucca);
        account[slot] = m;
        return m;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LimitAndDecayFollowTheSetting(bool perCharacter)
    {
        var decayEnabled = BaseHouse.DecayEnabled;
        var setting = AvalloneRules.HousePerCharacter;
        // The fixture never runs AccountSecurity.Configure, so the algorithm is None and new Account(...) throws
        // (every run since 2026-10-14). Set one for this test and put it back, as AccountPasswordTests does.
        var algorithm = AccountSecurity.CurrentAlgorithm;
        AccountSecurity.CurrentAlgorithm = PasswordProtectionAlgorithm.SHA2;
        BaseHouse.DecayEnabled = true;
        AvalloneRules.HousePerCharacter = perCharacter;

        var account = new Account($"house-test-{perCharacter}", "house-test-password");
        var first = NewCharacter(account, 0, 1400);
        var second = NewCharacter(account, 1, 1440);
        var third = NewCharacter(account, 2, 1480);

        var older = new SmallOldHouse(first, 0x64);
        var newer = new SmallOldHouse(second, 0x64);

        try
        {
            older.MoveToWorld(new Point3D(1400, 1400, 0), Map.Felucca);
            newer.MoveToWorld(new Point3D(1440, 1400, 0), Map.Felucca);
            older.BuiltOn = Core.Now - System.TimeSpan.FromDays(30);
            newer.BuiltOn = Core.Now;

            Assert.Same(account, first.Account);
            Assert.True(BaseHouse.HasAccountHouse(first));
            Assert.True(BaseHouse.HasAccountHouse(second));

            // The character with no house: free to place one only when the limit is per character.
            Assert.Equal(!perCharacter, BaseHouse.HasAccountHouse(third));

            // The account's newest house always refreshes itself; the older one only with the setting on.
            Assert.Equal(DecayType.AutoRefresh, newer.DecayType);
            Assert.Equal(perCharacter ? DecayType.AutoRefresh : DecayType.ManualRefresh, older.DecayType);
        }
        finally
        {
            older.Delete();
            newer.Delete();
            first.Delete();
            second.Delete();
            third.Delete();
            account.Delete();
            BaseHouse.DecayEnabled = decayEnabled;
            AvalloneRules.HousePerCharacter = setting;
            AccountSecurity.CurrentAlgorithm = algorithm;
        }
    }
}
