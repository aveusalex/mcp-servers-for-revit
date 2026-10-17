using System;
using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Runs small actions in a valid Revit API context (selection, zoom) on request from UI code that
    /// lives outside it, such as the plan approval window's buttons. One ExternalEvent drains a queue.
    /// <see cref="Initialize"/> must be called from a Revit API context (ExternalEvent.Create requirement).
    /// </summary>
    internal sealed class UiActionQueue : IExternalEventHandler
    {
        public static UiActionQueue Instance { get; } = new UiActionQueue();

        private readonly ConcurrentQueue<Action<UIApplication>> _actions = new ConcurrentQueue<Action<UIApplication>>();
        private ExternalEvent _event;

        private UiActionQueue() { }

        public bool IsReady => _event != null;

        public void Initialize()
        {
            if (_event == null) _event = ExternalEvent.Create(this);
        }

        /// <summary>Queue an action; returns false when the queue is not initialized yet.</summary>
        public bool Post(Action<UIApplication> action)
        {
            if (_event == null) return false;
            _actions.Enqueue(action);
            _event.Raise();
            return true;
        }

        public void Execute(UIApplication app)
        {
            while (_actions.TryDequeue(out var action))
            {
                try { action(app); }
                catch { /* each action reports its own errors */ }
            }
        }

        public string GetName() => "MCP UI actions";
    }
}
