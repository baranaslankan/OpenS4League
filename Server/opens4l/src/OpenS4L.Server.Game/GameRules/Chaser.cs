using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Options;
using OpenS4L.Common.Configuration;
using OpenS4L.Network.Message.GameRule;
using ProudNet.Hosting.Services;

namespace OpenS4L.Server.Game.GameRules
{
    /// <summary>
    /// Chaser (the client calls it "Slaughter", common::CSlaughter).
    /// Every round one player is the chaser and everyone else is a survivor.
    /// Survivors win the round when the round time runs out or when they kill the chaser,
    /// the chaser wins the round when every survivor is dead.
    /// </summary>
    /// <remarks>
    /// The scoring mirrors what the client computes on its own (CSlaughter score handlers,
    /// 0x10d8230 kill, 0x10d8840 suicide, 0x10d8c60 round win) so the scoreboard and the briefing agree.
    /// </remarks>
    public class Chaser : GameRuleBase
    {
        /// <summary>
        /// The byte of SlaughterRoundWinAck. The client compares it to 2 (0xee4018):
        /// 2 plays death_slaughter.ogg (the chaser died), anything else is the time out message
        /// </summary>
        public const byte RoundWinReasonTimeout = 1;
        public const byte RoundWinReasonChaserKilled = 2;

        private readonly ChaserOptions _options;
        private readonly ISchedulerService _schedulerService;
        private readonly Random _random = new Random();
        private readonly List<Player> _survivors = new List<Player>();
        private readonly HashSet<Player> _aliveSurvivors = new HashSet<Player>();
        private CancellationTokenSource _roundEndedCancellationTokenSource;
        private Player _lastChaser;

        public override GameRule GameRule => GameRule.Chaser;
        public override bool HasHalfTime => false;
        public override bool HasTimeLimit => true;

        public Player CurrentChaser { get; private set; }
        public Player BonusTarget { get; private set; }
        public int CurrentRound { get; private set; }
        public bool IsFirstRound => CurrentRound <= 1;
        public bool IsRoundRunning => _roundEndedCancellationTokenSource != null &&
                                      !_roundEndedCancellationTokenSource.IsCancellationRequested;

        /// <summary>Survivors of the current round (list A of the client briefing)</summary>
        public IReadOnlyList<Player> Survivors => _survivors;

        /// <summary>Survivors of the current round that are still alive (list B of the client briefing)</summary>
        public IEnumerable<Player> AliveSurvivors => _survivors.Where(x => _aliveSurvivors.Contains(x));

        /// <summary>The time the current round lasts, including the intro the client adds</summary>
        public TimeSpan CurrentRoundTime { get; private set; }

        /// <summary>The round limit without the intro (the client's briefing +0x38)</summary>
        public TimeSpan CurrentRoundLimit { get; private set; }

        public event EventHandler RoundStarted;
        public event EventHandler RoundEnded;

        protected virtual void OnRoundStarted()
        {
            RoundStarted?.Invoke(this, EventArgs.Empty);
        }

        protected virtual void OnRoundEnded()
        {
            RoundEnded?.Invoke(this, EventArgs.Empty);
        }

        public Chaser(GameRuleStateMachine stateMachine, IOptions<GameOptions> gameOptions,
            IOptions<ChaserOptions> options, ISchedulerService schedulerService)
            : base(stateMachine, gameOptions)
        {
            _options = options.Value;
            _schedulerService = schedulerService;
            StateMachine.GameStateChanged += OnGameStateChanged;
        }

        public override void Initialize(Room room)
        {
            base.Initialize(room);

            // Same as BattleRoyal: the free for all modes still use two teams in the room
            var playersPerTeam = Room.Options.PlayerLimit / 2;
            var spectatorsPerTeam = Room.Options.SpectatorLimit / 2;
            Room.TeamManager.Add(TeamId.Alpha, playersPerTeam, spectatorsPerTeam);
            Room.TeamManager.Add(TeamId.Beta, playersPerTeam, spectatorsPerTeam);
            Room.PlayerLeft += OnPlayerLeft;
        }

