using System;
using System.Collections.Concurrent;
using System.Linq;

namespace OpenS4L.Server.Game
{
    public enum VoteKickError
    {
        OK,
        AlreadyVoting,
        InvalidTarget,
        NotEnoughPlayers
    }

    /// <summary>
    /// A running vote to kick a player out of a room (GameKickOutRequestReq/GameKickOutVoteResultReq)
    /// </summary>
    public class VoteKick
    {
        public static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);

        private readonly ConcurrentDictionary<ulong, bool> _votes = new ConcurrentDictionary<ulong, bool>();

        public Player Sender { get; }
        public Player Target { get; }
        public VoteKickReason Reason { get; }
        public uint NeededCount { get; }
        public uint YesCount => (uint)_votes.Values.Count(x => x);
        public uint NoCount => (uint)_votes.Values.Count(x => !x);
        public DateTimeOffset StartTime { get; }

        internal VoteKick(Player sender, Player target, VoteKickReason reason, uint neededCount)
        {
            Sender = sender;
            Target = target;
            Reason = reason;
            NeededCount = neededCount;
            StartTime = DateTimeOffset.Now;
        }

        /// <returns>false if the player already voted</returns>
        internal bool Vote(Player plr, bool isYes)
        {
            return _votes.TryAdd(plr.Account.Id, isYes);
        }

        /// <summary>
        /// Players that can vote: everyone in the room except the target and players in gm mode
        /// </summary>
        public static Player[] GetVoters(Room room, Player target)
        {
            return room.Players.Values.Where(x => x != target && !x.IsInGMMode).ToArray();
        }

        /// <summary>
        /// Majority of the voters, but at least 2 so a single player can't kick someone on their own
        /// </summary>
        public static uint GetNeededCount(int voterCount)
        {
            return (uint)Math.Max(2, voterCount / 2 + 1);
        }
    }
}
