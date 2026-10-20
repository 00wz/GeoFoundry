using System;
using System.Collections.Generic;

namespace GeoFoundry.Core.Handlers
{
    /// <summary>
    /// Registry of <see cref="ILayerHandler"/> instances keyed by MVT layer name. Several handlers can be
    /// registered for one layer (for example, building → mesh + label + collider); they are called in
    /// registration order.
    /// </summary>
    /// <remarks>Not thread-safe; register handlers on the main thread during scene initialization.</remarks>
    public sealed class LayerHandlerRegistry
    {
        private readonly Dictionary<string, List<ILayerHandler>> _handlers =
            new Dictionary<string, List<ILayerHandler>>(8); 

        private static readonly IReadOnlyList<ILayerHandler> _empty = Array.Empty<ILayerHandler>();

        /// <summary>
        /// Adds a handler for its <see cref="ILayerHandler.LayerName"/>. Adding the same instance again is ignored.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        /// <exception cref="ArgumentException"><see cref="ILayerHandler.LayerName"/> is null or empty.</exception>
        public void Register(ILayerHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (string.IsNullOrEmpty(handler.LayerName))
                throw new ArgumentException("ILayerHandler.LayerName must not be empty.", nameof(handler));

            if (!_handlers.TryGetValue(handler.LayerName, out var list))
            {
                list = new List<ILayerHandler>(2);
                _handlers[handler.LayerName] = list;
            }
            if (!list.Contains(handler)) list.Add(handler);
        }

        /// <summary>Removes a specific handler. Returns true if it was registered.</summary>
        public bool Unregister(ILayerHandler handler)
        {
            if (handler == null) return false;
            if (!_handlers.TryGetValue(handler.LayerName, out var list)) return false;
            bool removed = list.Remove(handler);
            if (list.Count == 0) _handlers.Remove(handler.LayerName);
            return removed;
        }

        /// <summary>Removes all handlers of a layer. Returns the number of handlers removed.</summary>
        public int UnregisterAll(string layerName)
        {
            if (layerName == null) return 0;
            if (!_handlers.TryGetValue(layerName, out var list)) return 0;
            int n = list.Count;
            _handlers.Remove(layerName);
            return n;
        }

        /// <summary>Returns the handlers of a layer in registration order; never null.</summary>
        public IReadOnlyList<ILayerHandler> Get(string layerName)
        {
            if (layerName == null) return _empty;
            return _handlers.TryGetValue(layerName, out var list) ? (IReadOnlyList<ILayerHandler>)list : _empty;
        }

        /// <summary>Gets all registered handlers across all layers.</summary>
        public IEnumerable<ILayerHandler> All
        {
            get
            {
                foreach (var kv in _handlers)
                    foreach (var h in kv.Value) yield return h;
            }
        }

        /// <summary>Gets the total number of registered handlers.</summary>
        public int Count
        {
            get
            {
                int n = 0;
                foreach (var kv in _handlers) n += kv.Value.Count;
                return n;
            }
        }
    }
}
