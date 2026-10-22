using System;

namespace Cxt1024Bridge
{
    internal sealed class LayerChangedEventArgs : EventArgs
    {
        internal int Layer;
        internal bool Manual;
        internal ForegroundInfo Foreground;
    }

    internal sealed class LayerManager
    {
        private readonly object _gate = new object();
        private int _currentLayer;
        private bool _manual;
        private IntPtr _manualAnchor;
        private ForegroundInfo _foreground;
        private IntPtr _lastTerminalWindow;
        private long _contextGeneration;
        private long _contentGeneration;

        internal event EventHandler<LayerChangedEventArgs> LayerChanged;

        internal int CurrentLayer
        {
            get { lock (_gate) return _currentLayer; }
        }

        internal bool IsManual
        {
            get { lock (_gate) return _manual; }
        }

        internal ForegroundInfo CurrentForeground
        {
            get { lock (_gate) return _foreground; }
        }

        internal long ContextGeneration
        {
            get { lock (_gate) return _contextGeneration; }
        }

        // Presentation titles are not physical input/window identities.
        internal long ContentGeneration
        {
            get { lock (_gate) return _contentGeneration; }
        }

        internal IntPtr LastTerminalWindow
        {
            get { lock (_gate) return _lastTerminalWindow; }
        }

        internal void ObserveForeground(ForegroundInfo foreground)
        {
            if (foreground == null)
                return;
            LayerChangedEventArgs change = null;
            lock (_gate)
            {
                ForegroundInfo previous = _foreground;
                IntPtr previousWindow = previous == null ? IntPtr.Zero : previous.Window;
                bool windowChanged = previousWindow != foreground.Window;
                if (!SameContext(previous, foreground))
                    _contextGeneration++;
                if (!SameContext(previous, foreground) || previous == null ||
                    !string.Equals(previous.Title, foreground.Title, StringComparison.Ordinal))
                    _contentGeneration++;
                _foreground = foreground;
                if (foreground.AutomaticLayer == 2 && foreground.Window != IntPtr.Zero)
                    _lastTerminalWindow = foreground.Window;

                bool manualEnded = _manual && windowChanged && foreground.Window != _manualAnchor;
                if (manualEnded)
                {
                    _manual = false;
                    _manualAnchor = IntPtr.Zero;
                }

                if (!_manual && (_currentLayer != foreground.AutomaticLayer || manualEnded))
                {
                    _currentLayer = foreground.AutomaticLayer;
                    change = SnapshotChange();
                }
            }
            Raise(change);
        }

        internal void SetManualLayer(int layer)
        {
            if (layer < 0 || layer > 7)
                throw new ArgumentOutOfRangeException("layer");
            LayerChangedEventArgs change;
            lock (_gate)
            {
                _manual = true;
                _manualAnchor = _foreground == null
                    ? NativeMethods.GetForegroundWindow()
                    : _foreground.Window;
                _currentLayer = layer;
                _contextGeneration++;
                change = SnapshotChange();
            }
            Raise(change);
        }

        internal void InvalidateInputContext()
        {
            lock (_gate) { _contextGeneration++; }
        }

        internal void ReturnToAutomatic()
        {
            LayerChangedEventArgs change;
            lock (_gate)
            {
                _manual = false;
                _manualAnchor = IntPtr.Zero;
                _currentLayer = _foreground == null ? 0 : _foreground.AutomaticLayer;
                _contextGeneration++;
                change = SnapshotChange();
            }
            Raise(change);
        }

        private LayerChangedEventArgs SnapshotChange()
        {
            return new LayerChangedEventArgs
            {
                Layer = _currentLayer,
                Manual = _manual,
                Foreground = _foreground
            };
        }

        private static bool SameContext(ForegroundInfo left, ForegroundInfo right)
        {
            if (left == null || right == null)
                return left == right;
            return left.Window == right.Window &&
                left.ProcessId == right.ProcessId &&
                left.AutomaticLayer == right.AutomaticLayer;
        }

        private void Raise(LayerChangedEventArgs args)
        {
            if (args == null)
                return;
            EventHandler<LayerChangedEventArgs> handler = LayerChanged;
            if (handler != null)
                handler(this, args);
        }
    }
}
