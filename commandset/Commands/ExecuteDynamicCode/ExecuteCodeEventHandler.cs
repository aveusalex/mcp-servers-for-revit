using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// External event handler that compiles and runs a C# snippet against the target document.
    /// </summary>
    public class ExecuteCodeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        /// <summary>Wrap the snippet in a transaction that is committed on success.</summary>
        public const string TransactionModeAuto = "auto";
        /// <summary>No transaction: for read-only code, or code that manages its own transactions.</summary>
        public const string TransactionModeNone = "none";
        /// <summary>Read mode: run inside a transaction that is always rolled back, so the model never changes.</summary>
        public const string TransactionModeRollback = "rollback";

        private const string SnippetPath = "snippet";
        private const string GeneratedAssemblyName = "AIGeneratedCode";

        // Execution request
        private string _generatedCode;
        private object[] _executionParameters;
        private string _transactionMode = TransactionModeAuto;

        public ExecutionResultInfo ResultInfo { get; private set; }

        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        static ExecuteCodeEventHandler()
        {
            RoslynDependencyResolver.EnsureRegistered();
        }

        /// <summary>
        /// Normalizes a transactionMode value. Null/empty means "auto" (the historic default);
        /// "read" is accepted as an alias of "rollback". Returns null for unknown values.
        /// </summary>
        public static string NormalizeTransactionMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return TransactionModeAuto;
            switch (mode.Trim().ToLowerInvariant())
            {
                case TransactionModeAuto: return TransactionModeAuto;
                case TransactionModeNone: return TransactionModeNone;
                case TransactionModeRollback:
                case "read": return TransactionModeRollback;
                default: return null;
            }
        }

        public void SetExecutionParameters(string code, object[] parameters = null, string transactionMode = TransactionModeAuto)
        {
            _generatedCode = code;
            _executionParameters = parameters ?? Array.Empty<object>();
            _transactionMode = NormalizeTransactionMode(transactionMode) ?? TransactionModeAuto;
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            ResultInfo = new ExecutionResultInfo { TransactionMode = _transactionMode };
            bool transactionOpened = false;
            try
            {
                // doc-agnostic: operate on the broker-selected document (falls back to active).
                var doc = RevitDocumentContext.ResolveDocument(app);
                var executor = Compile(_generatedCode);

                if (_transactionMode == TransactionModeNone)
                {
                    ResultInfo.Result = ToJson(Invoke(executor, doc, _executionParameters));
                }
                else
                {
                    using (var transaction = new Transaction(doc, "MCP: send_code_to_revit"))
                    {
                        transaction.Start();
                        transactionOpened = true;

                        object result = Invoke(executor, doc, _executionParameters);

                        if (_transactionMode == TransactionModeRollback)
                        {
                            // Serialize while the elements the snippet returned still exist, then undo everything.
                            ResultInfo.Result = ToJson(result);
                            transaction.RollBack();
                            ResultInfo.RolledBack = true;
                        }
                        else
                        {
                            var status = transaction.Commit();
                            if (status != TransactionStatus.Committed)
                            {
                                ResultInfo.RolledBack = true;
                                throw new InvalidOperationException(
                                    $"Revit did not commit the transaction (status: {status}); a failure in the changes made Revit roll them back.");
                            }
                            ResultInfo.Result = ToJson(result);
                        }
                    }
                }

                ResultInfo.Success = true;
            }
            catch (SnippetCompilationException ex)
            {
                ResultInfo.Success = false;
                ResultInfo.ErrorType = "compile";
                ResultInfo.ErrorMessage = ex.Message;
                ResultInfo.Errors = ex.Errors;
                ResultInfo.Line = ex.Errors.FirstOrDefault(e => e.Line.HasValue)?.Line;
                ResultInfo.Hints = CodeErrorHints.ForCompileErrors(ex.Errors);
            }
            catch (Exception ex)
            {
                // Reflection wraps exceptions thrown by the snippet; report the snippet's own exception.
                var inner = ex is TargetInvocationException { InnerException: { } wrapped } ? wrapped : ex;
                bool fromSnippet = !ReferenceEquals(inner, ex);

                ResultInfo.Success = false;
                ResultInfo.ErrorType = fromSnippet ? "runtime" : "host";
                ResultInfo.ExceptionType = inner.GetType().FullName;
                ResultInfo.Line = fromSnippet ? FindSnippetLine(inner) : null;
                ResultInfo.ErrorMessage = fromSnippet
                    ? $"The snippet threw {inner.GetType().Name}{(ResultInfo.Line.HasValue ? $" at line {ResultInfo.Line}" : "")}: {inner.Message}"
                    : inner.Message;
                ResultInfo.Hints = CodeErrorHints.ForException(inner, _transactionMode);

                if (transactionOpened)
                {
                    ResultInfo.RolledBack = true;
                    ResultInfo.ErrorMessage += " (the transaction was rolled back, so the model is unchanged)";
                }
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private static object Invoke(MethodInfo executor, Document doc, object[] parameters)
        {
            return executor.Invoke(null, new object[] { doc, parameters });
        }

        private static MethodInfo Compile(string code)
        {
            // The #line directives make compiler errors and stack traces refer to lines of the snippet, not of this template.
            var wrappedCode = $@"
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;

namespace AIGeneratedCode
{{
    public static class CodeExecutor
    {{
        public static object Execute(Document document, object[] parameters)
        {{
#line 1 ""{SnippetPath}""
{code}
#line default
        }}
    }}
}}";

            var syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode, path: "template.cs");

            var compilation = CSharpCompilation.Create(
                GeneratedAssemblyName + "_" + Guid.NewGuid().ToString("N"),
                syntaxTrees: new[] { syntaxTree },
                references: CollectReferences(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug)
            );

            using (var peStream = new MemoryStream())
            using (var pdbStream = new MemoryStream())
            {
                var emitResult = compilation.Emit(
                    peStream,
                    pdbStream,
                    options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));

                if (!emitResult.Success)
                {
                    var errors = emitResult.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(DescribeCompileError)
                        .ToList();
                    throw new SnippetCompilationException(errors);
                }

                // Loading the PDB alongside lets runtime stack traces carry snippet line numbers (best effort).
                var assembly = Assembly.Load(peStream.ToArray(), pdbStream.ToArray());
                return assembly.GetType("AIGeneratedCode.CodeExecutor").GetMethod("Execute");
            }
        }

        /// <summary>
        /// References every loaded assembly once per simple name (upstream PR #26). Add-ins such as BIM 360/ACC
        /// load second copies of Autodesk assemblies, and Roslyn rejects two references with the same simple name.
        /// When there are duplicates the highest version wins.
        /// </summary>
        private static List<MetadataReference> CollectReferences()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(SafeLocation(a)))
                .GroupBy(a => a.GetName().Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(a => a.GetName().Version).First())
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
                .ToList();
        }

        private static string SafeLocation(Assembly assembly)
        {
            try
            {
                return assembly.Location;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>Returns the snippet line where the exception was thrown, when the stack trace has it.</summary>
        private static int? FindSnippetLine(Exception ex)
        {
            try
            {
                var trace = new StackTrace(ex, true);
                foreach (var frame in trace.GetFrames() ?? Array.Empty<StackFrame>())
                {
                    var assemblyName = frame.GetMethod()?.DeclaringType?.Assembly.GetName().Name;
                    if (assemblyName == null || !assemblyName.StartsWith(GeneratedAssemblyName, StringComparison.Ordinal))
                        continue;
                    int line = frame.GetFileLineNumber();
                    if (line > 0) return line;
                }
            }
            catch (Exception)
            {
                // Line numbers are a nicety; never fail the error report because of them.
            }
            return null;
        }

        /// <summary>
        /// Returns the snippet's result as JSON, once. Revit objects such as Elements can't be serialized,
        /// so those fall back to their string form instead of failing.
        /// </summary>
        private static JToken ToJson(object result)
        {
            if (result == null)
            {
                return JValue.CreateNull();
            }

            try
            {
                return JToken.FromObject(result);
            }
            catch (Exception)
            {
                return new JValue(result.ToString());
            }
        }

        private static CompileError DescribeCompileError(Diagnostic diagnostic)
        {
            FileLinePositionSpan span = diagnostic.Location.GetMappedLineSpan();
            if (span.HasMappedPath && span.Path == SnippetPath)
            {
                return new CompileError
                {
                    Code = diagnostic.Id,
                    Line = span.StartLinePosition.Line + 1,
                    Column = span.StartLinePosition.Character + 1,
                    Message = diagnostic.GetMessage(),
                };
            }

            // Errors outside the snippet come from the template; the usual one is a missing return statement.
            return new CompileError { Code = diagnostic.Id, Message = "Template: " + diagnostic.GetMessage() };
        }

        public string GetName()
        {
            return "MCP: send_code_to_revit";
        }
    }

    internal sealed class SnippetCompilationException : Exception
    {
        public SnippetCompilationException(IList<CompileError> errors)
            : base("Compilation failed:\n" + string.Join("\n", errors.Select(e => e.ToString())))
        {
            Errors = errors;
        }

        public IList<CompileError> Errors { get; }
    }

    public class CompileError
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("line", NullValueHandling = NullValueHandling.Ignore)]
        public int? Line { get; set; }

        [JsonProperty("column", NullValueHandling = NullValueHandling.Ignore)]
        public int? Column { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        public override string ToString()
        {
            return Line.HasValue ? $"Line {Line}: {Code} {Message}" : $"{Code} {Message}";
        }
    }

    public class ExecutionResultInfo
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("result")]
        public JToken Result { get; set; }

        /// <summary>Kept for backwards compatibility: a one-line description of the failure.</summary>
        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>compile | runtime | host (failure outside the snippet, e.g. no target document).</summary>
        [JsonProperty("errorType", NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorType { get; set; }

        [JsonProperty("exceptionType", NullValueHandling = NullValueHandling.Ignore)]
        public string ExceptionType { get; set; }

        /// <summary>1-based line of the snippet where the (first) error happened, when known.</summary>
        [JsonProperty("line", NullValueHandling = NullValueHandling.Ignore)]
        public int? Line { get; set; }

        [JsonProperty("errors", NullValueHandling = NullValueHandling.Ignore)]
        public IList<CompileError> Errors { get; set; }

        [JsonProperty("hints", NullValueHandling = NullValueHandling.Ignore)]
        public IList<string> Hints { get; set; }

        [JsonProperty("transactionMode")]
        public string TransactionMode { get; set; }

        /// <summary>True when the changes the snippet made were undone (rollback mode, or an error inside a transaction).</summary>
        [JsonProperty("rolledBack")]
        public bool RolledBack { get; set; }
    }
}
