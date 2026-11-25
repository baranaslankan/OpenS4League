using System.Collections.Generic;

namespace OpenS4L.Server.Game.Data
{
    /// <summary>
    /// What a capsule item can give when it is opened. Opening rolls one reward from every group.
    /// </summary>
    public class CapsuleInfo
    {
        public ItemNumber ItemNumber { get; set; }

        /// <summary>
        /// Where the odds come from: a server-side reward table (with rates) or the client's
        /// capsule tooltip (no rates, every listed reward is equally likely).
        /// </summary>
        public CapsuleSource Source { get; set; }

        public IList<CapsuleRewardGroup> Groups { get; set; } = new List<CapsuleRewardGroup>();
    }

    public enum CapsuleSource
    {
        RewardTable,
        Tooltip
    }

    public class CapsuleRewardGroup
    {
        public IList<CapsuleRewardInfo> Rewards { get; set; } = new List<CapsuleRewardInfo>();
    }

    public class CapsuleRewardInfo
    {
        public CapsuleRewardType Type { get; set; }

        /// <summary>For <see cref="CapsuleRewardType.PEN"/>: the amount, or the lower bound of a range.</summary>
        public uint PEN { get; set; }

        /// <summary>For <see cref="CapsuleRewardType.PEN"/> ranges: the upper bound (0 = fixed amount).</summary>
        public uint PENMax { get; set; }

        public ItemNumber ItemNumber { get; set; }
        public ItemPeriodType PeriodType { get; set; }
        public ushort Period { get; set; }
        public byte Color { get; set; }

        /// <summary>Effects (stats) the reward comes with, from the reward table. Empty = the shop's.</summary>
        public uint[] Effects { get; set; } = System.Array.Empty<uint>();

        /// <summary>Relative weight inside its group. 0 for every reward means equal odds.</summary>
        public uint Rate { get; set; }

        /// <summary>
        /// Periods and stats the client's tooltip lists for this reward. When there are any, opening
        /// rolls one of them (by <see cref="CapsuleRewardOption.Weight"/>) in place of
        /// <see cref="PeriodType"/>/<see cref="Period"/>/<see cref="Effects"/>.
        /// </summary>
        public IList<CapsuleRewardOption> Options { get; set; } = new List<CapsuleRewardOption>();
    }

    /// <summary>One period (with its stats) a reward can come with, from the capsule tooltip.</summary>
    public class CapsuleRewardOption
    {
        public ItemPeriodType PeriodType { get; set; }
        public ushort Period { get; set; }

        /// <summary>The stats the tooltip lists; empty when it lists none.</summary>
        public IReadOnlyCollection<CapsuleStat> Stats { get; set; } = System.Array.Empty<CapsuleStat>();

        /// <summary>The tooltip text the stats were read from (for logs).</summary>
        public string StatText { get; set; } = "";

        /// <summary>Effect ids for <see cref="Stats"/>. Empty = the reward's own (reward table), else the shop's.</summary>
        public uint[] Effects { get; set; } = System.Array.Empty<uint>();

        /// <summary>The tooltip says this option has no stat at all.</summary>
        public bool NoEffects { get; set; }

        /// <summary>The tooltip gave stats but no period: the reward's own period applies.</summary>
        public bool KeepsPeriod { get; set; }

        /// <summary>Relative weight among the reward's options.</summary>
        public uint Weight { get; set; } = 1;
    }
}
