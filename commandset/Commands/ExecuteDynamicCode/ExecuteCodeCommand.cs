using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// 处理代码执行的命令类
    /// </summary>
    public class ExecuteCodeCommand : RevitCommandBase
    {
        private ExecuteCodeEventHandler _handler => (ExecuteCodeEventHandler)Handler;

        public override string CommandName => "send_code_to_revit";

        public ExecuteCodeCommand(UIApplication uiApp)
            : base(new ExecuteCodeEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                // 参数验证
                if (!parameters.ContainsKey("code"))
                {
                    throw new ArgumentException("Missing required parameter: 'code'");
                }

                // 解析代码和参数
                string code = parameters["code"].Value<string>();
                JArray parametersArray = parameters["parameters"] as JArray;
                object[] executionParameters = parametersArray?.ToObject<object[]>() ?? Array.Empty<object>();
                string requestedMode = parameters["transactionMode"]?.Value<string>();
                string transactionMode = ExecuteCodeEventHandler.NormalizeTransactionMode(requestedMode)
                    ?? throw new ArgumentException(
                        $"Invalid transactionMode '{requestedMode}'. Use 'auto' (commit), 'rollback' (read mode: always rolled back) or 'none' (no transaction).");

                _handler.SetExecutionParameters(code, executionParameters, transactionMode);

                if (RaiseAndWaitForCompletion(60000))
                {
                    return _handler.ResultInfo;
                }
                else
                {
                    throw new TimeoutException("Code execution timed out after 60 seconds.");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Code execution failed: {ex.Message}", ex);
            }
        }
    }
}
