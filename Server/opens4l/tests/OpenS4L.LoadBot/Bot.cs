using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using OpenS4L;
using OpenS4L.Blub.Serialization;
using OpenS4L.Network;
using OpenS4L.Network.Data.Auth;
using OpenS4L.Network.Data.Game;
using OpenS4L.Network.Data.GameRule;
using OpenS4L.Network.Message.Auth;
using OpenS4L.Network.Message.Chat;
using OpenS4L.Network.Message.Game;
using OpenS4L.Network.Message.GameRule;
using ProudNet;
using ProudNet.Serialization;

namespace OpenS4L.LoadBot
{
    /// <summary>
    /// One simulated player: connects to the auth server, logs in, connects to the game server,
    /// and enters a channel; in the rooms scenario it then joins a room and plays a round. A real
    /// (network-valid) ProudNet client on the wire, so the bot shows up in the game server's
    /// PlayerManager and thus in the admin console (/players, /channels). Every request/reply step
    /// and the keep-alive round trip are recorded in <see cref="Metrics"/>.
    /// </summary>
    public class Bot
    {
        // ProudNet "network version" GUID each server validates during the handshake.
        private static readonly Guid AuthVersion = new Guid("{9be73c0b-3b10-403e-be7d-9f222702a38c}");
        private static readonly Guid GameVersion = new Guid("{beb92241-8333-4117-ab92-9b4af78c688f}");
        private static readonly Guid ChatVersion = new Guid("{97d36acf-8cc0-4dfb-bcc9-97cab255e2bc}");

        private static readonly Version ClientVersion = new Version(0, 8, 32, 26995);

        private readonly BlubSerializer _serializer;
        private readonly MessageFactory[] _factories;
        private readonly IPEndPoint _authEndPoint;
        private readonly IPEndPoint _gameEndPoint;
        private readonly IPEndPoint _chatEndPoint;
        private readonly string _username;
        private readonly string _password;
        private readonly uint _channelId;
        private readonly string _nickname;
        private readonly int _chatMessages;
        private readonly RoomGroup _room;
        private readonly int _roomMember;

        public int Index { get; }

        /// <summary>Optional per-bot protocol trace (handshake steps, sends, decoded messages).</summary>
        public Action<string> Trace { get; set; }

        public Bot(BlubSerializer serializer, MessageFactory[] factories, int index,
            IPEndPoint authEndPoint, IPEndPoint gameEndPoint, string username, string password,
            uint channelId, string nickname, IPEndPoint chatEndPoint = null, int chatMessages = 0,
            RoomGroup room = null, int roomMember = 0)
        {
            _room = room;
            _roomMember = roomMember;
            _serializer = serializer;
            _factories = factories;
            Index = index;
            _authEndPoint = authEndPoint;
            _gameEndPoint = gameEndPoint;
            _chatEndPoint = chatEndPoint;
            _username = username;
            _password = password;
            _channelId = channelId;
            _nickname = nickname;
            _chatMessages = chatMessages;
        }

