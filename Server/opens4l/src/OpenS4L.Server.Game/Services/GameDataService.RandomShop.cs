using System;
using System.IO;
using System.Xml.Linq;
using OpenS4L.Server.Game.Data;

namespace OpenS4L.Server.Game.Services
{
    public partial class GameDataService
    {
        /// <summary>
        /// Fumbi random shop settings. Never null: falls back to <see cref="RandomShopInfo.Default"/>.
        /// </summary>
        public RandomShopInfo RandomShop { get; private set; } = RandomShopInfo.Default;

        private void LoadRandomShop()
        {
            LoadRandomShopSettings();
            RandomShop.Odds = LoadRandomShopOdds();
        }

        /// <summary>The 2019 EU odds (operator data), or null when the file is missing or unreadable.</summary>
        private RandomShopOdds LoadRandomShopOdds()
        {
            const string fileName = RandomShopOdds.FileName;
            var path = Path.Combine(_resourcePath, fileName.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                _logger.Information("{FileName} not found - random shop uses the new_random_shop.x7 odds", fileName);
                return null;
            }

            try
            {
                var odds = RandomShopOdds.Parse(XDocument.Load(path));
                _logger.Information("Loaded random shop odds: {Lineup} lineup entries, {Periods} period/{Effects} effect/{Colors} color groups",
                    odds.Lineup.Count, odds.PeriodGroups.Count, odds.EffectGroups.Count, odds.ColorGroups.Count);
                return odds;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to read {FileName} - random shop uses the new_random_shop.x7 odds", fileName);
                return null;
            }
        }

        private void LoadRandomShopSettings()
        {
            const string fileName = "xml/new_random_shop.x7";
            var path = Path.Combine(_resourcePath, fileName.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                _logger.Information("{FileName} not found - using built-in random shop settings", fileName);
                RandomShop = RandomShopInfo.Default;
                return;
            }

            _logger.Information("Loading random shop settings...");
            try
            {
                RandomShop = RandomShopInfo.Parse(XDocument.Load(path));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to read {FileName} - using built-in random shop settings", fileName);
                RandomShop = RandomShopInfo.Default;
            }
        }
    }
}
