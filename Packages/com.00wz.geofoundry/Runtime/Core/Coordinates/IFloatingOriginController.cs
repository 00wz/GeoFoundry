using System;

namespace GeoFoundry.Core.Coordinates
{
    /// <summary>
    /// Describes a floating-origin pivot change; passed to <see cref="IFloatingOriginController.PivotChanged"/>
    /// subscribers.
    /// </summary>
    public readonly struct PivotChangedEventArgs
    {
        /// <summary>The pivot in effect before the rebase.</summary>
        public readonly MercatorPivot OldPivot;

        /// <summary>The pivot in effect after the rebase; equals <see cref="IFloatingOriginController.Current"/>.</summary>
        public readonly MercatorPivot NewPivot;

        /// <summary>Initializes a new instance with the pivots before and after the rebase.</summary>
        public PivotChangedEventArgs(MercatorPivot oldPivot, MercatorPivot newPivot)
        {
            OldPivot = oldPivot;
            NewPivot = newPivot;
        }
    }

    /// <summary>
    /// Owns the current global <see cref="MercatorPivot"/> and notifies subscribers when it changes (rebase).
    /// This is the single synchronization point: <c>TileLifecycleManager</c> (for tiles) and any client code
    /// (for its own objects and systems) subscribe to <see cref="PivotChanged"/> independently of each other.
    /// </summary>
    /// <remarks>
    /// Implementations decide how and when to detect that a rebase is needed;
    /// <see cref="TransformFloatingOriginController"/> is the built-in implementation that follows a given
    /// <c>Transform</c>. This interface describes only what subscribers observe.
    /// </remarks>
    public interface IFloatingOriginController
    {
        /// <summary>Gets the current global pivot. It changes only on rebase (see <see cref="PivotChanged"/>).</summary>
        MercatorPivot Current { get; }

        /// <summary>
        /// Raised synchronously on the main thread at the moment of rebase, after <see cref="Current"/> has been
        /// updated. Subscribers shift their scene objects to the new pivot.
        /// </summary>
        event Action<PivotChangedEventArgs> PivotChanged;
    }
}
