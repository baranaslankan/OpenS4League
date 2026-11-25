using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenS4L.Common.Configuration;
using OpenS4L.Database;
using OpenS4L.Database.Auth;
using OpenS4L.Database.Game;
using OpenS4L.Network.Message.Game;
using OpenS4L.Server.Game;
using OpenS4L.Server.Game.Handlers;
using ProudNet;
using Xunit;
using Constants = OpenS4L.Common.Constants;

namespace OpenS4L.Server.Mapping.Tests
{
    /// <summary>
    /// Nickname change / cancel and record reset items (ItemUseHandler) and the temporary nickname
    /// restore at login.
    /// </summary>
    public class GameItemUseTests
    {
        private readonly GameTestContext _ctx = new GameTestContext();

        public GameItemUseTests()
        {
            _ctx.Get<IOptions<GameOptions>>().Value.NickRestrictions = new NickRestrictionOptions
            {
                MinLength = 4, MaxLength = 16, MaxRepeat = 3, WhitespaceAllowed = false, AsciiOnly = true
            };
            foreach (var number in new uint[] { 4000001, 4000002, 4000004, 4010001 })
                GameFixtures.SeedShopItem(_ctx.GameData, (ItemNumber)number);
        }

        private async Task SeedAccountAsync(uint accountId, string nickname)
        {
            using (var auth = _ctx.Get<AuthContext>())
            {
                auth.Accounts.Add(new AccountEntity { Id = (int)accountId, Username = "g" + accountId, Nickname = nickname });
                await auth.SaveChangesAsync();
            }
            using (var db = _ctx.Get<GameContext>())
            {
                db.Players.Add(new PlayerEntity { Id = (int)accountId, TotalExperience = 0 });
                await db.SaveChangesAsync();
            }
        }

        private async Task<(Player plr, FakeSocketChannel channel)> LoginAsync(uint accountId)
        {
            var cache = (Foundatio.Caching.InMemoryCacheClient)_ctx.Get<Foundatio.Caching.ICacheClient>();
            await cache.SetAsync<string>(Constants.Cache.SessionKey(accountId), "sid-" + accountId);
            var (session, channel) = _ctx.CreateSession(accountId);
            await _ctx.Get<AuthenticationHandler>().OnHandle(new MessageContext { Session = session }, new LoginRequestReqMessage
            {
                AccountId = accountId, SessionId = "sid-" + accountId, Version = new Version(1, 0, 0, 0)
            });
            Assert.NotNull(session.Player);
            return (session.Player, channel);
        }

        private static T LastSent<T>(FakeSocketChannel channel)
        {
            return channel.Outbound
                .Select(o => o.GetType().GetProperty("Message")?.GetValue(o))
                .OfType<T>()
                .LastOrDefault();
        }

        private static PlayerItem Give(Player plr, uint itemNumber)
        {
            return plr.Inventory.Create((ItemNumber)itemNumber, ItemPriceType.PEN, ItemPeriodType.None, 0, 0,
                Array.Empty<uint>(), 1, false);
        }

        private async Task<string> DbNicknameAsync(uint accountId)
        {
            using (var auth = _ctx.Get<AuthContext>())
                return (await auth.Accounts.FirstAsync(x => x.Id == (int)accountId)).Nickname;
        }

        private async Task<uint> ChangeNickAsync(Player plr, FakeSocketChannel channel, PlayerItem item, string nickname)
        {
            await _ctx.Get<ItemUseHandler>().OnHandle(new MessageContext { Session = plr.Session },
                new ItemUseChangeNickReqMessage { ItemId = item.Id, Nickname = nickname });
            return LastSent<ItemUseChangeNickAckMessage>(channel).Result;
        }

        [Fact]
        public async Task ChangeNick_permanent_changesNickname_andUsesItem()
        {
            await SeedAccountAsync(9801, "OldNick");
            await SeedAccountAsync(9802, "TakenNick");
            var (plr, channel) = await LoginAsync(9801);
            var item = Give(plr, 4000001);

            Assert.Equal(ItemUseHandler.ResultTooShort, await ChangeNickAsync(plr, channel, item, "abc"));
            Assert.Equal(ItemUseHandler.ResultInvalidCharacters, await ChangeNickAsync(plr, channel, item, "bad name"));
            Assert.Equal(ItemUseHandler.ResultFailed, await ChangeNickAsync(plr, channel, item, "TakenNick"));
            Assert.NotNull(plr.Inventory[item.Id]);

            Assert.Equal(ItemUseHandler.ResultOK, await ChangeNickAsync(plr, channel, item, "NewNick"));
            Assert.Equal("NewNick", LastSent<ItemUseChangeNickAckMessage>(channel).Unk3);
            Assert.Equal("NewNick", plr.Account.Nickname);
            Assert.Equal("NewNick", await DbNicknameAsync(9801));
            Assert.Null(plr.Inventory[item.Id]);

            // The old nickname is free again after a permanent change
            Assert.False(await Nicknames.IsTakenAsync(_ctx.Get<DatabaseService>(), "OldNick"));
        }

