using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenS4L.LoadBot
{
    /// <summary>
    /// The bots that share one room. Member 0 is the master: it creates the room and, once every
    /// other member has joined and readied up, starts the match. The others wait for the room id,
    /// join it and ready up.
    /// </summary>
    public class RoomGroup
    {
        private int _joined;
        private int _ready;

        public RoomGroup(int number, int size, GameRule rule, byte map)
        {
            Number = number;
            Size = size;
            Rule = rule;
            Map = map;
        }

        public int Number { get; }
        public int Size { get; }
        public GameRule Rule { get; }
        public byte Map { get; }

        public TaskCompletionSource<uint> RoomId { get; } =
            new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllJoined { get; } =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllReady { get; } =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Stopwatch timestamp of the master's begin-round request; room.loading is measured from it.</summary>
        public long BeginTimestamp;

        public void Joined()
        {
            if (Interlocked.Increment(ref _joined) == Size)
                AllJoined.TrySetResult();
        }

        public void Ready()
        {
            // The master never readies up; it starts the round instead.
            if (Interlocked.Increment(ref _ready) == Size - 1)
                AllReady.TrySetResult();
        }

        /// <summary>A member failed, so the others stop waiting for it.</summary>
        public void Abort(string reason)
        {
            var ex = new InvalidOperationException($"room group {Number}: {reason}");
            RoomId.TrySetException(ex);
            AllJoined.TrySetException(ex);
            AllReady.TrySetException(ex);
        }
    }
}
