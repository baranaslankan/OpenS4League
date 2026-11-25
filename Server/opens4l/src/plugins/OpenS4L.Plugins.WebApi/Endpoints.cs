using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenS4L.Database;
using OpenS4L.Database.Auth;
using OpenS4L.Server.Game;
using OpenS4L.Server.Game.Data;
using OpenS4L.Server.Game.Services;
using OpenS4L.Plugins.WebApi.Mappers;
using OpenS4L.Plugins.WebApi.Models;

namespace OpenS4L.Plugins.WebApi
{
    /// <summary>
    /// Minimal-API route mappings for the Web API plugin (replaces the old EmbedIO
    /// WebApiController classes). Services are resolved per-request from the host's
    /// container (<paramref name="services"/>), which this plugin does not own.
    /// </summary>
    public static class Endpoints
    {
        public const string AdminKeyHeader = "X-Admin-Key";

        public static void Map(WebApplication app, IServiceProvider services, Serilog.ILogger logger, WebApiMapper mapper,
            string adminKey = null)
        {
            // /admin/* runs console commands, bans and kicks. With an AdminKey configured, a request
            // without the matching X-Admin-Key header is refused before it reaches any route.
            if (!string.IsNullOrEmpty(adminKey))
            {
                var expected = Encoding.UTF8.GetBytes(adminKey);
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments("/admin"))
                    {
                        var given = Encoding.UTF8.GetBytes(context.Request.Headers[AdminKeyHeader].ToString());
                        if (!CryptographicOperations.FixedTimeEquals(given, expected))
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return;
                        }
                    }

