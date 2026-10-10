namespace AgentWorkbench.Review.Comparison;

/// <summary>中文大写金额解析（支持 零壹贰... 与 零一二...，含 万/亿/仟/佰/拾/元/整）。</summary>
internal static class ChineseNumber
{
    private static readonly Dictionary<char, int> Digits = new()
    {
        ['零'] = 0, ['〇'] = 0,
        ['一'] = 1, ['壹'] = 1, ['二'] = 2, ['贰'] = 2, ['两'] = 2, ['三'] = 3, ['叁'] = 3,
        ['四'] = 4, ['肆'] = 4, ['五'] = 5, ['伍'] = 5, ['六'] = 6, ['陆'] = 6,
        ['七'] = 7, ['柒'] = 7, ['八'] = 8, ['捌'] = 8, ['九'] = 9, ['玖'] = 9
    };

    private static readonly Dictionary<char, long> Units = new()
    {
        ['十'] = 10, ['拾'] = 10,
        ['百'] = 100, ['佰'] = 100,
        ['千'] = 1000, ['仟'] = 1000,
        ['万'] = 10000, ['亿'] = 100000000
    };

    public static bool TryParse(string input, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var cleaned = input
            .Replace("人民币", string.Empty)
            .Replace("元", string.Empty)
            .Replace("整", string.Empty)
            .Replace("角", string.Empty)
            .Replace("分", string.Empty)
            .Trim();

        long section = 0, total = 0, number = 0;
        var any = false;

        foreach (var ch in cleaned)
        {
            if (Digits.TryGetValue(ch, out var digit))
            {
                number = digit;
                any = true;
            }
            else if (Units.TryGetValue(ch, out var unit))
            {
                any = true;
                if (unit >= 10000)
                {
                    total += (section + number) * unit;
                    section = 0;
                    number = 0;
                }
                else
                {
                    section += (number == 0 ? 1 : number) * unit;
                    number = 0;
                }
            }
            else
            {
                return false;
            }
        }

        value = total + section + number;
        return any && value > 0;
    }
}
