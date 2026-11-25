using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenS4L;
using OpenS4L.Blub.Serialization;
using OpenS4L.Database;
using OpenS4L.Database.Auth;
using OpenS4L.Database.Game;
using OpenS4L.Network.Data.Game;
using OpenS4L.Network.Message.Game;
using OpenS4L.Network.Serializers;
using OpenS4L.Server.Game;
using OpenS4L.Server.Game.Data;
using OpenS4L.Server.Game.Handlers;
using OpenS4L.Server.Game.Mappers;
using OpenS4L.Server.Game.Services;
using ProudNet;
using Xunit;
using Constants = OpenS4L.Common.Constants;

namespace OpenS4L.Server.Mapping.Tests
{
    /// <summary>
    /// Hour/day period items: the expiry sent to the client, expiry at login and while online,
    /// and capsule/admin grants keeping the requested period.
    /// </summary>
    public class ItemExpiryTests
    {
        private const int PriceGroup = 40;
        private static readonly ItemNumber Sword = new ItemNumber(2010005u);

        private readonly GameTestContext _ctx = new GameTestContext();
        private readonly GameMapper _mapper = new GameMapper();

        public ItemExpiryTests()
        {
            GameFixtures.SeedPeriodPriceGroup(_ctx.GameData, PriceGroup,
                (ItemPeriodType.None, 0), (ItemPeriodType.Days, 7), (ItemPeriodType.Days, 30), (ItemPeriodType.Hours, 2));
            GameFixtures.SeedShopItem(_ctx.GameData, Sword, PriceGroup);
        }

        private static int PriceId(int index) => PriceGroup * 100 + index;

        private ShopPrice Price(ItemPeriodType type, ushort period) =>
            _ctx.GameData.GetShopItemInfo(Sword, ItemPriceType.PEN).PriceGroup.GetPrice(type, period);

        private PlayerItem NewItem(ItemPeriodType type, ushort period, DateTimeOffset purchaseDate)
        {
            var inventory = _ctx.Get<PlayerInventory>();
            return new PlayerItem(_ctx.GameData, inventory, 1, _ctx.GameData.GetShopItemInfo(Sword, ItemPriceType.PEN),
                Price(type, period), 0, Array.Empty<uint>(), purchaseDate);
        }

        [Fact]
        public void ExpireDate_daysAndHours()
        {
            var bought = DateTimeOffset.FromUnixTimeSeconds(1700000000);

            Assert.Equal(bought.AddDays(30), NewItem(ItemPeriodType.Days, 30, bought).ExpireDate);
            Assert.Equal(bought.AddHours(2), NewItem(ItemPeriodType.Hours, 2, bought).ExpireDate);
            var permanent = NewItem(ItemPeriodType.None, 0, bought);
            Assert.Equal(DateTimeOffset.MinValue, permanent.ExpireDate);
            Assert.False(permanent.HasExpireDate);
            Assert.False(permanent.IsExpiredAt(DateTimeOffset.MaxValue));

            var day = NewItem(ItemPeriodType.Days, 7, bought);
            Assert.False(day.IsExpiredAt(bought.AddDays(7).AddSeconds(-1)));
            Assert.True(day.IsExpiredAt(bought.AddDays(7)));
        }

        [Fact]
        public void ToItemDto_sendsExpiryAsUnixSeconds()
        {
            var bought = DateTimeOffset.FromUnixTimeSeconds(1700000000);

            Assert.Equal(-1L, _mapper.ToItemDto(NewItem(ItemPeriodType.None, 0, bought)).ExpireTime.ToUnixTimeSeconds());
            var day = _mapper.ToItemDto(NewItem(ItemPeriodType.Days, 30, bought));
            Assert.Equal(1700000000L + 30 * 86400, day.ExpireTime.ToUnixTimeSeconds());
            Assert.Equal(2400, day.Durability); // day items keep their durability

            // On the wire ExpireTime is the int64 the client hands to _localtime64_s.
            var serializer = new BlubSerializer();
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                new UnixTimeSerializer().Serialize(serializer, w, day.ExpireTime);
            Assert.Equal(1700000000L + 30 * 86400, BitConverter.ToInt64(ms.ToArray(), 0));
        }