        [Fact]
        public async Task ChangeNick_temporary_canBeCancelled()
        {
            await SeedAccountAsync(9811, "OldNick");
            var (plr, channel) = await LoginAsync(9811);
            var item = Give(plr, 4000004); // 7 days

            Assert.Equal(ItemUseHandler.ResultOK, await ChangeNickAsync(plr, channel, item, "TempNick"));
            Assert.Equal("TempNick", await DbNicknameAsync(9811));
            // The old nickname stays reserved while the temporary one is in use
            Assert.True(await Nicknames.IsTakenAsync(_ctx.Get<DatabaseService>(), "OldNick"));

            // No second change while one is pending
            var second = Give(plr, 4000001);
            Assert.Equal(ItemUseHandler.ResultAlreadyUsed, await ChangeNickAsync(plr, channel, second, "OtherNick"));
            Assert.NotNull(plr.Inventory[second.Id]);

            var cancel = Give(plr, 4000002);
            await _ctx.Get<ItemUseHandler>().OnHandle(new MessageContext { Session = plr.Session },
                new ItemUseChangeNickCancelReqMessage());
            Assert.Equal(ItemUseHandler.ResultOK, LastSent<ItemUseChangeNickCancelAckMessage>(channel).Unk);
            Assert.Equal("OldNick", plr.Account.Nickname);
            Assert.Equal("OldNick", await DbNicknameAsync(9811));
            Assert.Null(plr.Inventory[cancel.Id]);

            // Nothing left to cancel
            Give(plr, 4000002);
            await _ctx.Get<ItemUseHandler>().OnHandle(new MessageContext { Session = plr.Session },
                new ItemUseChangeNickCancelReqMessage());
            Assert.Equal(ItemUseHandler.ResultAlreadyUsed, LastSent<ItemUseChangeNickCancelAckMessage>(channel).Unk);
        }

        [Fact]
        public async Task Login_restoresExpiredTemporaryNickname()
        {
            await SeedAccountAsync(9821, "TempNick");
            using (var auth = _ctx.Get<AuthContext>())
            {
                auth.Nicknames.Add(new NicknameHistoryEntity
                {
                    AccountId = 9821, Nickname = "OldNick",
                    ExpireDate = DateTimeOffset.Now.AddMinutes(-1).ToUnixTimeSeconds()
                });
                await auth.SaveChangesAsync();
            }

            var (plr, _) = await LoginAsync(9821);
            Assert.Equal("OldNick", plr.Account.Nickname);
            Assert.Equal("OldNick", await DbNicknameAsync(9821));
            Assert.False(await Nicknames.HasPendingChangeAsync(_ctx.Get<DatabaseService>(), 9821));
        }

        [Fact]
        public async Task RecordReset_usesItem()
        {
            await SeedAccountAsync(9831, "Resetter");
            var (plr, channel) = await LoginAsync(9831);
            var wrong = Give(plr, 4000001);
            var item = Give(plr, 4010001);

            await _ctx.Get<ItemUseHandler>().OnHandle(new MessageContext { Session = plr.Session },
                new ItemUseRecordResetReqMessage { ItemId = wrong.Id });
            Assert.Equal(ItemUseHandler.ResultFailed, LastSent<ItemUseRecordResetAckMessage>(channel).Result);
            Assert.NotNull(plr.Inventory[wrong.Id]);

            await _ctx.Get<ItemUseHandler>().OnHandle(new MessageContext { Session = plr.Session },
                new ItemUseRecordResetReqMessage { ItemId = item.Id });
            Assert.Equal(ItemUseHandler.ResultOK, LastSent<ItemUseRecordResetAckMessage>(channel).Result);
            Assert.Null(plr.Inventory[item.Id]);
        }
    }
}
