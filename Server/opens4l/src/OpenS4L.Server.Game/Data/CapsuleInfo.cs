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

        /// <summary>Relative weight inside its group. 0 for every reward means equal odds.</summary>
        public uint Rate { get; set; }
    }
}