        [Fact]
        public void ToItemDto_hourItemsSendRemainingSecondsAsDurability()
        {
            var running = _mapper.ToItemDto(NewItem(ItemPeriodType.Hours, 2, DateTimeOffset.Now.AddMinutes(-30)));
            Assert.InRange(running.Durability, 90 * 60 - 5, 90 * 60 + 1);

            var over = _mapper.ToItemDto(NewItem(ItemPeriodType.Hours, 2, DateTimeOffset.Now.AddHours(-3)));
            Assert.Equal(0, over.Durability);
        }

        [Fact]
        public async Task CreateFromShop_keepsTheRequestedDayPeriod()
        {
            var (plr, _) = await LoginAsync(9701);

            var item = plr.Inventory.CreateFromShop(Sword, ItemPeriodType.Days, 30);
            Assert.Equal(ItemPeriodType.Days, item.PeriodType);
            Assert.Equal(30, item.Period);
            Assert.Equal(PriceId(2), item.GetShopPrice().Id);
            Assert.InRange(item.ExpireDate, DateTimeOffset.Now.AddDays(30).AddMinutes(-1), DateTimeOffset.Now.AddDays(30));

            // A period the shop doesn't offer takes the nearest timed price, never permanent.
            var fallback = plr.Inventory.CreateFromShop(Sword, ItemPeriodType.Days, 5);
            Assert.Equal(ItemPeriodType.Days, fallback.PeriodType);
        }

        [Fact]
        public async Task Online_expiredItemIsUnequippedAndDeleted()
        {
            var (plr, channel) = await LoginAsync(9702);
            var (character, _) = plr.CharacterManager.Create(0, CharacterGender.Male, 0, 0, 0, 0, 0, 0);
            var item = plr.Inventory.CreateFromShop(Sword, ItemPeriodType.Days, 7);
            Assert.Equal(CharacterInventoryError.OK, character.Equip(item, (byte)WeaponSlot.Weapon1));

            Assert.Empty(plr.Inventory.UpdateExpiredItems(DateTimeOffset.Now));

            var later = DateTimeOffset.Now.AddDays(8);
            var changed = plr.Inventory.UpdateExpiredItems(later);
            Assert.Same(item, Assert.Single(changed));
            Assert.Null(item.CharacterInventory);
            Assert.DoesNotContain(item, character.Weapons.GetItems());
            Assert.DoesNotContain(plr.Inventory, x => x.Id == item.Id);

            var messages = Messages(channel);
            Assert.Contains(messages.OfType<ItemUseItemAckMessage>(), m => m.ItemId == item.Id && m.Action == UseItemAction.UnEquip);
            Assert.Contains(messages.OfType<ItemInventroyDeleteAckMessage>(), m => m.ItemId == item.Id);

            Assert.Empty(plr.Inventory.UpdateExpiredItems(later.AddMinutes(1)));
        }

        [Fact]
        public async Task ExpiryService_checksOnlinePlayersInTheLobby()
        {
            var (plr, _) = await LoginAsync(9703);
            var (character, _) = plr.CharacterManager.Create(0, CharacterGender.Male, 0, 0, 0, 0, 0, 0);
            var item = plr.Inventory.CreateFromShop(Sword, ItemPeriodType.Days, 7);
            character.Equip(item, (byte)WeaponSlot.Weapon1);

            var service = new ItemExpiryService(new Logging.Logger<ItemExpiryService>(),
                Microsoft.Extensions.Options.Options.Create(new AppOptions()),
                _ctx.Get<ProudNet.Hosting.Services.ISchedulerService>(), _ctx.Get<PlayerManager>());
            service.CheckPlayers(DateTimeOffset.Now.AddDays(8));

            Assert.Null(item.CharacterInventory);
            Assert.True(character.IsDirty); // persisted by the write-behind save, not here
        }

        [Fact]
        public async Task ExpiredItem_isDroppedWhenLoadedAndQueuedForDelete()
        {
            var (plr, _) = await LoginAsync(9704);
            var item = SeedItem(9704, PriceId(1), DateTimeOffset.Now.AddDays(-8), plr);

            Assert.Null(item);
            Assert.True(plr.Inventory.HasPendingChanges);
        }

