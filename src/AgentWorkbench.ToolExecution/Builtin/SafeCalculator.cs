using System.Globalization;

namespace AgentWorkbench.ToolExecution.Builtin;

/// <summary>安全四则运算求值（不 eval）：支持 + - * / 括号与小数。</summary>
public static class SafeCalculator
{
    public static double Evaluate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException("表达式为空", nameof(expression));

        var parser = new Parser(expression);
        var value = parser.ParseExpression();
        parser.ExpectEnd();
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidOperationException("表达式结果非法");
        return value;
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipWhite();
                if (Match('+')) value += ParseTerm();
                else if (Match('-')) value -= ParseTerm();
                else return value;
            }
        }

        private double ParseTerm()
        {
            var value = ParseFactor();
            while (true)
            {
                SkipWhite();
                if (Match('*')) value *= ParseFactor();
                else if (Match('/'))
                {
                    var divisor = ParseFactor();
                    if (divisor == 0) throw new DivideByZeroException("除以零");
                    value /= divisor;
                }
                else return value;
            }
        }

        private double ParseFactor()
        {
            SkipWhite();
            if (Match('('))
            {
                var value = ParseExpression();
                SkipWhite();
                if (!Match(')')) throw new FormatException("缺少右括号");
                return value;
            }

            if (Match('-')) return -ParseFactor();
            if (Match('+')) return ParseFactor();

            var start = _pos;
            while (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] == '.'))
                _pos++;
            if (start == _pos) throw new FormatException($"非法字符：{text[_pos..]}");
            return double.Parse(text[start.._pos], CultureInfo.InvariantCulture);
        }

        private bool Match(char c)
        {
            if (_pos < text.Length && text[_pos] == c)
            {
                _pos++;
                return true;
            }
            return false;
        }

        private void SkipWhite()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
                _pos++;
        }

        public void ExpectEnd()
        {
            SkipWhite();
            if (_pos != text.Length)
                throw new FormatException($"表达式尾部非法：{text[_pos..]}");
        }
    }
}
