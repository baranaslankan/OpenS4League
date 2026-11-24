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
