using System.Threading;
using System.Threading.Tasks;
using RevitMCPCommandSet.Framework;
using Xunit;

namespace RevitMCPFramework.Tests
{
    public class RequestQueueTests
    {
        [Fact]
        public void Runs_in_fifo_order()
        {
            var queue = new RequestQueue<string>();
            queue.Enqueue("a");
            queue.Enqueue("b");
            Assert.Equal(2, queue.PendingCount);
            Assert.Equal("a", queue.TryStartNext().Payload);
            Assert.Equal("b", queue.TryStartNext().Payload);
            Assert.Null(queue.TryStartNext());
        }

        [Fact]
        public void Not_started_request_is_cancelled_on_timeout_and_never_runs()
        {
            var queue = new RequestQueue<string>();
            var ticket = queue.Enqueue("late");

            Assert.False(queue.Wait(ticket, 20));
            Assert.True(ticket.IsCancelled);
            Assert.Equal(0, queue.PendingCount);
            Assert.Null(queue.TryStartNext()); // Revit finally gets to it: skipped
        }

        [Fact]
        public void Cancelled_request_is_skipped_but_later_ones_run()
        {
            var queue = new RequestQueue<string>();
            var first = queue.Enqueue("first");
            queue.Enqueue("second");
            Assert.True(queue.Cancel(first));
            Assert.Equal("second", queue.TryStartNext().Payload);
        }

        [Fact]
        public void Started_request_is_not_cancelled_and_is_awaited_to_completion()
        {
            var queue = new RequestQueue<string>();
            var ticket = queue.Enqueue("slow");
            var running = queue.TryStartNext();
            Assert.False(queue.Cancel(ticket));

            var finisher = Task.Run(() =>
            {
                Thread.Sleep(150); // longer than the start timeout below
                queue.Finish(running, "done", null);
            });

            Assert.True(queue.Wait(ticket, 10));
            Assert.True(ticket.IsDone);
            Assert.Equal("done", ticket.Result);
            finisher.Wait();
        }

        [Fact]
        public void Finished_request_returns_immediately_with_error()
        {
            var queue = new RequestQueue<int>();
            var ticket = queue.Enqueue(1);
            var running = queue.TryStartNext();
            var error = new System.InvalidOperationException("boom");
            queue.Finish(running, null, error);

            Assert.True(queue.Wait(ticket, 1000));
            Assert.Same(error, ticket.Error);
            Assert.False(queue.Cancel(ticket));
        }
    }
}