        public async Task<bool> RunAsync(CancellationToken ct)
        {
            using var auth = new ServerConnection(_serializer, _factories);
            auth.Client.Trace = Trace;
            auth.Client.Error += (_, ex) => Log("auth error: {0}", ex.Message);
            auth.Client.Disconnected += (_, __) => Log("auth disconnected");
            auth.Client.RoundTrip += (_, rtt) => Metrics.Record("ping.auth", rtt);
            Log("connecting to auth {0}", _authEndPoint);

            var step = Metrics.Start("auth.connect");
            await Guard(step, auth.Client.ConnectAsync(_authEndPoint, AuthVersion, ct));
            step.Done();

            // 1. Auth login
            step = Metrics.Start("auth.login");
            var loginWait = auth.WaitFor<LoginEUAckMessage>(TimeSpan.FromSeconds(10));
            auth.Client.Send(new LoginEUReqMessage
            {
                Username = _username,
                Password = _password,
                Unk1 = "", Unk2 = "", Unk3 = 0, Unk4 = 0, Unk5 = 0, Unk6 = "", Unk7 = 0,
                Unk8 = "", Unk9 = "", Token = new AeriaTokenDto(), Unk10 = ""
            });

            var loginAck = await Guard(step, loginWait);
            if (loginAck.Result != AuthLoginResult.OK)
            {
                step.Failed();
                Log("auth login rejected: {0}", loginAck.Result);
                return false;
            }

            step.Done();

            Log("auth login OK (account {0}, session {1})", loginAck.AccountId, loginAck.SessionId);

            // 2. Server list -> find the game server endpoint.
            // After a game-server restart it can take up to ~30s for it to announce itself to
            // auth (the server-list update interval). Re-request until it appears so a test run
            // right after a restart doesn't fail with "no game server in server list".
            var gameInfo = null as ServerInfoDto;
            var serverListDeadline = DateTime.UtcNow.AddSeconds(60);
            while (gameInfo == null && DateTime.UtcNow < serverListDeadline)
            {
                step = Metrics.Start("auth.serverlist");
                var listWait = auth.WaitFor<ServerListAckMessage>(TimeSpan.FromSeconds(10));
                auth.Client.Send(new ServerListReqMessage());
                var serverList = await Guard(step, listWait);
                step.Done();
                foreach (var s in serverList.ServerList)
                {
                    if (s.Type == ServerType.Game && s.IsEnabled)
                    {
                        gameInfo = s;
                        break;
                    }
                }

                if (gameInfo == null)
                {
                    Log("game server not in server list yet, retrying...");
                    await Task.Delay(3000, ct);
                }
            }

            if (gameInfo == null)
            {
                Log("no game server in server list after retries");
                return false;
            }

            Log("server list has game server {0} at {1}", gameInfo.Name, gameInfo.EndPoint);
            var gameEndPoint = _gameEndPoint ?? gameInfo.EndPoint;

            // 3. Game login
            using var game = new ServerConnection(_serializer, _factories);
            game.Client.Trace = Trace;
            game.Client.Error += (_, ex) => Log("game error: {0}", ex.Message);
            game.Client.Disconnected += (_, __) => Log("game disconnected");
            game.Client.MessageReceived += (_, e) =>
            {
                if (e.Message is ServerResultAckMessage res)
                    Log("game result: {0}", res.Result);
                else if (e.Message is ChannelListInfoAckMessage)
                    Log("game result: got channel list");
            };
            game.Client.RoundTrip += (_, rtt) => Metrics.Record("ping.game", rtt);
            Log("connecting to game {0}", gameEndPoint);
            step = Metrics.Start("game.connect");
            await Guard(step, game.Client.ConnectAsync(gameEndPoint, GameVersion, ct));
            step.Done();

            step = Metrics.Start("game.login");
            var gameWait = game.WaitFor<LoginReguestAckMessage>(TimeSpan.FromSeconds(10));
            game.Client.Send(new LoginRequestReqMessage
            {
                Username = _username,
                Version = ClientVersion,
                AccountId = loginAck.AccountId,
                SessionId = loginAck.SessionId,
                KickConnection = true,
                Unk1 = 0, Unk3 = 0, Unk4 = "", Unk5 = "", Unk6 = 0,
                AeriaToken = new AeriaTokenDto()
            });

            var gameAck = await Guard(step, gameWait);
            Log("game login ack result={0}", gameAck.Result);
            if (gameAck.Result == GameLoginResult.OK || gameAck.Result == GameLoginResult.ChooseNickname)
                step.Done();
            if (gameAck.Result == GameLoginResult.ChooseNickname)
            {
                Log("game login: nickname required, creating character '{0}'", _nickname);
                game.Client.Send(new CharacterFirstCreateReqMessage
                {
                    Nickname = _nickname,
                    Style = new CharacterStyle(CharacterGender.Male, 0, 0, 0, 0, 0),
                    // FixedArraySerializer(8): must supply exactly 8 item numbers. The server
                    // auto-grants a weapon (dagger 2000006) + skill (shield 3050001) regardless,
                    // so include them + 6 empty slots.
                    Items = new[]
                    {
                        new ItemNumber(2000006), new ItemNumber(3050001),
                        new ItemNumber(0), new ItemNumber(0), new ItemNumber(0),
                        new ItemNumber(0), new ItemNumber(0), new ItemNumber(0)
                    }
                });
            }
            else if (gameAck.Result != GameLoginResult.OK)
            {
                step.Failed();
                Log("game login failed: {0}", gameAck.Result);
                return false;
            }

            // Give the login/account-information flow a moment to settle.
            await Task.Delay(500, ct);

            // 4. Enter a channel. This keeps the player registered in the game server's
            // PlayerManager (the all-channels test proves players who enter a channel stay
            // online). Chat login (server-side OnChatLogin) looks the player up there, so a
            // chat bot MUST be in a game channel for chat login to succeed.
            Log("entering channel {0}", _channelId);
            step = Metrics.Start("game.channel");
            var channelWait = game.WaitFor<ServerResultAckMessage>(TimeSpan.FromSeconds(10),
                m => m.Result == ServerResult.ChannelEnter || m.Result == ServerResult.JoinChannelFailed ||
                     m.Result == ServerResult.ChannelLimitReached || m.Result == ServerResult.NonExistingChannel);
            game.Client.Send(new ChannelEnterReqMessage { Channel = _channelId });
            var channelAck = await Guard(step, channelWait);
            if (channelAck.Result != ServerResult.ChannelEnter)
            {
                step.Failed();
                Log("channel {0} refused: {1}", _channelId, channelAck.Result);
                return false;
            }

            step.Done();
            Log("bot {0} online in channel {1}", Index, _channelId);

            // 4b. Rooms scenario: join (or create) the group's room and play a round.
            if (_room != null && !await DoRoomAsync(game, loginAck.AccountId, ct))
                return false;

            // 5. If a chat endpoint is configured, also connect to the chat server and chat.
            if (_chatEndPoint != null)
                return await DoChatAsync(game, loginAck.AccountId, loginAck.SessionId, ct);

            // 6. Otherwise stay connected until cancelled.
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            Log("bot {0} shutting down", Index);
            return true;
        }