        public override void Cleanup()
        {
            base.Cleanup();

            Room.PlayerLeft -= OnPlayerLeft;
            Room.TeamManager.Remove(TeamId.Alpha);
            Room.TeamManager.Remove(TeamId.Beta);
        }

        protected override bool CanStartGame()
        {
            if (StateMachine.GameState != GameState.Waiting)
                return false;

            // The players that will be loaded: ready players and the master
            var count = TeamManager.Players
                .Count(plr => plr.Mode == PlayerGameMode.Normal && (plr.IsReady || Room.Master == plr));
            return count >= Math.Max(2, _options.MinPlayers);
        }

        protected override bool HasEnoughPlayers()
        {
            return TeamManager.PlayersPlaying.Count() > 1;
        }

        protected internal override Briefing CreateBriefing()
        {
            return new BriefingChaser(this);
        }

        protected override PlayerScore CreateScore(Player plr)
        {
            return new ChaserPlayerScore(_options);
        }

        protected override BriefingPlayer CreateBriefingPlayer(Player plr)
        {
            return new BriefingPlayerChaser(plr);
        }

        protected override (uint baseGain, uint bonusGain) CalculateExperienceGained(Player plr)
        {
            var experienceRates = _options.ExperienceRates ?? new ExperienceRateOptions();
            var place = 1;

            var plrs = TeamManager.Players
                .Where(x => x.State == PlayerState.Waiting && x.Mode == PlayerGameMode.Normal)
                .ToArray();

            foreach (var x in plrs.OrderByDescending(x => x.Score.GetTotalScore()))
            {
                if (x == plr)
                    break;

                place++;
                if (place > 3)
                    break;
            }

            var rankingBonus = 0f;
            switch (place)
            {
                case 1:
                    rankingBonus = experienceRates.FirstPlaceBonus;
                    break;

                case 2:
                    rankingBonus = experienceRates.SecondPlaceBonus;
                    break;

                case 3:
                    rankingBonus = experienceRates.ThirdPlaceBonus;
                    break;
            }

            var experienceGained = (uint)(plr.Score.GetTotalScore() * experienceRates.ScoreFactor +
                                          rankingBonus +
                                          plrs.Length * experienceRates.PlayerCountFactor +
                                          plr.GetCurrentPlayTime().TotalMinutes * experienceRates.ExperiencePerMinute);

            return (experienceGained, 0);
        }

        protected override (uint baseGain, uint bonusGain) CalculatePENGained(Player plr)
        {
            return (0, 0);
        }

        #region Score
        protected internal override void OnScoreKill(ScoreContext killer, ScoreContext assist, ScoreContext target,
            AttackAttribute attackAttribute)
        {
            if (target.IsSentry)
            {
                SendScoreKill(killer, assist, target, attackAttribute);
                return;
            }

            if (!IsRoundRunning)
                return;

            var targetScore = GetScore(target.Player);
            if (target.Player == CurrentChaser)
            {
                // A survivor killed the chaser
                targetScore.ChaserDeaths++;
                targetScore.Deaths++;
                if (killer.Player != CurrentChaser)
                    killer.Score.Kills++;

                if (assist != null && assist.Player != CurrentChaser)
                    assist.Score.KillAssists++;

                SendScoreKill(killer, assist, target, attackAttribute);
                EndRound(true, RoundWinReasonChaserKilled);
                return;
            }

            targetScore.Deaths++;
            targetScore.SurvivorDeaths++;
            if (killer.Player == CurrentChaser)
                CreditChaserKill(target.Player);

            SendScoreKill(killer, assist, target, attackAttribute);
            OnSurvivorDied(target.Player);
        }

