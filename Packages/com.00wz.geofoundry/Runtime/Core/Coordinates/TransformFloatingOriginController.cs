using System;
using System.Threading;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Core.Coordinates
{
    /// <summary>
    /// Built-in <see cref="IFloatingOriginController"/> that follows the position of a given
    /// <see cref="Transform"/> (usually the camera). When the target moves farther than the threshold from the
    /// pivot, it moves the pivot to the target, shifts the target itself to the matching position near the new
    /// origin and notifies subscribers.
    /// </summary>
    /// <remarks>
    /// Runs its own per-frame loop on the main thread (<see cref="Awaitable.NextFrameAsync"/>), independent of
    /// <c>TileLifecycleManager</c>. The target must be shifted too: otherwise it would still be far from the new
    /// pivot on the next frame. Exceptions thrown by a subscriber are logged and do not prevent other
    /// subscribers from being notified. After notification, <see cref="Physics.SyncTransforms"/> is called so
    /// that colliders moved by subscribers are in sync immediately.
    /// </remarks>
    public sealed class TransformFloatingOriginController : IFloatingOriginController, IDisposable
    {
        private readonly Transform _target;
        private readonly double _thresholdMeters;
        private readonly CancellationTokenSource _cts = new();
        private bool _started;

        /// <inheritdoc/>
        public MercatorPivot Current { get; private set; }

        /// <inheritdoc/>
        public event Action<PivotChangedEventArgs> PivotChanged;

        /// <summary>Initializes the controller. Call <see cref="Start"/> to begin tracking.</summary>
        /// <param name="initialPivot">The pivot in effect until the first rebase.</param>
        /// <param name="target">The transform to follow, usually the camera.</param>
        /// <param name="thresholdMeters">Distance from the pivot, in meters, beyond which a rebase happens.</param>
        /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
        public TransformFloatingOriginController(MercatorPivot initialPivot, Transform target, double thresholdMeters)
        {
            Current = initialPivot;
            _target = target != null ? target : throw new ArgumentNullException(nameof(target));
            _thresholdMeters = thresholdMeters;
        }

        /// <summary>Starts the tracking loop. Subsequent calls are ignored.</summary>
        public void Start()
        {
            if (_started) return;
            _started = true;
            _ = RunLoop(_cts.Token);
        }

        private async Awaitable RunLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Awaitable.NextFrameAsync(ct); }
                catch (OperationCanceledException) { break; }

                // The loop is fire-and-forget: an escaping exception would silently stop rebasing for good.
                try { Tick(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        /// <summary>
        /// Performs one check-and-rebase step. Separate from <see cref="RunLoop"/> so that tests can drive it
        /// directly: <see cref="Awaitable.NextFrameAsync"/> does not advance in EditMode tests.
        /// </summary>
        internal void Tick()
        {
            if (_target == null) return;

            var pos = _target.position;
            var targetMercator = Current.MercatorFromWorld(new float3(pos.x, 0f, pos.z));
            if (!Current.ShouldRebase(targetMercator, _thresholdMeters)) return;

            Rebase(targetMercator, pos.y);
        }

        private void Rebase(double2 targetMercator, float targetHeight)
        {
            var oldPivot = Current;
            Current = new MercatorPivot(targetMercator);

            // Relative to the new pivot the target is near zero; move it there so it does not stay far away.
            var newWorld = Current.WorldFromMercator(targetMercator, targetHeight);
            _target.position = new Vector3(newWorld.x, newWorld.y, newWorld.z);

            // Invoke each subscriber separately: an exception in one (including client code) must not leave
            // the others, such as tiles, on the old pivot.
            var handlers = PivotChanged;
            if (handlers != null)
            {
                var args = new PivotChangedEventArgs(oldPivot, Current);
                foreach (var handler in handlers.GetInvocationList())
                {
                    try { ((Action<PivotChangedEventArgs>)handler)(args); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            }

            // Subscribers may have moved objects with colliders: sync PhysX now instead of waiting for the next
            // FixedUpdate. Safe even when the scene has no physics.
            Physics.SyncTransforms();
        }

        /// <summary>Stops the tracking loop.</summary>
        public void Dispose()
        {
            try { _cts.Cancel(); _cts.Dispose(); } catch { /* ignore */ }
        }
    }
}
