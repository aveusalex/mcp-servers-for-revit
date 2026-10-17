using System;
using System.Collections.Generic;
using System.Threading;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// FIFO of requests waiting for Revit's thread, with the <c>RequestGate</c> semantics per request:
    /// a request Revit hasn't started when its caller stops waiting is cancelled and never runs; a request
    /// Revit has started always runs to completion. So a call reported as "not started" can't change the
    /// model later, and a retry can't apply the change twice.
    /// </summary>
    /// <remarks>
    /// No Revit dependency so it can be tested outside Revit. Queue-and-drain design adapted from KenLP's
    /// RevitMCPExternalEventHandler (MIT, Copyright (c) 2026 Le Phu); the cancel-if-not-started rule is the
    /// one from this repo's RequestGate.
    /// </remarks>
    public sealed class RequestQueue<T>
    {
        internal enum State
        {
            Pending,
            Running,
            Done,
            Cancelled
        }

        public sealed class Ticket
        {
            internal readonly ManualResetEventSlim Completed = new ManualResetEventSlim(false);
            internal State State = State.Pending;

            internal Ticket(T payload)
            {
                Payload = payload;
            }

            public T Payload { get; }

            /// <summary>What the handler produced. Set before the ticket completes.</summary>
            public object Result { get; internal set; }

            /// <summary>The exception the handler threw, if any.</summary>
            public Exception Error { get; internal set; }

            public bool IsCancelled => State == State.Cancelled;
            public bool IsDone => State == State.Done;
        }

        private readonly object _sync = new object();
        private readonly Queue<Ticket> _queue = new Queue<Ticket>();

        /// <summary>Requests waiting for Revit (cancelled ones excluded).</summary>
        public int PendingCount
        {
            get
            {
                lock (_sync)
                {
                    int count = 0;
                    foreach (var t in _queue)
                    {
                        if (t.State == State.Pending) count++;
                    }
                    return count;
                }
            }
        }

        /// <summary>Adds a request. Raise the external event after this, never before.</summary>
        public Ticket Enqueue(T payload)
        {
            var ticket = new Ticket(payload);
            lock (_sync)
            {
                _queue.Enqueue(ticket);
            }
            return ticket;
        }

        /// <summary>
        /// Called on Revit's thread. Returns the next request to run (now marked running), skipping cancelled
        /// ones, or null when the queue is empty.
        /// </summary>
        public Ticket TryStartNext()
        {
            lock (_sync)
            {
                while (_queue.Count > 0)
                {
                    var ticket = _queue.Dequeue();
                    if (ticket.State != State.Pending) continue;
                    ticket.State = State.Running;
                    return ticket;
                }
                return null;
            }
        }

        /// <summary>Called on Revit's thread when the request finished (successfully or not).</summary>
        public void Finish(Ticket ticket, object result, Exception error)
        {
            ticket.Result = result;
            ticket.Error = error;
            lock (_sync)
            {
                ticket.State = State.Done;
            }
            ticket.Completed.Set();
        }

        /// <summary>
        /// Waits up to <paramref name="startTimeoutMilliseconds"/> for Revit to start the request. Returns false
        /// if it didn't, after cancelling it. Once started, waits for it to finish, however long that takes.
        /// </summary>
        public bool Wait(Ticket ticket, int startTimeoutMilliseconds)
        {
            if (ticket.Completed.Wait(startTimeoutMilliseconds))
            {
                return true;
            }

            if (Cancel(ticket))
            {
                return false;
            }

            ticket.Completed.Wait();
            return true;
        }

        /// <summary>Cancels the request if Revit hasn't started it. Returns true if it was cancelled.</summary>
        public bool Cancel(Ticket ticket)
        {
            lock (_sync)
            {
                if (ticket.State != State.Pending)
                {
                    return false;
                }
                ticket.State = State.Cancelled;
                return true;
            }
        }
    }
}