        protected internal override void OnScoreTeamKill(ScoreContext killer, ScoreContext target,
            AttackAttribute attackAttribute)
        {
            if (target.IsSentry)
            {
                SendScoreTeamKill(killer, target, attackAttribute);
                return;
            }

            if (!IsRoundRunning)
                return;

            GetScore(killer.Player).TeamKills++;
            GetScore(target.Player).TeamKillDeaths++;
            target.Score.Deaths++;
            SendScoreTeamKill(killer, target, attackAttribute);

            if (target.Player == CurrentChaser)
            {
                EndRound(true, RoundWinReasonChaserKilled);
                return;
            }

            CreditChaserKill(target.Player);
            OnSurvivorDied(target.Player);
        }

        protected internal override void OnScoreHeal(Player plr)
        {
            if (!IsRoundRunning)
                return;

            if (plr != CurrentChaser)
                plr.Score.HealAssists++;

            SendScoreHeal(plr);
        }

        protected internal override void OnScoreSuicide(Player plr)
        {
            if (!IsRoundRunning)
                return;

            plr.Score.Deaths++;
            plr.Score.Suicides++;
            SendScoreSuicide(plr);

            if (plr == CurrentChaser)
            {
                EndRound(true, RoundWinReasonChaserKilled);
                return;
            }

            // The client credits the chaser for survivors that kill themselves (0x10d8840)
            CreditChaserKill(plr);
            OnSurvivorDied(plr);
        }

        /// <summary>
        /// The chaser's client reports the damage a survivor dealt to the chaser
        /// (CSlaughterKillProcessor, 0x55bc10)
        /// </summary>
        public void OnAttackPoint(Player sender, Player attacker, float attackPoints, float bonusAttackPoints)
        {
            if (!IsRoundRunning || sender != CurrentChaser || attacker == null || attacker == CurrentChaser)
                return;

            if (!IsValidPoints(attackPoints) || !IsValidPoints(bonusAttackPoints))
                return;

            var attackerScore = GetScore(attacker);
            attackerScore.AttackPoints += attackPoints;
            attackerScore.BonusAttackPoints += bonusAttackPoints;
            attackerScore.RoundAttackPoints += attackPoints;
            GetScore(sender).ChaserAttackPoints += attackPoints;

            // The sender already applied the points to its own briefing (0x55bf12)
            BroadcastExcept(sender, new SlaughterAttackPointAckMessage
            {
                AccountId = attacker.Account.Id,
                Unk1 = attackPoints,
                Unk2 = bonusAttackPoints
            });

            UpdateBonusTarget(attacker);
        }

        /// <summary>
        /// A client reports the hp it healed (CSlaughterHealAssistProcessor, 0x55aaa0)
        /// </summary>
        public void OnHealPoint(Player sender, float healPoints)
        {
            if (!IsRoundRunning || !IsValidPoints(healPoints))
                return;

            GetScore(sender).HealPoints += healPoints;

            // The sender already applied the points to its own briefing (0x55ab77)
            BroadcastExcept(sender, new SlaughterHealPointAckMessage
            {
                AccountId = sender.Account.Id,
                Unk = healPoints
            });
        }

        private static bool IsValidPoints(float points)
        {
            return !float.IsNaN(points) && !float.IsInfinity(points) && points >= 0 && points < 100000;
        }

        private void CreditChaserKill(Player target)
        {
            if (CurrentChaser == null)
                return;

            var chaserScore = GetScore(CurrentChaser);
            if (target != null && target == BonusTarget)
            {
                chaserScore.BonusKills++;
                chaserScore.RoundBonusKills++;
            }
            else
            {
                chaserScore.ChaserKills++;
                chaserScore.RoundChaserKills++;
            }
        }

        private void OnSurvivorDied(Player plr)
        {
            if (!_aliveSurvivors.Remove(plr))
                return;

            CheckSurvivors();
        }

