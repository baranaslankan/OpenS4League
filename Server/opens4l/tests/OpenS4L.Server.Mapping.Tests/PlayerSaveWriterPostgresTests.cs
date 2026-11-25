using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using OpenS4L.Database;
using OpenS4L.Database.Game;
using OpenS4L.Server.Game.Services;
using Xunit;

namespace OpenS4L.Server.Mapping.Tests
{
    /// <summary>
    /// PlayerSaveWriter against a real Postgres: deleting an item a character row still points at
    /// (an expired or refunded item that was worn). The character FKs have no ON DELETE action.
    /// </summary>
    public class PlayerSaveWriterPostgresTests : IAsyncLifetime
    {
        private PostgresDatabase _db;
        private GameTestContext _ctx;

        public async Task InitializeAsync()
        {
            _db = await PostgresFixture.Instance.CreateDatabaseAsync();
            _ctx = new GameTestContext(_db);
        }

        public async Task DisposeAsync()
        {
            _ctx?.Dispose();
            if (_db != null) await _db.DisposeAsync();
        }

        [Fact]
        public async Task WriteInventory_deletesAnItemStillWornByACharacter()
        {
            const int accountId = 9801;
            using (var db = _ctx.Get<GameContext>())
            {
                var priceGroup = new ShopPriceGroupEntity { Name = "pg", PriceType = 1 };
                priceGroup.ShopPrices.Add(new ShopPriceEntity { PeriodType = 3, Period = 30, Price = 1, IsEnabled = true });
                var effectGroup = new ShopEffectGroupEntity { Name = "eg" };
                var shopItem = new ShopItemEntity { Id = 2010005 };
                shopItem.ItemInfos.Add(new ShopItemInfoEntity { PriceGroup = priceGroup, EffectGroup = effectGroup, IsEnabled = true });
                db.Items.Add(shopItem);
                await db.SaveChangesAsync();

                var info = shopItem.ItemInfos[0];
                db.Players.Add(new PlayerEntity { Id = accountId, TotalExperience = 1000 });
                db.PlayerItems.Add(new PlayerItemEntity
                {
                    Id = 980101, PlayerId = accountId, ShopItemInfoId = info.Id,
                    ShopPriceId = priceGroup.ShopPrices[0].Id, Effects = "[]"
                });
                db.PlayerCharacters.Add(new PlayerCharacterEntity
                {
                    Id = 980110, PlayerId = accountId, Slot = 0, Weapon1Id = 980101, SkillId = 980101
                });
                await db.SaveChangesAsync();
            }

            var snapshot = new PlayerSaveSnapshot { AccountId = accountId };
            snapshot.ItemIdsToRemove.Add(980101);
            using (var db = _ctx.Get<GameContext>())
                await PlayerSaveWriter.WriteInventory(db, snapshot);

            using (var db = _ctx.Get<GameContext>())
            {
                Assert.False(await db.PlayerItems.AnyAsync(x => x.Id == 980101));
                var character = await db.PlayerCharacters.SingleAsync(x => x.Id == 980110);
                Assert.Null(character.Weapon1Id);
                Assert.Null(character.SkillId);
            }
        }
    }
}
