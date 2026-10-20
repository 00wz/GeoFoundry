using System.Threading;
using Unity.Jobs;
using UnityEngine;

namespace GeoFoundry.Core
{
    /// <summary>
    /// Non-blocking waiting for a <see cref="JobHandle"/>: polls <see cref="JobHandle.IsCompleted"/> once per frame
    /// instead of calling the blocking <see cref="JobHandle.Complete"/> right away.
    /// </summary>
    /// <remarks>
    /// The job never outlives the call that awaits it: the method returns only after the job has actually finished
    /// and <see cref="JobHandle.Complete"/> has been called, including after <c>ct</c> is cancelled. Jobs usually
    /// read data borrowed from the caller (terrain grids, the MVT layer, building properties) that its owners
    /// release right after the caller returns; returning earlier would free that data under a running job
    /// (<c>InvalidOperationException</c> in the Editor, use-after-free in a player). Therefore, even after a
    /// <c>false</c> result, all native containers of the job can be released with an ordinary <c>Dispose()</c>.
    /// </remarks>
    public static class JobHandleAwaitExtensions
    {
        /// <summary>
        /// Waits for <paramref name="handle"/> to complete without blocking the main thread, then completes it.
        /// Must be called on the main thread.
        /// </summary>
        /// <param name="handle">The scheduled job to wait for.</param>
        /// <param name="ct">Signals that the result is no longer needed; it does not shorten the wait.</param>
        /// <returns>
        /// <c>true</c> if the job has completed and the result is needed; <c>false</c> if the job has also completed
        /// but <paramref name="ct"/> was cancelled, so the result is not needed. Cancellation neither interrupts
        /// the wait nor blocks the thread: the job is awaited over the following frames.
        /// </returns>
        public static async Awaitable<bool> WaitNonBlockingAsync(this JobHandle handle, CancellationToken ct)
        {
            while (!handle.IsCompleted)
                await Awaitable.NextFrameAsync(CancellationToken.None);
            handle.Complete();
            return !ct.IsCancellationRequested;
        }
    }
}