        private void CheckSurvivors()
        {
            if (!IsRoundRunning)
                return;

            // In a solo room there are no survivors at all, the round only ends with the time
            if (_survivors.Count > 0 && _aliveSurvivors.Count == 0)
                EndRound(false, 0);
        }

        private void UpdateBonusTarget(Player candidate)
        {
            if (!_aliveSurvivors.Contains(candidate) || candidate == BonusTarget)
                return;

            var candidatePoints = GetScore(candidate).RoundAttackPoints;
            if (candidatePoints <= 0)
                return;

            if (BonusTarget != null && _aliveSurvivors.Contains(BonusTarget) &&
                GetScore(BonusTarget).RoundAttackPoints >= candidatePoints)
            {
                return;
            }

            SetBonusTarget(candidate);
        }

        private void SetBonusTarget(Player plr)
        {
            BonusTarget = plr;
            Room.Broadcast(new SlaughterChangeBonusTargetAckMessage(plr?.Account.Id ?? 0));
        }
        #endregion

        protected internal override void OnPlayerIntrude(Player plr)
        {
            if (CurrentChaser == null)
                return;

            plr.Session.Send(new SlaughterChangeSlaughterAckMessage(
                CurrentChaser.Account.Id,
                _survivors.Select(x => x.Account.Id).ToArray()
            ));

            if (BonusTarget != null)
                plr.Session.Send(new SlaughterChangeBonusTargetAckMessage(BonusTarget.Account.Id));
        }

        private void OnPlayerLeft(object sender, RoomPlayerEventArgs e)
        {
            if (StateMachine.GameState != GameState.Playing || !IsRoundRunning)
                return;

            var plr = e.Player;
            if (plr == CurrentChaser)
            {
                // No winner when the chaser leaves, just go to the next round
                EndRound(null, 0);
                return;
            }

            _survivors.Remove(plr);
            if (_aliveSurvivors.Remove(plr))
                CheckSurvivors();
        }

        private void OnGameStateChanged(object sender, EventArgs e)
        {
            switch (StateMachine.GameState)
            {
                case GameState.Playing:
                    if (CurrentRound != 0)
                        break;

                    _lastChaser = null;
                    NewRound(this, null);
                    break;

                case GameState.Result:
                    _roundEndedCancellationTokenSource?.Cancel();
                    break;

                case GameState.Waiting:
                    _roundEndedCancellationTokenSource?.Cancel();
                    CurrentRound = 0;
                    CurrentChaser = null;
                    BonusTarget = null;
                    _lastChaser = null;
                    _survivors.Clear();
                    _aliveSurvivors.Clear();
                    break;
            }
        }

        protected static ChaserPlayerScore GetScore(Player plr)
        {
            return (ChaserPlayerScore)plr.Score;
        }

        /// <summary>
        /// Round time as the client computes it (0x1094530): 60s + 10s per player above 6.
        /// The client adds an intro of 7s for the first round and 4s for every other round
        /// </summary>
        public static TimeSpan GetRoundLimit(ChaserOptions options, int playerCount)
        {
            var extraPlayers = Math.Max(playerCount - options.RoundTimeExtraPlayerThreshold, 0);
            return TimeSpan.FromSeconds(options.RoundTime + extraPlayers * options.RoundTimePerExtraPlayer);
        }

        private Player PickChaser(IReadOnlyList<Player> players)
        {
            var candidates = players.Count > 1
                ? players.Where(x => x != _lastChaser).ToArray()
                : players.ToArray();

            return candidates[_random.Next(candidates.Length)];
        }