        private async Task<bool> DoChatAsync(ServerConnection game, ulong accountId, string sessionId, CancellationToken ct)
        {
            using var chat = new ServerConnection(_serializer, _factories);
            chat.Client.Trace = Trace;
            chat.Client.Error += (_, ex) => Log("chat error: {0}", ex.Message);
            chat.Client.Disconnected += (_, __) => Log("chat disconnected");

            chat.Client.RoundTrip += (_, rtt) => Metrics.Record("ping.chat", rtt);
            Log("connecting to chat {0}", _chatEndPoint);
            var step = Metrics.Start("chat.connect");
            await Guard(step, chat.Client.ConnectAsync(_chatEndPoint, ChatVersion, ct));
            step.Done();

            // The game server sets Account.Nickname while handling CharacterFirstCreate, and
            // ProudNet does not wait for one handler to finish before dispatching the next, so
            // under load the nickname can still be unset when chat login arrives — the chat
            // server then answers 3 (nickname mismatch). Retry a few times instead of failing
            // the bot; anything else (wrong session, already logged in, server full) is final.
            LoginAckMessage ack = null;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                step = Metrics.Start("chat.login");
                var chatWait = chat.WaitFor<LoginAckMessage>(TimeSpan.FromSeconds(10));
                chat.Client.Send(new LoginReqMessage
                {
                    AccountId = accountId,
                    Nickname = _nickname,
                    SessionId = sessionId
                });

                ack = await Guard(step, chatWait);
                if (ack.Result != 3)
                    break;

                Log("chat login not ready (result 3, attempt {0}/5) — nickname not live yet", attempt);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            if (ack.Result != 0)
            {
                step.Failed();
                Log("chat login failed: result {0}", ack.Result);
                return false;
            }

            step.Done();

            Log("chat login OK — chatting as '{0}'", _nickname);

            // Send chat messages. If _chatMessages > 0, send exactly that many then finish;
            // otherwise send one every ~3s until cancelled.
            var n = 0;
            try
            {
                while (_chatMessages == 0 || n < _chatMessages)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    n++;
                    chat.Client.Send(new MessageChatReqMessage
                    {
                        ChatType = ChatType.Channel,
                        Message = $"hello from bot {Index} ({n})"
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }

            Log("bot {0} chat quota done ({1} message(s) sent) — staying online", Index, n);

            // --stay applies to chat bots too: once the message quota is sent, keep the game
            // channel + chat session open until cancelled, so a run stays watchable for its
            // full duration instead of ending as soon as the last message is out.
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            Log("bot {0} chat done ({1} message(s) sent)", Index, n);
            return true;
        }

        /// <summary>
        /// Rooms scenario. The master (member 0) creates the room; the others join it once it
        /// exists. Everyone but the master readies up, the master starts the round, and each bot
        /// reports loading done. Steps: room.make / room.enter (request to the master-change ack
        /// that ends the join), room.ready, room.loading (master's begin request to this bot's
        /// loading ack) and room.start (loading done to the game-start ack; this includes the
        /// server's 5 s start countdown). The client's relay (P2P) connection is not simulated.
        /// </summary>
        private async Task<bool> DoRoomAsync(ServerConnection game, ulong accountId, CancellationToken ct)
        {
            var isMaster = _roomMember == 0;
            var joinTimeout = TimeSpan.FromSeconds(10);
            try
            {
                uint roomId;
                var step = Metrics.Start(isMaster ? "room.make" : "room.enter");
                if (isMaster)
                {
                    var infoWait = game.WaitFor<RoomEnterRoomInfoAck2Message>(joinTimeout);
                    var refusedWait = game.WaitFor<ServerResultAckMessage>(joinTimeout,
                        m => m.Result == ServerResult.FailedToRequestTask);
                    game.Client.Send(new RoomMakeReq2Message
                    {
                        Room = new MakeRoom2Dto
                        {
                            Name = $"loadbot {_room.Number}",
                            GameRule = _room.Rule,
                            Map = _room.Map,
                            PlayerLimit = (byte)Math.Max(_room.Size, 8),
                            ScoreLimit = 100,
                            TimeLimit = TimeSpan.FromMinutes(30),
                            ItemLimit = 0,
                            Password = "",
                            IsSpectatingEnabled = false,
                            SpectatorLimit = 0
                        }
                    });
                    if (await Task.WhenAny(infoWait, refusedWait) == refusedWait && refusedWait.IsCompletedSuccessfully)
                    {
                        step.Failed();
                        throw new InvalidOperationException(
                            $"room refused (FailedToRequestTask): is map {_room.Map} valid for {_room.Rule}?");
                    }

                    roomId = (await Guard(step, infoWait)).Room.RoomId;
                }
                else
                {
                    // Not part of the step: the master may still be logging in.
                    roomId = await _room.RoomId.Task.WaitAsync(TimeSpan.FromSeconds(120), ct);
                    step = Metrics.Start("room.enter");
                    var infoWait = game.WaitFor<RoomEnterRoomInfoAck2Message>(joinTimeout);
                    game.Client.Send(new RoomEnterReqMessage { RoomId = roomId, Password = "" });
                    await Guard(step, infoWait);
                }

                // The client sends this once it is connected to the relay; the server then
                // finishes the join (name tags, master/referee, briefing).
                var masterWait = game.WaitFor<RoomChangeMasterAckMessage>(joinTimeout);
                game.Client.Send(new RoomEnterPlayerReqMessage());
                await Guard(step, masterWait);
                step.Done();
                Log("bot {0} in room {1} ({2})", Index, roomId, isMaster ? "master" : "member");

                if (isMaster)
                    _room.RoomId.TrySetResult(roomId);
                _room.Joined();
                await _room.AllJoined.Task.WaitAsync(TimeSpan.FromSeconds(120), ct);

                // Registered before anyone can start the round, so no loading ack is missed.
                var loadingWait = game.WaitFor<RoomGameLoadingAckMessage>(TimeSpan.FromSeconds(120));

                if (!isMaster)
                {
                    step = Metrics.Start("room.ready");
                    var readyWait = game.WaitFor<RoomReadyRoundAckMessage>(joinTimeout,
                        m => m.AccountId == accountId && m.IsReady);
                    game.Client.Send(new RoomReadyRoundReq2Message { IsReady = true, EquipCheck = NewEquipCheck() });
                    await Guard(step, readyWait);
                    step.Done();
                    _room.Ready();
                }
                else
                {
                    if (_room.Size > 1)
                        await _room.AllReady.Task.WaitAsync(TimeSpan.FromSeconds(120), ct);

                    Interlocked.Exchange(ref _room.BeginTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
                    game.Client.Send(new RoomBeginRoundReq2Message { IsReady = true, EquipCheck = NewEquipCheck() });
                }

                step = Metrics.Start("room.loading");
                await Guard(step, loadingWait);
                var begin = Interlocked.Read(ref _room.BeginTimestamp);
                if (begin != 0)
                    Metrics.Record("room.loading", System.Diagnostics.Stopwatch.GetElapsedTime(begin));

                step = Metrics.Start("room.start");
                var startWait = game.WaitFor<RoomGameStartAckMessage>(TimeSpan.FromSeconds(60));
                game.Client.Send(new GameLoadingSuccessReqMessage());
                await Guard(step, startWait);
                step.Done();
                Log("bot {0} playing in room {1}", Index, roomId);
                return true;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Log("room flow failed: {0}", ex.Message);
                _room.Abort($"bot {Index}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Equip snapshot the client sends with ready/begin. The server checks the equipment it
        /// tracks itself, so empty slots are enough; the fixed-size arrays must still be full.
        /// </summary>
        private static EquipCheckDto NewEquipCheck() => new EquipCheckDto
        {
            Weapons = Enumerable.Range(0, 3).Select(_ => new ItemCheckDto()).ToArray(),
            Skill = new ItemCheckDto(),
            Costumes = Enumerable.Range(0, 8).Select(_ => new ItemCheckDto()).ToArray()
        };

        /// <summary>Await <paramref name="task"/>, counting a timeout or error as a failure of the step.</summary>
        private static async Task Guard(Metrics.StepTimer step, Task task)
        {
            try
            {
                await task;
            }
            catch
            {
                step.Failed();
                throw;
            }
        }

        private static async Task<T> Guard<T>(Metrics.StepTimer step, Task<T> task)
        {
            try
            {
                return await task;
            }
            catch
            {
                step.Failed();
                throw;
            }
        }

        private void Log(string message, params object[] args)
        {
            Console.WriteLine($"[bot {Index}] " + string.Format(message, args));
        }
    }
}
