using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenS4L.Blub.Serialization;
using ProudNet;
using ProudNet.Serialization;

namespace OpenS4L.LoadBot
{
    /// <summary>A ProudNet client connection with a typed wait-for-ack helper.</summary>
    public class ServerConnection : IDisposable
    {
        private readonly object _lock = new object();
        private readonly List<(Type Type, Func<object, bool> Match, TaskCompletionSource<object> Tcs)> _waiters =
            new List<(Type, Func<object, bool>, TaskCompletionSource<object>)>();

        public ProudNetClient Client { get; }

        public ServerConnection(BlubSerializer serializer, MessageFactory[] factories)
        {
            Client = new ProudNetClient(serializer, factories);
            Client.MessageReceived += OnMessage;
        }

        private void OnMessage(object sender, MessageEventArgs e)
        {
            (Type, Func<object, bool>, TaskCompletionSource<object>)[] matched;
            lock (_lock)
            {
                matched = _waiters
                    .Where(w => w.Type.IsInstanceOfType(e.Message) && (w.Match == null || w.Match(e.Message)))
                    .ToArray();

                foreach (var w in matched)
                    _waiters.Remove(w);
            }

            foreach (var w in matched)
                w.Item3.TrySetResult(e.Message);
        }

        /// <summary>Wait for the next inbound message of type <typeparamref name="T"/>.</summary>
        /// <remarks>
        /// The waiter is registered when this is called, so call it before sending the request whose
        /// reply it awaits; otherwise a fast reply can arrive before anyone is waiting for it.
        /// </remarks>
        public async Task<T> WaitFor<T>(TimeSpan timeout, Func<T, bool> match = null) where T : class
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<object, bool> predicate = match == null ? null : m => match((T)m);
            lock (_lock)
                _waiters.Add((typeof(T), predicate, tcs));

            // Awaited (not ContinueWith) so a timeout surfaces as TimeoutException, not AggregateException.
            return (T)await tcs.Task.WaitAsync(timeout);
        }

        public void Dispose()
        {
            Client.MessageReceived -= OnMessage;
            Client.Dispose();
        }
    }
}
