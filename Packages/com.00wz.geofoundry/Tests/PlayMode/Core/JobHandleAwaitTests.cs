using System.Collections;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Core;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.Core
{
    /// <summary>
    /// Contract of <see cref="JobHandleAwaitExtensions.WaitNonBlockingAsync"/>: cancellation does not return
    /// while the job is still reading the caller's borrowed data; otherwise the owner would free it under a
    /// running job. The job deterministically blocks on a gate that the test opens.
    /// </summary>
    public sealed class JobHandleAwaitTests
    {
        /// <summary>Gate of <see cref="GatedReadJob"/>: 0 is closed, 1 is open.</summary>
        private static int s_gate;

        /// <summary>
        /// Not Burst-compiled: reads the static gate field. Waits at most <see cref="GateTimeoutMs"/>, so if the
        /// main thread unexpectedly picks up the job, the test fails instead of hanging the Editor.
        /// </summary>
        private struct GatedReadJob : IJob
        {
            [ReadOnly] public NativeArray<float> Borrowed;
            public NativeArray<float> Result;

            public void Execute()
            {
                var timer = Stopwatch.StartNew();
                while (Volatile.Read(ref s_gate) == 0 && timer.ElapsedMilliseconds < GateTimeoutMs) Thread.Yield();
                Result[0] = Borrowed[0];
            }
        }

        private const int GateTimeoutMs = 10_000;

        [UnityTest]
        public IEnumerator WaitNonBlockingAsync_Cancelled_ReturnsOnlyAfterJobCompletes()
        {
            if (JobsUtility.JobWorkerCount == 0) Assert.Ignore("Requires at least one Job System worker thread.");

            Volatile.Write(ref s_gate, 0);
            var borrowed = new NativeArray<float>(1, Allocator.Persistent);
            var result = new NativeArray<float>(1, Allocator.Persistent);
            var handle = new GatedReadJob { Borrowed = borrowed, Result = result }.Schedule();
            JobHandle.ScheduleBatchedJobs();
            using var cts = new CancellationTokenSource();
            try
            {
                var wait = handle.WaitNonBlockingAsync(cts.Token);
                cts.Cancel();
                for (int i = 0; i < 3; i++) yield return null;
                Assert.That(wait.GetAwaiter().IsCompleted, Is.False,
                    "cancellation must not return while the job is reading the borrowed array");

                Volatile.Write(ref s_gate, 1);
                for (int i = 0; i < 60 && !wait.GetAwaiter().IsCompleted; i++) yield return null;
                Assert.That(wait.GetAwaiter().IsCompleted, Is.True, "the job did not complete in time");
                Assert.That(wait.GetAwaiter().GetResult(), Is.False, "cancelled wait returns false");
                Assert.DoesNotThrow(() => borrowed.Dispose(), "the job has completed, so the borrowed array can be freed");
            }
            finally
            {
                Volatile.Write(ref s_gate, 1);
                handle.Complete();
                if (borrowed.IsCreated) borrowed.Dispose();
                result.Dispose();
            }
        }
    }
}
