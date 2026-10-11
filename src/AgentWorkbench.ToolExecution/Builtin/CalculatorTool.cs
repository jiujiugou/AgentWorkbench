using System.Globalization;

namespace AgentWorkbench.ToolExecution.Builtin;

/// <summary>工具 <c>calculator</c>：四则运算求值（内部走 <see cref="SafeCalculator"/>，不 eval）。</summary>
public sealed class CalculatorTool : IAgentToolProvider
{
    public const string ToolName = "calculator";

    public IReadOnlyList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(
            (string expression) => SafeCalculator.Evaluate(expression).ToString(CultureInfo.InvariantCulture),
            ToolName,
            "计算四则运算表达式，支持 + - * / 与括号，例如 (1+2)*3。")
    ];
}
