using System;
using System.Collections.Generic;
using System.Linq;
using OpenS4L.Network.Data.Game;
using OpenS4L.Server.Game.Data;
using OpenS4L.Server.Game.Services;

namespace OpenS4L.Server.Game
{
    /// <summary>
    /// Opens capsule items: rolls one reward per group, grants it and removes the capsule.
    /// </summary>
    public static class CapsuleOpener
    {
        /// <summary>Result code sent with the rewards. Taken from other S4 emulators; unconfirmed against official docs.</summary>
        public const byte ResultSuccess = 3;
        public const byte ResultFailed = 1;

        /// <summary>
        /// Returns the rewards that were granted, or null when the capsule could not be opened
        /// (unknown capsule, or none of its rolled rewards exist in the shop). On null nothing changes.
        /// </summary>
        public static CapsuleRewardDto[] Open(Player plr, PlayerItem capsule, GameDataService gameData, Random random)
        {
            if (!gameData.Capsules.TryGetValue(capsule.ItemNumber, out var info))
                return null;

            var rolled = info.Groups
                .Select(group => Roll(group, random))
                .Where(reward => reward != null)
                .Where(reward => reward.Type == CapsuleRewardType.PEN || gameData.GetShopItem(reward.ItemNumber) != null)
                .ToList();

            if (rolled.Count == 0)
                return null;

            plr.Inventory.UseOne(capsule);

            var result = new List<CapsuleRewardDto>();
            var penGained = false;
            foreach (var reward in rolled)
            {
                if (reward.Type == CapsuleRewardType.PEN)
                {
                    var amount = RollPen(reward, random);
                    plr.PEN += amount;
                    penGained = true;
                    result.Add(new CapsuleRewardDto { RewardType = CapsuleRewardType.PEN, PEN = amount });
                    continue;
                }

                var (periodType, period, effects, noEffects) = Resolve(reward, random);
                var item = plr.Inventory.CreateFromShop(reward.ItemNumber, periodType, period, reward.Color, effects,
                    noEffects);
                if (item == null)
                    continue;

                result.Add(new CapsuleRewardDto
                {
                    RewardType = CapsuleRewardType.Item,
                    ItemNumber = item.ItemNumber,
                    PriceType = item.PriceType,
                    PeriodType = item.PeriodType,
                    Period = item.Period,
                    Unk1 = item.Color,
                    Effect = item.Effects.FirstOrDefault()
                });
            }

            if (penGained)
                plr.SendMoneyUpdate();

            return result.ToArray();
        }

        /// <summary>
        /// The period and effects a rolled item reward comes with: one of its tooltip options (by
        /// weight) when it has any, else its own. An option without effect ids keeps the reward's
        /// own effects (reward table), and with none of those the shop's.
        /// </summary>
        public static (ItemPeriodType PeriodType, ushort Period, uint[] Effects, bool NoEffects) Resolve(
            CapsuleRewardInfo reward, Random random)
        {
            if (reward.Options.Count == 0)
                return (reward.PeriodType, reward.Period, reward.Effects, false);

            var option = reward.Options[reward.Options.Count - 1];
            var total = reward.Options.Sum(x => (long)x.Weight);
            if (total > 0)
            {
                var pick = random.NextInt64(total);
                foreach (var o in reward.Options)
                {
                    pick -= o.Weight;
                    if (pick < 0)
                    {
                        option = o;
                        break;
                    }
                }
            }
            else
            {
                option = reward.Options[random.Next(reward.Options.Count)];
            }

            if (option.NoEffects)
                return (option.PeriodType, option.Period, Array.Empty<uint>(), true);
            return (option.PeriodType, option.Period, option.Effects.Length > 0 ? option.Effects : reward.Effects, false);
        }

        /// <summary>
        /// A PEN amount. Ranges ("PEN 1,000~100,000") give no odds; rolling them evenly would average
        /// half the top, so the roll is even on a log scale: as likely 1,000-10,000 as 10,000-100,000.
        /// </summary>
        public static uint RollPen(CapsuleRewardInfo reward, Random random)
        {
            if (reward.PENMax <= reward.PEN)
                return reward.PEN;

            var min = Math.Max(1.0, reward.PEN);
            var amount = Math.Exp(Math.Log(min) + random.NextDouble() * (Math.Log(reward.PENMax) - Math.Log(min)));
            return (uint)Math.Clamp(Math.Round(amount), reward.PEN, reward.PENMax);
        }

        /// <summary>Weighted pick by Rate; equal odds when no reward in the group has a rate.</summary>
        public static CapsuleRewardInfo Roll(CapsuleRewardGroup group, Random random)
        {
            if (group.Rewards.Count == 0)
                return null;

            var total = group.Rewards.Sum(x => (long)x.Rate);
            if (total <= 0)
                return group.Rewards[random.Next(group.Rewards.Count)];

            var pick = random.NextInt64(total);
            foreach (var reward in group.Rewards)
            {
                pick -= reward.Rate;
                if (pick < 0)
                    return reward;
            }

            return group.Rewards[group.Rewards.Count - 1];
        }
    }
}
