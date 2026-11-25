using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ProudNet.Hosting.Services;

namespace OpenS4L.Server.Game.Services
{
    /// <summary>
    /// Periodically expires hour/day period items of online players: an item whose time is up is
    /// unequipped and deleted (<see cref="PlayerInventory.UpdateExpiredItems"/>).
    /// Items already expired at login are deleted while loading. Nothing is written here - the
    /// unequip and delete are queued for the <see cref="PlayerSaveService"/> write-behind
    /// cycle.
    /// </summary>
    public class ItemExpiryService : IHostedService
    {
        private readonly ILogger _logger;
        private readonly AppOptions _appOptions;
        private readonly ISchedulerService _schedulerService;
        private readonly PlayerManager _playerManager;
        private readonly CancellationTokenSource _shutdown;

        public ItemExpiryService(ILogger<ItemExpiryService> logger, IOptions<AppOptions> appOptions,
            ISchedulerService schedulerService, PlayerManager playerManager)
        {
            _logger = logger;
            _appOptions = appOptions.Value;
            _schedulerService = schedulerService;
            _playerManager = playerManager;
            _shutdown = new CancellationTokenSource();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _schedulerService.ScheduleAsync(OnTick, null, null, _appOptions.ItemExpiryCheckInterval, _shutdown.Token);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _shutdown.Cancel();
            return Task.CompletedTask;
        }

        private async void OnTick(object _, object __)
        {
            if (_shutdown.IsCancellationRequested)
                return;

            try
            {
                CheckPlayers(DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to check item expiry");
            }

            if (_shutdown.IsCancellationRequested)
                return;

            try
            {
                await _schedulerService.ScheduleAsync(OnTick, null, null, _appOptions.ItemExpiryCheckInterval,
                    _shutdown.Token);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to schedule next item expiry check");
            }
        }

        internal void CheckPlayers(DateTimeOffset now)
        {
            foreach (var plr in _playerManager.Where(x => x.Session != null && x.Session.IsConnected).ToList())
            {
                // Equipment can't change during a match (the client refuses it as well); the next
                // check after the match picks the item up.
                if (plr.Room != null && plr.State != PlayerState.Lobby)
                    continue;

                try
                {
                    var changed = plr.Inventory.UpdateExpiredItems(now);
                    if (changed.Count > 0)
                    {
                        plr.AddContextToLogger(_logger).Information("Expired items {Items}",
                            string.Join(", ", changed.Select(x => $"{x.Id}:{x.ItemNumber}")));
                    }
                }
                catch (Exception ex)
                {
                    plr.AddContextToLogger(_logger).Error(ex, "Unable to expire items");
                }
            }
        }
    }
}
