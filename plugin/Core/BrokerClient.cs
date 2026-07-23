using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;
using RevitMCPSDK.API.Models.JsonRPC;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// <para>Replaces the old <c>SocketService</c> TcpListener. Instead of binding a
    /// port and waiting for the MCP server to dial in on a manual click, the
    /// plugin now connects OUT to the broker at <c>ws://127.0.0.1:8090</c> the
    /// moment Revit starts, and re-connects automatically if the broker drops.</para>
    ///
    /// <para>Responsibilities: authenticate with the shared token, register every
    /// open document and heartbeat the set every 5s, receive command envelopes
    /// <c>{correlationId, docId, command, params}</c>, resolve the target document,
    /// enforce the doc-agnostic / ui-bound scope policy, dispatch on the UI thread,
    /// and send the response back tagged with the same correlationId.</para>
    /// </summary>
    public class BrokerClient
    {
        private static BrokerClient _instance;
        public static BrokerClient Instance => _instance ?? (_instance = new BrokerClient());

        // Commands that only make sense against the active document. Used as a
        // safety net when the deployed config does not carry a "scope" field.
        private static readonly HashSet<string> DefaultUiBound = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get_current_view_info", "get_current_view_elements", "get_selected_elements",
            "create_dimensions", "tag_walls", "tag_rooms", "operate_element", "color_splash",
        };

        private readonly ICommandRegistry _commandRegistry;
        private ILogger _logger;
        private CommandExecutor _commandExecutor;
        private ConfigurationManager _configManager;

        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private System.Threading.Timer _heartbeatTimer;

        private string _brokerUrl = "ws://127.0.0.1:8090";
        private string _token;
        private bool _commandsLoaded;
        private bool _initialized;
        private volatile bool _running;
        private UIControlledApplication _uiCtrlApp;

        public bool IsRunning => _running;

        private BrokerClient()
        {
            _commandRegistry = new RevitCommandRegistry();
            _logger = new Logger();
        }

        /// <summary>
        /// Wire up (but do not block on) the connection. Safe to call from
        /// <c>OnStartup</c> where only a UIControlledApplication exists: the heavy
        /// command-registry load is deferred until a document is actually active.
        /// </summary>
        public void Initialize(UIControlledApplication uiCtrlApp)
        {
            if (_initialized) return; // idempotent: startup wires this once
            _initialized = true;

            _token = BrokerToken.LoadOrCreate();
            _uiCtrlApp = uiCtrlApp;

            _configManager = new ConfigurationManager(_logger);
            _configManager.LoadConfiguration();
            if (_configManager.Config?.Settings != null &&
                !string.IsNullOrEmpty(_configManager.Config.Settings.BrokerUrl))
            {
                _brokerUrl = _configManager.Config.Settings.BrokerUrl;
            }

            DocumentSessionManager.Instance.Initialize(uiCtrlApp, _logger);
            // Push a fresh registration whenever the document set/active doc changes.
            DocumentSessionManager.Instance.StateChanged += OnDocumentStateChanged;

            // The command registry constructs command instances, which create
            // ExternalEvents — and ExternalEvent.Create must run on Revit's UI
            // thread with a live document. Idling gives us exactly that context.
            // We keep listening until the load succeeds, then unsubscribe.
            uiCtrlApp.Idling += OnIdleLoadCommands;
        }

        private void OnIdleLoadCommands(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (_commandsLoaded) return;
            if (EnsureCommandsLoaded() && _uiCtrlApp != null)
            {
                _uiCtrlApp.Idling -= OnIdleLoadCommands;
            }
        }

        /// <summary>Start the connect/heartbeat loop. Called automatically on startup.</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            Task.Run(() => ConnectLoopAsync(_cts.Token));
            _heartbeatTimer = new System.Threading.Timer(
                _ => SafeFireAndForget(SendHeartbeatAsync), null, 5000, 5000);
            _logger.Info("BrokerClient started, dialing {0}", _brokerUrl);
        }

        /// <summary>Kill switch: drop the connection to the broker and stop heartbeating.</summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _heartbeatTimer?.Dispose(); } catch { }
            _heartbeatTimer = null;
            try { _cts?.Cancel(); } catch { }
            try
            {
                _ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "kill switch",
                    CancellationToken.None).Wait(1000);
            }
            catch { }
            _ws = null;
            _logger.Info("BrokerClient stopped (kill switch)");
        }

        // --- connection loop with exponential backoff -----------------------------

        private async Task ConnectLoopAsync(CancellationToken token)
        {
            int backoff = 1000; // 1s, doubling up to a 30s ceiling
            while (_running && !token.IsCancellationRequested)
            {
                try
                {
                    _ws = new ClientWebSocket();
                    await _ws.ConnectAsync(new Uri(_brokerUrl), token);
                    await HandshakeAsync(token);
                    backoff = 1000; // reset after a good connection
                    _logger.Info("Connected to broker");

                    await SendRegisterAsync();
                    await ReceiveLoopAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Warning("Broker connection failed: {0}. Retrying in {1}ms", ex.Message, backoff);
                }

                if (!_running) break;
                try { await Task.Delay(backoff, token); } catch { break; }
                backoff = Math.Min(backoff * 2, 30000);
            }
        }

        private async Task HandshakeAsync(CancellationToken token)
        {
            var hello = new JObject
            {
                ["type"] = "hello",
                ["role"] = "plugin",
                ["token"] = _token,
                ["sessionId"] = DocumentSessionManager.Instance.SessionId,
                ["revitVersion"] = DocumentSessionManager.Instance.RevitVersion,
            };
            await SendRawAsync(hello.ToString(Formatting.None));

            string reply = await ReceiveMessageAsync(token);
            var msg = JObject.Parse(reply);
            if ((string)msg["type"] != "welcome")
                throw new Exception($"Broker refused handshake: {(string)msg["error"]?["code"]}");
        }

        // --- receiving ------------------------------------------------------------

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            while (_running && _ws != null && _ws.State == WebSocketState.Open)
            {
                string message = await ReceiveMessageAsync(token);
                if (message == null) break; // socket closed
                HandleEnvelope(message);
            }
        }

        private async Task<string> ReceiveMessageAsync(CancellationToken token)
        {
            var buffer = new byte[8192];
            var sb = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    return null;
                }
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);
            return sb.ToString();
        }

        private void HandleEnvelope(string json)
        {
            JObject env;
            try { env = JObject.Parse(json); }
            catch { return; }

            if ((string)env["type"] != "command") return; // only commands are actionable here

            string correlationId = (string)env["correlationId"];
            string docId = (string)env["docId"];
            string command = (string)env["command"];
            JToken paramsToken = env["params"] ?? new JObject();

            try
            {
                var (result, error) = Dispatch(docId, command, paramsToken);
                SafeFireAndForget(() => SendResponseAsync(correlationId, result, error));
            }
            catch (Exception ex)
            {
                SafeFireAndForget(() => SendResponseAsync(correlationId, null,
                    BrokerError("INTERNAL_ERROR", ex.Message)));
            }
        }

        // --- dispatch (Fase 3 targeting + Fase 4 scope policy) --------------------

        private (JToken result, JObject error) Dispatch(string docId, string command, JToken paramsToken)
        {
            // Resolve the target document by its stable docId.
            Document target = DocumentSessionManager.Instance.ResolveDocument(docId);
            if (target == null)
                return (null, BrokerError("DOCUMENT_NOT_FOUND",
                    $"No open document with id {docId}. It may have been closed."));

            // The command registry loads on the UI thread via Idling. If it hasn't
            // yet (no document has been active), we cannot dispatch — never load
            // here, because ExternalEvent.Create off the UI thread is unsupported.
            if (!_commandsLoaded)
                return (null, BrokerError("NOT_READY",
                    "No active Revit document yet; open or click into a project and retry."));

            string scope = ResolveScope(command);
            string activeDocId = DocumentSessionManager.Instance.ActiveDocId;
            bool isActive = docId == activeDocId;
            bool allowAutoActivate = _configManager.Config?.Settings?.AllowAutoActivate ?? false;
            bool allowBackgroundWrites = _configManager.Config?.Settings?.AllowBackgroundWrites ?? false;

            // ui-bound commands need the target to BE the active document.
            if (string.Equals(scope, "ui-bound", StringComparison.OrdinalIgnoreCase) && !isActive)
            {
                if (allowAutoActivate)
                {
                    // Fase 4 Opção B: activate, run, restore. Off unless opted in.
                    return DispatchWithAutoActivate(target, command, paramsToken, activeDocId);
                }
                return (null, BrokerError("REQUIRES_ACTIVE_DOCUMENT",
                    $"Command '{command}' needs its target document to be active. " +
                    $"Active: {activeDocId ?? "(none)"}, requested: {docId}. " +
                    "Ask the user to bring that project's window to the front, or reorder the work."));
            }

            // Guard-rail: writing to a non-active (background) document is opt-in.
            if (!isActive && !allowBackgroundWrites && IsLikelyWrite(command))
                return (null, BrokerError("BACKGROUND_WRITE_BLOCKED",
                    $"Write command '{command}' targets a non-active document; background writes are " +
                    "disabled. Activate the document or enable allowBackgroundWrites."));

            return ExecuteResolved(target, command, paramsToken);
        }

        private (JToken result, JObject error) DispatchWithAutoActivate(
            Document target, string command, JToken paramsToken, string previousActiveDocId)
        {
            // NOTE: switching the active document under the user is intentionally
            // gated behind allowAutoActivate. Best-effort restore afterwards.
            try
            {
                var uiApp = DocumentSessionManager.Instance.GetUIApplication();
                uiApp?.OpenAndActivateDocument(target.PathName);
                return ExecuteResolved(target, command, paramsToken);
            }
            finally
            {
                try
                {
                    var prev = DocumentSessionManager.Instance.ResolveDocument(previousActiveDocId);
                    if (prev != null && !string.IsNullOrEmpty(prev.PathName))
                        DocumentSessionManager.Instance.GetUIApplication()?.OpenAndActivateDocument(prev.PathName);
                }
                catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Publish the resolved docId on an AppDomain data slot that the command
        /// handlers read (see commandset RevitDocumentContext), then execute. The
        /// slot is process-global, which is safe because commands are serialized
        /// onto Revit's single UI thread — one runs to completion before the next.
        /// </summary>
        private (JToken result, JObject error) ExecuteResolved(Document target, string command, JToken paramsToken)
        {
            string docId = DocumentSessionManager.DocIdOf(target);
            AppDomain.CurrentDomain.SetData("RevitMCP.TargetDocId", docId);
            try
            {
                var request = BuildRequest(command, paramsToken);
                string responseJson = _commandExecutor.ExecuteCommand(request);
                var response = JObject.Parse(responseJson);

                if (response["error"] != null)
                    return (null, (JObject)response["error"]);
                return (response["result"], null);
            }
            finally
            {
                AppDomain.CurrentDomain.SetData("RevitMCP.TargetDocId", null);
            }
        }

        private JsonRPCRequest BuildRequest(string command, JToken paramsToken)
        {
            var rpc = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = command,
                ["params"] = paramsToken,
                ["id"] = Guid.NewGuid().ToString(),
            };
            return JsonConvert.DeserializeObject<JsonRPCRequest>(rpc.ToString());
        }

        private string ResolveScope(string command)
        {
            if (_configManager?.Config?.Commands != null)
            {
                foreach (var c in _configManager.Config.Commands)
                {
                    if (string.Equals(c.CommandName, command, StringComparison.OrdinalIgnoreCase))
                        return string.IsNullOrEmpty(c.Scope) ? "doc-agnostic" : c.Scope;
                }
            }
            return DefaultUiBound.Contains(command) ? "ui-bound" : "doc-agnostic";
        }

        private static bool IsLikelyWrite(string command)
        {
            if (string.IsNullOrEmpty(command)) return false;
            return command.StartsWith("create_", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("delete_", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("modify_", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("tag_", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("operate_", StringComparison.OrdinalIgnoreCase)
                || command.Equals("color_splash", StringComparison.OrdinalIgnoreCase)
                || command.Equals("send_code_to_revit", StringComparison.OrdinalIgnoreCase);
        }

        // --- lazy command registry load -------------------------------------------

        private bool EnsureCommandsLoaded()
        {
            if (_commandsLoaded) return true;

            var uiApp = DocumentSessionManager.Instance.GetUIApplication();
            if (uiApp == null || uiApp.ActiveUIDocument == null) return false;

            try
            {
                ExternalEventManager.Instance.Initialize(uiApp, _logger);
                _commandExecutor = new CommandExecutor(_commandRegistry, _logger);

                var commandManager = new CommandManager(_commandRegistry, _logger, _configManager, uiApp);
                commandManager.LoadCommands();

                _commandsLoaded = true;
                _logger.Info("Command registry loaded");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to load command registry: {0}", ex.Message);
                return false;
            }
        }

        private void OnDocumentStateChanged()
        {
            // Registration/heartbeat re-push happens on the heartbeat cadence, but
            // push immediately on change so the broker sees new/closed docs fast.
            // (Command loading is handled separately on the UI thread via Idling.)
            SafeFireAndForget(SendRegisterAsync);
        }

        // --- sending --------------------------------------------------------------

        private async Task SendRegisterAsync() => await SendStateAsync("register");

        private async Task SendHeartbeatAsync() => await SendStateAsync("heartbeat");

        private async Task SendStateAsync(string type)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            var payload = new JObject
            {
                ["type"] = type,
                ["sessionId"] = DocumentSessionManager.Instance.SessionId,
                ["revitVersion"] = DocumentSessionManager.Instance.RevitVersion,
                ["documents"] = JArray.FromObject(DocumentSessionManager.Instance.BuildDocumentList()),
            };
            await SendRawAsync(payload.ToString(Formatting.None));
        }

        private async Task SendResponseAsync(string correlationId, JToken result, JObject error)
        {
            var payload = new JObject
            {
                ["type"] = "response",
                ["correlationId"] = correlationId,
            };
            if (error != null) payload["error"] = error;
            else payload["result"] = result ?? JValue.CreateNull();
            await SendRawAsync(payload.ToString(Formatting.None));
        }

        private async Task SendRawAsync(string json)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(json);
            await _sendLock.WaitAsync();
            try
            {
                await _ws.SendAsync(new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private static JObject BrokerError(string code, string message)
        {
            return new JObject { ["code"] = code, ["message"] = message };
        }

        private static void SafeFireAndForget(Func<Task> action)
        {
            Task.Run(async () =>
            {
                try { await action(); } catch { /* logged upstream where relevant */ }
            });
        }
    }
}