        /// <param name="survivorsWon">true survivors won, false the chaser won, null no winner</param>
        private void EndRound(bool? survivorsWon, byte reason)
        {
            if (!IsRoundRunning)
                return;

            _roundEndedCancellationTokenSource.Cancel();

            if (survivorsWon == true)
            {
                // Everyone of list A gets the round win, everyone of list B (alive) the survive bonus (0x10d8c60)
                foreach (var plr in _survivors)
                    GetScore(plr).RoundWins++;

                foreach (var plr in AliveSurvivors)
                    GetScore(plr).RoundsSurvived++;

                Room.Broadcast(new SlaughterRoundWinAckMessage(reason));
            }
            else if (survivorsWon == false)
            {
                if (CurrentChaser != null)
                    GetScore(CurrentChaser).ChaserRoundWins++;

                Room.Broadcast(new SlaughterSLRoundWinAckMessage());
            }

            _lastChaser = CurrentChaser;
            OnRoundEnded();

            if (StateMachine.GameState != GameState.Playing)
                return;

            var delay = TimeSpan.FromSeconds(_options.NextRoundDelay);
            Room.Broadcast(new GameEventMessageAckMessage(
                GameEventMessage.ChaserIn,
                (ulong)delay.TotalMilliseconds,
                0,
                0,
                ""
            ));
            _schedulerService.ScheduleAsync(NewRound, this, _roundEndedCancellationTokenSource, delay);
        }

        private static void NewRound(object ctx, object state)
        {
            Debug.Assert(ctx is Chaser);
            var This = (Chaser)ctx;

            // state is the token source of the round that scheduled this, the round must not have changed since
            if (state != null && state != This._roundEndedCancellationTokenSource)
                return;

            if (This.StateMachine.GameState != GameState.Playing)
                return;

            var players = This.TeamManager.PlayersPlaying
                .Where(x => x.Mode == PlayerGameMode.Normal)
                .ToArray();
            if (players.Length == 0)
                return;

            This.CurrentRound++;
            This.StateMachine.ResetRoundTimer();
            This._roundEndedCancellationTokenSource = new CancellationTokenSource();

            var chaser = This.PickChaser(players);
            var chaserScore = GetScore(chaser);
            chaserScore.TimesChaser++;
            chaserScore.ConsecutiveChaser = chaser == This._lastChaser ? chaserScore.ConsecutiveChaser + 1 : 1;
            foreach (var plr in players)
            {
                var score = GetScore(plr);
                score.RoundAttackPoints = 0;
                score.RoundChaserKills = 0;
                score.RoundBonusKills = 0;
            }

            This.CurrentChaser = chaser;
            This._survivors.Clear();
            This._survivors.AddRange(players.Where(x => x != chaser));
            This._aliveSurvivors.Clear();
            foreach (var plr in This._survivors)
                This._aliveSurvivors.Add(plr);

            This.CurrentRoundLimit = GetRoundLimit(This._options, players.Length);
            This.CurrentRoundTime = This.CurrentRoundLimit + TimeSpan.FromSeconds(
                This.IsFirstRound ? This._options.FirstRoundIntro : This._options.RoundIntro);

            // Respawns everyone for the next round like Captain does
            if (!This.IsFirstRound)
                This.Room.Broadcast(new GameEventMessageAckMessage(GameEventMessage.ResetRound, 0, 0, 0, string.Empty));

            This.Room.Broadcast(new SlaughterChangeSlaughterAckMessage(
                chaser.Account.Id,
                This._survivors.Select(x => x.Account.Id).ToArray()
            ));
            This.OnRoundStarted();

            This._schedulerService.ScheduleAsync(
                (x, s) =>
                {
                    var rule = (Chaser)x;
                    if (s != rule._roundEndedCancellationTokenSource)
                        return;

                    rule.EndRound(true, RoundWinReasonTimeout);
                },
                This,
                This._roundEndedCancellationTokenSource,
                This.CurrentRoundTime,
                This._roundEndedCancellationTokenSource.Token
            );
        }

        private void BroadcastExcept(Player except, IGameRuleMessage message)
        {
            foreach (var plr in Room.Players.Values)
            {
                if (plr == except || plr.Session == null)
                    continue;

                plr.Session.Send(message);
            }
        }
    }