                    await next();
                });
            }

            // GET /statistics
            app.MapGet("/statistics", () =>
            {
                var uptime = DateTime.Now - Process.GetCurrentProcess().StartTime;
                var players = services.GetRequiredService<PlayerManager>().Count;
                StatisticsTracker.Record(players);
                var dto = new StatisticsDto(
                    (long)uptime.TotalSeconds,
                    players,
                    StatisticsTracker.PeakPlayers);
                logger.ForContext<WebApiService>()
                    .Information("GetStatistics invoked: players={Players} uptime={Uptime}s peak={Peak}",
                        dto.PlayersOnline, dto.Uptime, dto.PeakPlayers);
                return Results.Json(dto);
            });

            // GET /metrics — aggregates for the public status page (no nicknames, ids or passwords)
            app.MapGet("/metrics", () =>
            {
                var uptime = DateTime.Now - Process.GetCurrentProcess().StartTime;
                var players = services.GetRequiredService<PlayerManager>().Count;
                StatisticsTracker.Record(players);

                var channels = services.GetRequiredService<ChannelService>().ToArray();
                var rooms = channels.SelectMany(c => c.RoomManager).ToArray();
                var dto = new MetricsDto
                {
                    ServerTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Uptime = (long)uptime.TotalSeconds,
                    PlayersOnline = players,
                    PeakPlayers = StatisticsTracker.PeakPlayers,
                    Channels = channels.Select(c => new MetricsChannelDto
                    {
                        Id = c.Id,
                        Name = c.Name,
                        PlayersOnline = c.Players.Count,
                        PlayerLimit = c.PlayerLimit,
                        Rooms = c.RoomManager.Count
                    }).ToArray(),
                    Rooms = new MetricsRoomsDto
                    {
                        Total = rooms.Length,
                        Playing = rooms.Count(r => r.GameRule.StateMachine.GameState == GameState.Playing),
                        PlayersInRooms = rooms.Sum(r => r.Players.Count),
                        ByRule = rooms.GroupBy(r => r.GameRule.GameRule.ToString())
                            .ToDictionary(g => g.Key, g => g.Count()),
                        ByState = rooms.GroupBy(r => r.GameRule.StateMachine.GameState.ToString())
                            .ToDictionary(g => g.Key, g => g.Count())
                    },
                    Process = ProcessSampler.Sample()
                };
                return Results.Json(dto);
            });

            // GET /channels and /channels/{channelId}
            app.MapGet("/channels", () => Results.Json(
                services.GetRequiredService<ChannelService>()
                    .Select(x => mapper.ToChannelDto(x))
                    .ToArray()));

            app.MapGet("/channels/{channelId:long}", (long channelId) =>
            {
                var channel = services.GetRequiredService<ChannelService>()[(uint)channelId];
                return channel == null
                    ? Results.NotFound()
                    : Results.Json(mapper.ToChannelDto(channel));
            });

            // GET /gamedata/maps, /gamedata/maps/{mapId}, /gamedata/items/{itemId}
            app.MapGet("/gamedata/maps", () => Results.Json(
                services.GetRequiredService<GameDataService>().Maps
                    .Select(x => mapper.ToMapDto(x))
                    .ToArray()));

            app.MapGet("/gamedata/maps/{mapId:long}", (long mapId) =>
            {
                var map = services.GetRequiredService<GameDataService>().Maps
                    .FirstOrDefault(x => x.Id == mapId);
                return map == null
                    ? Results.NotFound()
                    : Results.Json(mapper.ToMapDto(map));
            });

            app.MapGet("/gamedata/items/{itemId:long}", (long itemId) =>
            {
                var item = services.GetRequiredService<GameDataService>().Items[(uint)itemId];
                return item == null
                    ? Results.NotFound()
                    : Results.Json(mapper.ToItemDto(item));
            });

            // GET /rooms/{channelId} and /rooms/{channelId}/{roomId}
            app.MapGet("/rooms/{channelId:long}", (long channelId) =>
            {
                var channel = services.GetRequiredService<ChannelService>()[(uint)channelId];
                return channel == null
                    ? Results.NotFound()
                    : Results.Json(channel.RoomManager.Select(x => mapper.ToRoomDto(x)).ToArray());
            });

            app.MapGet("/rooms/{channelId:long}/{roomId:long}", (long channelId, long roomId) =>
            {
                var channel = services.GetRequiredService<ChannelService>()[(uint)channelId];
                if (channel == null)
                    return Results.NotFound();

                var room = channel.RoomManager[(uint)roomId];
                return room == null
                    ? Results.NotFound()
                    : Results.Json(mapper.ToRoomDto(room));
            });

            // GET /players and /players/{playerId}
            app.MapGet("/players", () => Results.Json(
                services.GetRequiredService<PlayerManager>()
                    .Select(x => mapper.ToPlayerDto(x))
                    .ToArray()));

            app.MapGet("/players/{playerId:long}", (long playerId) =>
            {
                var plr = services.GetRequiredService<PlayerManager>()[(ulong)playerId];
                if (plr == null)
                    return Results.NotFound();

                return Results.Json(mapper.ToPlayerDto(plr));
            });

            // POST /admin/kick — form-encoded playerId
            app.MapPost("/admin/kick", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                if (!form.TryGetValue("playerId", out var playerIdStr))
                    return Results.BadRequest(new { error = "Invalid payload" });

                if (!ulong.TryParse(playerIdStr.ToString(), out var playerId))
                    return Results.BadRequest(new { error = "Invalid payload" });

                var plr = services.GetRequiredService<PlayerManager>()[playerId];
                if (plr == null)
                    return Results.NotFound(new { error = "Player not found" });

                plr.Disconnect();
                return Results.Ok();
            });

            // POST /admin/command — runs a server console command (form field "command"), as if it
            // had been typed into the game server's console. Only console-enabled commands run.
            app.MapPost("/admin/command", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                if (!form.TryGetValue("command", out var command) || string.IsNullOrWhiteSpace(command.ToString()))
                    return Results.BadRequest(new { error = "Invalid payload" });

                var args = command.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                logger.Information("Web API console command {Command}", command.ToString());
                var ok = await services.GetRequiredService<CommandService>().Execute(null, args);
                return Results.Json(new { ok });
            });

            // POST /admin/ban, /admin/roomkick, /admin/closeroom — JSON bodies
            app.MapPost("/admin/ban", async (HttpRequest request) =>
            {
                var body = await request.ReadFromJsonAsync<BanRequestDto>();
                if (body == null)
                    return Results.BadRequest(new { error = "Invalid payload" });

                var plr = services.GetRequiredService<PlayerManager>()[body.PlayerId];
                if (plr == null)
                    return Results.NotFound(new { error = "Player not found" });

                using (var db = services.GetRequiredService<DatabaseService>().Open<AuthContext>())
                {
                    db.Bans.Add(new BanEntity
                    {
                        AccountId = (int)plr.Account.Id,
                        Date = DateTimeOffset.Now.ToUnixTimeSeconds(),
                        Duration = body.Duration,
                        Reason = body.Reason
                    });

                    await db.SaveChangesAsync();
                }

                plr.Disconnect();
                return Results.Ok();
            });

            app.MapPost("/admin/roomkick", async (HttpRequest request) =>
            {
                var body = await request.ReadFromJsonAsync<RoomKickRequestDto>();
                if (body == null)
                    return Results.BadRequest(new { error = "Invalid payload" });

                var plr = services.GetRequiredService<PlayerManager>()[body.PlayerId];
                if (plr == null)
                    return Results.NotFound(new { error = "Player not found" });

                if (plr.Room == null)
                    return Results.NotFound(new { error = "Player is not in a room" });

                plr.Room.Leave(plr, body.Reason ?? RoomLeaveReason.ModeratorKick);
                return Results.Ok();
            });

            app.MapPost("/admin/closeroom", async (HttpRequest request) =>
            {
                var body = await request.ReadFromJsonAsync<CloseRoomRequestDto>();
                if (body == null)
                    return Results.BadRequest(new { error = "Invalid payload" });

                var channel = services.GetRequiredService<ChannelService>()[body.ChannelId];
                if (channel == null)
                    return Results.NotFound(new { error = "Channel not found" });

                var room = channel.RoomManager[body.RoomId];
                if (room == null)
                    return Results.NotFound(new { error = "Room not found" });

                foreach (var plr in room.Players.Values)
                    plr.Room.Leave(plr, body.Reason ?? RoomLeaveReason.ModeratorKick);

                return Results.Ok();
            });
        }
    }
}
