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

            // A switched-on Unique Booster makes permanent rewards likelier (x1.5..x5) or certain
            // (100%), for the first group that has one ("double capsules: only the first item").
            // Not for skill capsules. It is used up by the capsule even if nothing permanent is in it.
            var booster = GetActiveBooster(plr);
            var boost = booster == null || IsSkillCapsule(info) ? 0f : BoosterFactor(booster.ItemNumber);
            var isPermanent = (Func<CapsuleRewardInfo, bool>)(r => IsPermanent(r, gameData));
            var boosted = false;
            var rolled = new List<(CapsuleRewardInfo Reward, float Boost)>();
            foreach (var group in info.Groups)
            {
                var factor = !boosted && boost > 0 && group.Rewards.Any(isPermanent) ? boost : 0f;
                boosted |= factor > 0;
                var reward = factor > 0 ? Roll(group, random, r => isPermanent(r) ? factor : 1f) : Roll(group, random);
                if (reward == null)
                    continue;
                if (reward.Type != CapsuleRewardType.PEN && gameData.GetShopItem(reward.ItemNumber) == null)
                    continue;
                rolled.Add((reward, factor));
            }

            if (rolled.Count == 0)
                return null;

            plr.Inventory.UseOne(capsule);
            if (booster != null && boost > 0)
            {
                if (booster.PeriodType == ItemPeriodType.Units)
                    plr.Inventory.UseOne(booster);
                if (GetActiveBooster(plr) == null)
                {
                    plr.EquippedBoosts.Remove(booster.Id);
                    SendBoosts(plr);
                }
            }

            var result = new List<CapsuleRewardDto>();
            var penGained = false;
            foreach (var (reward, rewardBoost) in rolled)
            {
                if (reward.Type == CapsuleRewardType.PEN)
                {
                    var amount = RollPen(reward, random);
                    plr.PEN += amount;
                    penGained = true;
                    result.Add(new CapsuleRewardDto { RewardType = CapsuleRewardType.PEN, PEN = amount });
                    continue;
                }

                var (periodType, period, effects, noEffects) = Resolve(reward, random, rewardBoost);
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
            CapsuleRewardInfo reward, Random random, float boost = 0)
        {
            if (reward.Options.Count == 0)
                return (reward.PeriodType, reward.Period, reward.Effects, false);

            // Unique Booster: the permanent option weighs boost times more; 100% makes it certain.
            var options = reward.Options;
            if (boost >= GuaranteedBoost && options.Any(o => IsPermanent(o.PeriodType)))
                options = options.Where(o => IsPermanent(o.PeriodType)).ToList();
            double Weight(CapsuleRewardOption o) =>
                Math.Max(o.Weight, 0u) * (boost > 0 && IsPermanent(o.PeriodType) ? boost : 1.0);

            var option = options[options.Count - 1];
            var total = options.Sum(Weight);
            if (total > 0)
            {
                var pick = random.NextDouble() * total;
                foreach (var o in options)
                {
                    pick -= Weight(o);
                    if (pick < 0)
                    {
                        option = o;
                        break;
                    }
                }
            }
            else
            {
                option = options[random.Next(options.Count)];
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

        /// <summary>Unique Booster factor that means "certain" (the 100% booster).</summary>
        public const float GuaranteedBoost = 1000f;

        /// <summary>Unique Boosters: 5040001..5040006 = x1.5, x2, x3, x4, x5, 100% (item names).</summary>
        public static bool IsUniqueBooster(ItemNumber item) => item.Id / 1000 == 5040;

        public static float BoosterFactor(ItemNumber item) => item.Id switch
        {
            5040001 => 1.5f,
            5040002 => 2f,
            5040003 => 3f,
            5040004 => 4f,
            5040005 => 5f,
            5040006 => GuaranteedBoost,
            _ => 0f
        };

        /// <summary>The Unique Booster the player switched on and still has, or null.</summary>
        public static PlayerItem GetActiveBooster(Player plr)
        {
            foreach (var id in plr.EquippedBoosts)
            {
                var item = plr.Inventory[id];
                if (item != null && IsUniqueBooster(item.ItemNumber) && !item.IsExpiredOrUsedUp())
                    return item;
            }

            return null;
        }

        /// <summary>Tells the client which boosts are switched on (its boost slot).</summary>
        public static void SendBoosts(Player plr)
        {
            plr.EquippedBoosts.RemoveWhere(id => plr.Inventory[id] == null);
            plr.Session?.Send(new OpenS4L.Network.Message.Game.ItemEquipBoostItemInfoAckMessage
            {
                Items = plr.EquippedBoosts.ToArray()
            });
        }

        private static bool IsSkillCapsule(CapsuleInfo info) =>
            info.Groups.SelectMany(g => g.Rewards).Where(r => r.Type == CapsuleRewardType.Item)
                .All(r => r.ItemNumber.Category == ItemCategory.Skill);

        private static bool IsPermanent(ItemPeriodType type) => type == ItemPeriodType.None || type == 0;

        /// <summary>
        /// Whether a reward can come out permanent: a permanent option, or no options and a
        /// permanent period (a set box only when its name gives no period).
        /// </summary>
        public static bool IsPermanent(CapsuleRewardInfo reward, GameDataService gameData)
        {
            if (reward.Type != CapsuleRewardType.Item)
                return false;
            if (reward.Options.Count > 0)
                return reward.Options.Any(o => IsPermanent(o.PeriodType));
            if (!IsPermanent(reward.PeriodType))
                return false;
            if (reward.ItemNumber.Id / 10000 != 405)
                return true;
            var name = gameData?.Items?.GetValueOrDefault(reward.ItemNumber)?.Name;
            return !CapsuleDataParser.TryParseBoxPeriod(name, out _, out _);
        }

        /// <summary>
        /// Weighted pick with each reward's weight multiplied by <paramref name="factor"/>; a factor
        /// of <see cref="GuaranteedBoost"/> or more limits the pick to those rewards.
        /// </summary>
        public static CapsuleRewardInfo Roll(CapsuleRewardGroup group, Random random, Func<CapsuleRewardInfo, float> factor)
        {
            var rewards = group.Rewards;
            if (rewards.Any(r => factor(r) >= GuaranteedBoost))
                rewards = rewards.Where(r => factor(r) >= GuaranteedBoost).ToList();
            if (rewards.Count == 0)
                return null;

            var anyRate = rewards.Any(r => r.Rate > 0);
            double Weight(CapsuleRewardInfo r) => (anyRate ? r.Rate : 1.0) * factor(r);
            var total = rewards.Sum(Weight);
            var pick = random.NextDouble() * total;
            foreach (var reward in rewards)
            {
                pick -= Weight(reward);
                if (pick < 0)
                    return reward;
            }

            return rewards[rewards.Count - 1];
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
