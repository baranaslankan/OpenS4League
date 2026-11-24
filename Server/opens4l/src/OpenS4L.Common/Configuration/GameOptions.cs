namespace OpenS4L.Common.Configuration
{
    public class GameOptions
    {
        public bool EnableTutorial { get; set; }

        /// <summary>
        /// Whether staff accounts are flagged as GM to the client. The client then skips some of
        /// its own checks, but also skips the capsule opening animation. Server-side permissions
        /// use the account's security level and don't depend on this. Defaults to true.
        /// </summary>
        public bool ShowGMFlag { get; set; } = true;
        public int MaxLevel { get; set; }
        public int StartLevel { get; set; }
        public int StartPEN { get; set; }
        public int StartAP { get; set; }
        public int StartCoins1 { get; set; }
        public int StartCoins2 { get; set; }

        /// <summary>
        /// How the lobby EXP bar is refreshed after experience changes (match result, admin commands).
        /// false (default): send ExpRefreshInfoAck with the new total experience.
        /// true: re-send PlayerAccountInfoAck (the login message that fills the bar) instead,
        /// in case ExpRefreshInfoAck turns out to mean something else.
        /// </summary>
        public bool ExpRefreshUsesAccountInfo { get; set; }
        public NickRestrictionOptions NickRestrictions { get; set; }
        public ClanOptions ClanOptions { get; set; }
        public int DurabilityLossPerDeath { get; set; }
        public int DurabilityLossPerMinute { get; set; }

        /// <summary>
        /// Match PEN reward settings shared by all modes (the per-mode rates are PENRates in each
        /// mode section). Defaults apply when the section is missing from config.hjson.
        /// </summary>
        public PENRewardOptions PENRewards { get; set; } = new PENRewardOptions();
        public DeathmatchOptions Deathmatch { get; set; }
        public TouchdownOptions Touchdown { get; set; }
        public BattleRoyalOptions BattleRoyal { get; set; }
        public CaptainOptions Captain { get; set; }
        public ChaserOptions Chaser { get; set; }
    }

    public class DeathmatchOptions
    {
        public ExperienceRateOptions ExperienceRates { get; set; }

        /// <summary>Match PEN rates, defaults from point_bonus.x7 (death_match).</summary>
        public PENRateOptions PENRates { get; set; } = new PENRateOptions(15.645f, 0.006f, 0.007f);
        public int PointsPerKill { get; set; }
        public int PointsPerKillAssist { get; set; }
        public int PointsPerHealAssist { get; set; }
        public int PointsPerDeath { get; set; }
    }

    public class TouchdownOptions
    {
        public ExperienceRateOptions ExperienceRates { get; set; }

        /// <summary>Match PEN rates, defaults from point_bonus.x7 (football).</summary>
        public PENRateOptions PENRates { get; set; } = new PENRateOptions(15.645f, 0.006f, 0.007f);
        public int PointsPerGoal { get; set; }
        public int PointsPerGoalAssist { get; set; }
        public int PointsPerOffense { get; set; }
        public int PointsPerOffenseAssist { get; set; }
        public int PointsPerDefense { get; set; }
        public int PointsPerDefenseAssist { get; set; }
        public int PointsPerFumbi { get; set; }
        public int PointsPerKill { get; set; }
        public int PointsPerKillAssist { get; set; }
        public int PointsPerHealAssist { get; set; }
        public int PointsPerDeath { get; set; }
    }

    public class BattleRoyalOptions
    {
        public ExperienceRateOptions ExperienceRates { get; set; }

        /// <summary>Match PEN rates, defaults from point_bonus.x7 (survival).</summary>
        public PENRateOptions PENRates { get; set; } = new PENRateOptions(19.02f, 0.006f, 0.007f);
        public int PointsPerKill { get; set; }
        public int PointsPerKillAssist { get; set; }
        public int PointsPerBonusKill { get; set; }
        public int PointsPerBonusAssist { get; set; }
        public int PointsPerDeath { get; set; }
    }

    public class CaptainOptions
    {
        public ExperienceRateOptions ExperienceRates { get; set; }

        /// <summary>Match PEN rates, defaults from point_bonus.x7 (captain).</summary>
        public PENRateOptions PENRates { get; set; } = new PENRateOptions(15.645f, 0.006f, 0.007f);
        public int PointsPerKill { get; set; }
        public int PointsPerKillAssist { get; set; }
        public int PointsPerCaptainKill { get; set; }
        public int PointsPerRoundWin { get; set; }
        public int PointsPerSuicide { get; set; }
        public int PointsPerHeal { get; set; }
    }

    /// <summary>
    /// Chaser (Slaughter). The points default to what the client adds to its own scoreboard,
    /// the round time defaults to what the client shows (60s + 10s per player above 6, plus an
    /// intro of 7s for the first round and 4s for the others)
    /// </summary>
    public class ChaserOptions
    {
        public ExperienceRateOptions ExperienceRates { get; set; }
        public int PointsPerChaserKill { get; set; } = 2;
        public int PointsPerBonusKill { get; set; } = 4;
        public int PointsPerRoundWin { get; set; } = 5;
        public int PointsPerRoundSurvived { get; set; } = 10;

        /// <summary>Players needed to start the game (at least 2)</summary>
        public int MinPlayers { get; set; } = 2;

        /// <summary>Round time in seconds</summary>
        public int RoundTime { get; set; } = 60;

        /// <summary>Seconds added to the round time for every player above <see cref="RoundTimeExtraPlayerThreshold"/></summary>
        public int RoundTimePerExtraPlayer { get; set; } = 10;
        public int RoundTimeExtraPlayerThreshold { get; set; } = 6;

        /// <summary>Intro before the first round in seconds</summary>
        public int FirstRoundIntro { get; set; } = 7;

        /// <summary>Intro before every other round in seconds</summary>
        public int RoundIntro { get; set; } = 4;

        /// <summary>Wait between two rounds in seconds</summary>
        public int NextRoundDelay { get; set; } = 5;
    }

    public class ExperienceRateOptions
    {
        public float ScoreFactor { get; set; }
        public float FirstPlaceBonus { get; set; }
        public float SecondPlaceBonus { get; set; }
        public float ThirdPlaceBonus { get; set; }
        public float PlayerCountFactor { get; set; }
        public float ExperiencePerMinute { get; set; }
    }

    /// <summary>
    /// Per-mode match PEN rates. The values come from the server table point_bonus.x7 that ships
    /// with the client (ranking_factor, player_count_factor, point_per_min):
    /// PEN = minutes * PointsPerMinute * (1 + score * ScoreFactor + players * PlayerCountFactor)
    /// </summary>
    public class PENRateOptions
    {
        public float PointsPerMinute { get; set; }
        public float ScoreFactor { get; set; }
        public float PlayerCountFactor { get; set; }

        public PENRateOptions()
        {
        }

        public PENRateOptions(float pointsPerMinute, float scoreFactor, float playerCountFactor)
        {
            PointsPerMinute = pointsPerMinute;
            ScoreFactor = scoreFactor;
            PlayerCountFactor = playerCountFactor;
        }
    }

    public class PENRewardOptions
    {
        /// <summary>Global multiplier for the base match PEN (1 = point_bonus.x7 rates).</summary>
        public float Multiplier { get; set; } = 1;

        /// <summary>
        /// Level PEN bonus (point_bonus.x7 level_bonus, enabled for EU by CO_LEVEL_PEN_BONUS in
        /// content_setting.x7). Paid as BonusPENGained.
        /// </summary>
        public bool EnableLevelBonus { get; set; } = true;

        /// <summary>
        /// Level bonus tiers. null = the point_bonus.x7 tiers (10-19: 20%, 20-29: 25%, 30-100: 30%).
        /// Setting this in config.hjson replaces the tiers.
        /// </summary>
        public PENLevelBonusOptions[] LevelBonuses { get; set; }

        /// <summary>PEN Plus boost items (category 5) the player owns add their rate to BonusPENGained.</summary>
        public bool EnableBoostItems { get; set; } = true;

        /// <summary>
        /// Fill the bonus flags of the result briefing (2 = PEN+, 8/16/32 = level bonus 20/25/30%).
        /// The layout is unchanged, only the value; off until checked in game.
        /// </summary>
        public bool SendResultBonusFlags { get; set; }

        public static readonly PENLevelBonusOptions[] DefaultLevelBonuses =
        {
            new PENLevelBonusOptions { MinLevel = 10, MaxLevel = 19, Percent = 20, ResultFlag = 8 },
            new PENLevelBonusOptions { MinLevel = 20, MaxLevel = 29, Percent = 25, ResultFlag = 16 },
            new PENLevelBonusOptions { MinLevel = 30, MaxLevel = 100, Percent = 30, ResultFlag = 32 }
        };
    }

    public class PENLevelBonusOptions
    {
        public int MinLevel { get; set; }
        public int MaxLevel { get; set; }
        public float Percent { get; set; }
        public int ResultFlag { get; set; }
    }
}