    /// <summary>
    /// common::CSlaughterBriefing. The client reads the players with 0x110e2b0 and doesn't
    /// read the trailer (slot 13 isn't overridden), the trailer follows the writer of the
    /// client's common code (slot 12, 0x1094640)
    /// </summary>
    public class BriefingChaser : Briefing
    {
        public ulong Chaser { get; set; }
        public ulong BonusTarget { get; set; }
        public int ElapsedTime { get; set; }
        public int RoundTime { get; set; }
        public int RoundLimit { get; set; }
        public bool IsFirstRound { get; set; }
        public ulong[] Survivors { get; set; }
        public ulong[] AliveSurvivors { get; set; }

        public BriefingChaser(Chaser chaser)
        {
            Chaser = chaser.CurrentChaser?.Account.Id ?? 0;
            BonusTarget = chaser.BonusTarget?.Account.Id ?? 0;
            ElapsedTime = (int)chaser.StateMachine.RoundTime.TotalMilliseconds;
            RoundTime = (int)chaser.CurrentRoundTime.TotalMilliseconds;
            RoundLimit = (int)chaser.CurrentRoundLimit.TotalMilliseconds;
            IsFirstRound = chaser.CurrentRound <= 1;
            Survivors = chaser.Survivors.Select(x => x.Account.Id).ToArray();
            AliveSurvivors = chaser.AliveSurvivors.Select(x => x.Account.Id).ToArray();
        }

        protected override void Serialize(BinaryWriter w)
        {
            base.Serialize(w);

            w.Write(Chaser);
            w.Write(BonusTarget);
            w.Write(ElapsedTime);
            w.Write(RoundTime);
            w.Write(RoundLimit);
            w.Write(IsFirstRound ? 1 : 0);

            // +0x48 int list, unknown
            w.Write(0);

            w.Write(Survivors.Length);
            foreach (var id in Survivors)
                w.Write(id);

            w.Write(AliveSurvivors.Length);
            foreach (var id in AliveSurvivors)
                w.Write(id);
        }
    }

    /// <summary>
    /// The slaughter player record (reader 0x110e2b0): the base record, 21 ints, 4 floats and a byte
    /// </summary>
    public class BriefingPlayerChaser : BriefingPlayer
    {
        public uint Kills { get; set; }
        public uint KillAssists { get; set; }
        public uint HealAssists { get; set; }
        public uint Deaths { get; set; }
        public uint ChaserKills { get; set; }
        public uint BonusKills { get; set; }
        public uint ChaserDeaths { get; set; }
        public uint TeamKills { get; set; }
        public uint TeamKillDeaths { get; set; }
        public uint Suicides { get; set; }
        public uint RoundWins { get; set; }
        public uint RoundsSurvived { get; set; }
        public uint RoundChaserKills { get; set; }
        public uint RoundBonusKills { get; set; }
        public uint TimesChaser { get; set; }
        public uint ConsecutiveChaser { get; set; }
        public uint ChaserRoundWins { get; set; }
        public float AttackPoints { get; set; }
        public float BonusAttackPoints { get; set; }
        public float HealPoints { get; set; }
        public float ChaserAttackPoints { get; set; }

        public BriefingPlayerChaser(Player plr)
        {
            AccountId = plr.Account.Id;
            Experience = plr.TotalExperience;
            TeamId = plr.Team.Id;
            State = plr.State;
            Mode = plr.Mode;
            IsReady = plr.IsReady;
            TotalScore = plr.Score.GetTotalScore();

            var score = (ChaserPlayerScore)plr.Score;
            Kills = score.Kills;
            KillAssists = score.KillAssists;
            HealAssists = score.HealAssists;
            Deaths = score.SurvivorDeaths;
            ChaserKills = score.ChaserKills;
            BonusKills = score.BonusKills;
            ChaserDeaths = score.ChaserDeaths;
            TeamKills = score.TeamKills;
            TeamKillDeaths = score.TeamKillDeaths;
            Suicides = score.Suicides;
            RoundWins = score.RoundWins;
            RoundsSurvived = score.RoundsSurvived;
            RoundChaserKills = score.RoundChaserKills;
            RoundBonusKills = score.RoundBonusKills;
            TimesChaser = score.TimesChaser;
            ConsecutiveChaser = score.ConsecutiveChaser;
            ChaserRoundWins = score.ChaserRoundWins;
            AttackPoints = score.AttackPoints;
            BonusAttackPoints = score.BonusAttackPoints;
            HealPoints = score.HealPoints;
            ChaserAttackPoints = score.ChaserAttackPoints;
        }

