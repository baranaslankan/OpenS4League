using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using OpenS4L;
using OpenS4L.Common;
using OpenS4L.Database;
using OpenS4L.Database.Auth;
using OpenS4L.Database.Game;
using OpenS4L.Network.Message.Game;
using OpenS4L.Server.Game;
using OpenS4L.Server.Game.Handlers;
using OpenS4L.Server.Game.Services;
using ProudNet;
using Xunit;
using Constants = OpenS4L.Common.Constants;

namespace OpenS4L.Server.Mapping.Tests
{
    /// <summary>
    /// Exercises the Administrator player commands (pen, ap, exp, level, item) via CommandService.Execute.
    /// </summary>
    public class PlayerCommandTests
    {
        private readonly GameTestContext _ctx = new GameTestContext();

        private async Task<Player> LoginAsync(uint accountId, SecurityLevel level = SecurityLevel.User)
        {
            var cache = (Foundatio.Caching.InMemoryCacheClient)_ctx.Get<Foundatio.Caching.ICacheClient>();
            await cache.SetAsync<string>(Constants.Cache.SessionKey(accountId), "sid-" + accountId);
            using (var auth = _ctx.Get<AuthContext>())
            {
                auth.Accounts.Add(new AccountEntity { Id = (int)accountId, Username = "g" + accountId, Nickname = "nick" + accountId, SecurityLevel = (byte)level });
                await auth.SaveChangesAsync();
            }
            using (var db = _ctx.Get<GameContext>())
            {
                db.Players.Add(new PlayerEntity { Id = (int)accountId, TotalExperience = 0 });
                await db.SaveChangesAsync();
            }

            var handler = _ctx.Get<AuthenticationHandler>();
            var (session, _) = _ctx.CreateSession(accountId);
            await handler.OnHandle(new MessageContext { Session = session }, new LoginRequestReqMessage
            {
                AccountId = accountId, SessionId = "sid-" + accountId, Version = new Version(1, 0, 0, 0)
            });
            return session.Player;
        }

        private async Task<(Player admin, CommandService cmd)> SetupAsync(uint id)
        {
            var admin = await LoginAsync(id, SecurityLevel.Administrator);
            var cmd = _ctx.Get<CommandService>();
            await cmd.StartAsync(System.Threading.CancellationToken.None);
            return (admin, cmd);
        }

        [Fact]
        public async Task Pen_setsOwnPEN()
        {
            var (admin, cmd) = await SetupAsync(8101);
            Assert.True(await cmd.Execute(admin, new[] { "pen", "999999" }));
            Assert.Equal(999999u, admin.PEN);
        }

        [Fact]
        public async Task Ap_setsTargetAPByNickname()
        {
            var (admin, cmd) = await SetupAsync(8102);
            var other = await LoginAsync(8103);
            Assert.True(await cmd.Execute(admin, new[] { "ap", "500", "nick8103" }));
            Assert.Equal(500u, other.AP);
            Assert.Equal(0u, admin.AP);
        }

        [Fact]
        public async Task Level_setsExperienceToLevelStart()
        {
            var (admin, cmd) = await SetupAsync(8104);
            Assert.True(await cmd.Execute(admin, new[] { "level", "1" }));
            Assert.Equal(1, admin.Level);
            Assert.Equal(100u, admin.TotalExperience);
        }

        [Fact]
        public async Task Level_unknownLevel_leavesExperience()
        {
            var (admin, cmd) = await SetupAsync(8105);
            Assert.True(await cmd.Execute(admin, new[] { "level", "99" }));
            Assert.Equal(0u, admin.TotalExperience);
        }

        [Fact]
        public async Task Exp_addsExperienceAndLevelsUp()
        {
            var (admin, cmd) = await SetupAsync(8106);
            Assert.True(await cmd.Execute(admin, new[] { "exp", "150" }));
            Assert.Equal(150u, admin.TotalExperience);
            Assert.Equal(1, admin.Level);
        }

        [Fact]
        public async Task Item_givesShopItem()
        {
            var (admin, cmd) = await SetupAsync(8107);
            var number = new ItemNumber(ItemCategory.Weapon, 1, 1);
            var before = admin.Inventory.Count(x => x.ItemNumber == number);
            Assert.True(await cmd.Execute(admin, new[] { "item", number.Id.ToString() }));
            Assert.Equal(before + 1, admin.Inventory.Count(x => x.ItemNumber == number));
        }

        [Fact]
        public async Task Item_unknownItem_givesNothing()
        {
            var (admin, cmd) = await SetupAsync(8108);
            var before = admin.Inventory.Count();
            Assert.True(await cmd.Execute(admin, new[] { "item", "123" }));
            Assert.Equal(before, admin.Inventory.Count());
        }

        [Fact]
        public async Task Pen_badArgs_returnsFalse()
        {
            var (admin, cmd) = await SetupAsync(8109);
            Assert.False(await cmd.Execute(admin, new[] { "pen", "lots" }));
        }

        [Fact]
        public async Task Pen_gameMasterLacksPermission()
        {
            var gm = await LoginAsync(8110, SecurityLevel.GameMaster);
            var cmd = _ctx.Get<CommandService>();
            await cmd.StartAsync(System.Threading.CancellationToken.None);
            Assert.False(await cmd.Execute(gm, new[] { "pen", "1" }));
            Assert.Equal(0u, gm.PEN);
        }

        [Fact]
        public async Task Pen_offlineTarget_returnsFalse()
        {
            var (admin, cmd) = await SetupAsync(8111);
            Assert.False(await cmd.Execute(admin, new[] { "pen", "5", "nobodyhere" }));
            Assert.Equal(0u, admin.PEN);
        }

        [Fact]
        public async Task Pen_fromConsoleWithoutTarget_returnsFalse()
        {
            var (_, cmd) = await SetupAsync(8112);
            Assert.False(await cmd.Execute(null, new[] { "pen", "5" }));
        }
    }
}
