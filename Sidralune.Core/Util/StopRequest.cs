using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sidralune.Core.Util
{
    /// <summary>
    /// The stop request of the background job a calculation runs in, so long loops anywhere end when the job is stopped
    /// without every routine taking a token. The job runner sets it; it flows into Parallel loops and tasks started from there.
    /// </summary>
    public static class StopRequest
    {
        private static readonly AsyncLocal<CancellationToken> Token = new AsyncLocal<CancellationToken>();

        /// <summary>The stop request of the calling job; None outside a job.</summary>
        public static CancellationToken Current => Token.Value;

        /// <summary>Runs action with ct as its stop request (on this thread and in the loops and tasks it starts).</summary>
        public static void Run(CancellationToken ct, Action action)
        {
            var old = Token.Value;
            Token.Value = ct;
            try { action(); }
            finally { Token.Value = old; }
        }

        /// <summary>Throws OperationCanceledException when the job has been stopped.</summary>
        public static void Check() => Token.Value.ThrowIfCancellationRequested();

        /// <summary>Options for a Parallel loop that ends, between iterations, when the job is stopped.</summary>
        public static ParallelOptions Options => new ParallelOptions { CancellationToken = Token.Value };

        /// <summary>ct when it can be cancelled, else the job's stop request.</summary>
        public static CancellationToken Or(CancellationToken ct) => ct.CanBeCanceled ? ct : Token.Value;
    }
}