        public override void Serialize(BinaryWriter w)
        {
            base.Serialize(w);

            // Stream order of 0x110e2b0, the comments are the field offsets of the client record
            w.Write(Kills); // a4
            w.Write(KillAssists); // b0
            w.Write(HealAssists); // bc
            w.Write(Deaths); // c8
            w.Write(ChaserKills); // d4
            w.Write(BonusKills); // e0
            w.Write(ChaserDeaths); // ec
            w.Write(TeamKills); // f8
            w.Write(TeamKillDeaths); // 104
            w.Write(Suicides); // 110
            w.Write(RoundWins); // 11c
            w.Write(RoundsSurvived); // 128
            w.Write(RoundChaserKills); // 134
            w.Write(RoundBonusKills); // 140
            w.Write(TimesChaser); // 14c
            w.Write(ConsecutiveChaser); // 158
            w.Write(0); // 170
            w.Write(0); // 17c
            w.Write(0); // 188
            w.Write(ChaserRoundWins); // 164
            w.Write(0); // 194
            w.Write(AttackPoints); // 1a0
            w.Write(BonusAttackPoints); // 1ac
            w.Write(HealPoints); // 1b8
            w.Write(ChaserAttackPoints); // 1c4
            w.Write((byte)0); // 1d0
        }
    }

    public class ChaserPlayerScore : PlayerScore
    {
        private readonly ChaserOptions _options;

        public uint ChaserKills { get; set; }
        public uint BonusKills { get; set; }
        public uint SurvivorDeaths { get; set; }
        public uint ChaserDeaths { get; set; }
        public uint TeamKills { get; set; }
        public uint TeamKillDeaths { get; set; }
        public uint RoundWins { get; set; }
        public uint RoundsSurvived { get; set; }
        public uint RoundChaserKills { get; set; }
        public uint RoundBonusKills { get; set; }
        public uint TimesChaser { get; set; }
        public uint ConsecutiveChaser { get; set; }
        public uint ChaserRoundWins { get; set; }
        public float AttackPoints { get; set; }
        public float BonusAttackPoints { get; set; }
        public float HealPoints { get; set; }
        public float ChaserAttackPoints { get; set; }
        public float RoundAttackPoints { get; set; }

        public ChaserPlayerScore(ChaserOptions options)
        {
            _options = options;
        }

        public override uint GetTotalScore()
        {
            var score = ChaserKills * _options.PointsPerChaserKill +
                        BonusKills * _options.PointsPerBonusKill +
                        RoundWins * _options.PointsPerRoundWin +
                        RoundsSurvived * _options.PointsPerRoundSurvived;
            return (uint)Math.Max(score, 0);
        }

        public override void Reset()
        {
            base.Reset();
            ChaserKills = 0;
            BonusKills = 0;
            SurvivorDeaths = 0;
            ChaserDeaths = 0;
            TeamKills = 0;
            TeamKillDeaths = 0;
            RoundWins = 0;
            RoundsSurvived = 0;
            RoundChaserKills = 0;
            RoundBonusKills = 0;
            TimesChaser = 0;
            ConsecutiveChaser = 0;
            ChaserRoundWins = 0;
            AttackPoints = 0;
            BonusAttackPoints = 0;
            HealPoints = 0;
            ChaserAttackPoints = 0;
            RoundAttackPoints = 0;
        }
    }
}