        [Fact]
        public async Task Login_expiredEquippedItemIsDeletedAndNotListed()
        {
            const uint accountId = 9705;
            var bought = DateTimeOffset.Now.AddDays(-40);
            using (var db = _ctx.Get<GameContext>())
            {
                db.Players.Add(new PlayerEntity
                {
                    Id = (int)accountId, TotalExperience = 1000,
                    Items = new List<PlayerItemEntity>
                    {
                        new PlayerItemEntity
                        {
                            Id = 970501, PlayerId = (int)accountId, ShopItemInfoId = (int)Sword.Id + 1000,
                            ShopPriceId = PriceId(2), Effects = "[]", PurchaseDate = bought.ToUnixTimeSeconds(),
                            Durability = 2400
                        },
                        new PlayerItemEntity
                        {
                            Id = 970502, PlayerId = (int)accountId, ShopItemInfoId = (int)Sword.Id + 1000,
                            ShopPriceId = PriceId(0), Effects = "[]", PurchaseDate = bought.ToUnixTimeSeconds(),
                            Durability = 2400
                        }
                    },
                    Characters = new List<PlayerCharacterEntity>
                    {
                        new PlayerCharacterEntity
                        {
                            Id = 970510, PlayerId = (int)accountId, Slot = 0, Gender = (byte)CharacterGender.Male,
                            Weapon1Id = 970501, Weapon2Id = 970502
                        }
                    }
                });
                await db.SaveChangesAsync();
            }

            var (plr, channel) = await LoginAsync(accountId, seedPlayer: false);

            var character = plr.CharacterManager.GetCharacter(0);
            Assert.Null(plr.Inventory[970501]);
            Assert.Null(character.Weapons.GetItems()[(int)WeaponSlot.Weapon1]);
            Assert.Same(plr.Inventory[970502], character.Weapons.GetItems()[(int)WeaponSlot.Weapon2]);

            var list = Messages(channel).OfType<ItemInventoryInfoAckMessage>().Single();
            Assert.DoesNotContain(list.Items, x => x.Id == 970501);
            Assert.Equal(-1L, list.Items.Single(x => x.Id == 970502).ExpireTime.ToUnixTimeSeconds());

            // Already handled at login: the online check has nothing left to expire.
            Assert.Empty(plr.Inventory.UpdateExpiredItems(DateTimeOffset.Now));
        }

        private static PlayerItem SeedItem(uint accountId, int priceId, DateTimeOffset purchaseDate, Player plr)
        {
            // Inventory.Create stamps "now"; build the item through the load path for a past purchase.
            var entity = new PlayerItemEntity
            {
                Id = accountId * 100 + 1, PlayerId = (int)accountId, ShopItemInfoId = (int)Sword.Id + 1000,
                ShopPriceId = priceId, Effects = "[]", PurchaseDate = purchaseDate.ToUnixTimeSeconds(), Durability = 2400
            };
            plr.Inventory.Initialize(plr, new PlayerEntity { Items = new List<PlayerItemEntity> { entity } });
            return plr.Inventory[(ulong)entity.Id];
        }

        private static List<object> Messages(FakeSocketChannel channel) =>
            channel.Outbound.Select(o => o.GetType().GetProperty("Message")?.GetValue(o)).Where(x => x != null).ToList();

        private async Task<(Player, FakeSocketChannel)> LoginAsync(uint accountId, bool seedPlayer = true)
        {
            var cache = (Foundatio.Caching.InMemoryCacheClient)_ctx.Get<Foundatio.Caching.ICacheClient>();
            await cache.SetAsync<string>(Constants.Cache.SessionKey(accountId), "sid-" + accountId);
            using (var auth = _ctx.Get<AuthContext>())
            {
                auth.Accounts.Add(new AccountEntity { Id = (int)accountId, Username = "g" + accountId, Nickname = "nick" + accountId });
                await auth.SaveChangesAsync();
            }
            if (seedPlayer)
            {
                using var db = _ctx.Get<GameContext>();
                db.Players.Add(new PlayerEntity { Id = (int)accountId, TotalExperience = 1000 });
                await db.SaveChangesAsync();
            }

            var (session, channel) = _ctx.CreateSession(accountId);
            await _ctx.Get<AuthenticationHandler>().OnHandle(new MessageContext { Session = session }, new LoginRequestReqMessage
            {
                AccountId = accountId, SessionId = "sid-" + accountId, Version = new Version(1, 0, 0, 0)
            });
            Assert.NotNull(session.Player);
            return (session.Player, channel);
        }
    }
}
