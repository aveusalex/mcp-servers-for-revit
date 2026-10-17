using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>A call waiting for Revit's thread. The target docId is captured when it's queued.</summary>
    internal sealed class ToolRequest
    {
        public ToolRequest(string tool, JObject args, string requestId, string docId)
        {
            Tool = tool;
            Args = args;
            RequestId = requestId;
            DocId = docId;
        }

        public string Tool { get; }
        public JObject Args { get; }
        public string RequestId { get; }
        public string DocId { get; }
    }

    /// <summary>
    /// The single ExternalEvent handler for every framework tool. Callers queue a request and wait; Revit drains
    /// the queue on its thread. A request Revit hasn't started within <see cref="StartTimeoutMilliseconds"/> is
    /// cancelled (never runs) and answered with REVIT_BUSY; a started request runs to completion.
    /// </summary>
    /// <remarks>Queue-and-drain design adapted from KenLP's RevitMCPExternalEventHandler (MIT, Copyright (c) 2026 Le Phu).</remarks>
    public sealed class ToolDispatcher : IExternalEventHandler
    {
        /// <summary>
        /// How long a call may wait for Revit to start it. Below the broker's 50 s "running" threshold so a call
        /// that never started gets a clear answer within the synchronous window.
        /// </summary>
        public const int StartTimeoutMilliseconds = 45000;

        private static readonly object CreateLock = new object();

        [ThreadStatic]
        private static bool _onRevitThread;

        private readonly RequestQueue<ToolRequest> _queue = new RequestQueue<ToolRequest>();
        private ExternalEvent _event;

        private ToolDispatcher()
        {
        }

        public static ToolDispatcher Instance { get; private set; }

        /// <summary>Requests waiting for Revit.</summary>
        public int PendingCount => _queue.PendingCount;

        /// <summary>Creates the dispatcher and its ExternalEvent once. Must run on Revit's thread.</summary>
        internal static ToolDispatcher EnsureCreated(UIApplication uiApp)
        {
            lock (CreateLock)
            {
                if (Instance != null) return Instance;
                var dispatcher = new ToolDispatcher();
                dispatcher._event = ExternalEvent.Create(dispatcher);
                DocumentChangeTracker.EnsureSubscribed(uiApp?.Application);
                // get_session_changes reports changes since the add-in started, not since its first call.
                if (uiApp?.Application != null)
                    RevitMCPCommandSet.Tools.Query.Support.SessionChangeLog.EnsureSubscribed(uiApp.Application);
                Instance = dispatcher;
                return dispatcher;
            }
        }

        /// <summary>Queues a call and waits for its envelope. Called on the plugin's socket thread.</summary>
        public JObject Submit(string tool, JObject args, string requestId)
        {
            var request = new ToolRequest(tool, args ?? new JObject(), requestId, RevitDocumentContext.CurrentTargetDocId);

            if (_onRevitThread)
            {
                // Already on Revit's thread (a tool calling another): waiting would deadlock, so run inline.
                return Process(null, request);
            }

            var ticket = _queue.Enqueue(request);
            var raised = _event.Raise();
            if (raised == ExternalEventRequest.Denied || raised == ExternalEventRequest.TimedOut)
            {
                if (_queue.Cancel(ticket))
                {
                    return NotStarted(tool, "Revit refused to schedule the request");
                }
            }

            if (!_queue.Wait(ticket, StartTimeoutMilliseconds))
            {
                return NotStarted(tool, $"Revit didn't start it within {StartTimeoutMilliseconds / 1000} s");
            }

            if (ticket.Result is JObject envelope) return envelope;
            return EnvelopeJson.ToJson(ToolErrors.Fail(ticket.Error ?? new InvalidOperationException("The tool returned no result.")));
        }

        public void Execute(UIApplication app)
        {
            _onRevitThread = true;
            try
            {
                RequestQueue<ToolRequest>.Ticket ticket;
                while ((ticket = _queue.TryStartNext()) != null)
                {
                    JObject envelope = null;
                    Exception error = null;
                    try
                    {
                        envelope = Process(app, ticket.Payload);
                    }
                    catch (Exception ex)
                    {
                        // Keep exceptions out of Revit; the caller gets them as INTERNAL_ERROR.
                        error = ex;
                    }
                    finally
                    {
                        _queue.Finish(ticket, envelope, error);
                    }
                }
            }
            finally
            {
                _onRevitThread = false;
            }
        }

        public string GetName() => "Revit MCP tool dispatcher";

        private JObject Process(UIApplication app, ToolRequest request)
        {
            app = app ?? _lastApp;
            _lastApp = app;

            if (!ToolRegistry.TryGet(request.Tool, out var tool))
            {
                return EnvelopeJson.ToJson(ToolErrors.Fail(ToolErrorCodes.UnknownTool,
                    $"There is no tool named '{request.Tool}'.", "The add-in may be older than the server; check get_session_context."));
            }

            var args = (JObject)request.Args.DeepClone();
            bool dryRun = TakeDryRun(args);
            args.Remove("document"); // routing argument, consumed by the server/broker

            var doc = string.IsNullOrEmpty(request.DocId)
                ? RevitDocumentContext.ResolveDocument(app)
                : RevitDocumentContext.TryResolveDocument(app, request.DocId);

            var result = ToolRunner.Run(app, doc, tool, args, dryRun, request.RequestId, batchId: null, insideGroup: false);
            return EnvelopeJson.ToJson(result);
        }

        private UIApplication _lastApp;

        /// <summary>Removes the injected <c>dryRun</c> flag from the args and returns it.</summary>
        internal static bool TakeDryRun(JObject args)
        {
            var token = args["dryRun"];
            if (token == null) return false;
            args.Remove("dryRun");
            if (token.Type == JTokenType.Boolean) return (bool)token;
            if (token.Type == JTokenType.String) return string.Equals((string)token, "true", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private static JObject NotStarted(string tool, string why)
        {
            return EnvelopeJson.ToJson(ToolErrors.Fail(ToolErrorCodes.RevitBusy,
                $"{why}, so '{tool}' was cancelled and nothing was changed.",
                "Revit only runs add-in requests while it's idle: ask the user to close any open dialog or finish the current command, then retry."));
        }
    }
}
