using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentWorkbench.Tools.Builtin;

/// <summary>
/// 动作请求号的派生规则。<b>幂等能不能生效，全看这一个函数</b>。
///
/// 背景：去重表按 requestId 判重（<c>action_idempotency</c> 的主键）。
/// 只要 requestId 是随机生成的，「同一个包裹送两次」就会得到两个新号码，
/// 去重表永远命中不了——保险丝挂在墙上、线没接。
///
/// 派生输入四项：会话标识 / 本次输入的客户端消息标识 / 能力名 / 规范化后的参数。
/// 前三项保证「同一次输入」稳定，第四项保证「参数改了就是新请求」——
/// 模型超时后原样重试会被挡住，改参数重试则放行，这正是想要的区分。
///
/// 边界（破了就等于把去重权交给模型）：
/// 1. 前三项<b>只能来自宿主作用域</b>（<see cref="Ports.IActionOriginProvider"/>），
///    绝不接受工具参数里同名字段；模型塞进来也读不到。
/// 2. 参数一律先规范化再参与计算：同一个对象换个键序、换个转义写法，
///    算出的号必须相同，否则「原样重试」会因为序列化差异被当成新请求。
/// 3. 拿不到客户端消息标识时回退为一次性 ID（行为与此前一致），
///    绝不退化成「所有请求同一个号」——那是把不重复执行变成不执行。
    ///    <b>这条在 web 生产路径不可达</b>：Host 的三条入口都要求输入身份必填，缺了直接拒绝，
    ///    见 `src/Host/AgentWorkbench.Webapi/README.md`「输入身份契约」。
    ///    绕过 Host 直接调本函数仍会触发回退（现存一处：Workflow 的 `AgentStepExecutor`）。
/// </summary>
public static class ActionRequestId
{
    /// <summary>派生号的长度。与 <see cref="Guid"/> 的 "N" 格式一致，便于在同一列里混存。</summary>
    public const int Length = 32;

    /// <summary>
    /// 按「哪一次输入 + 哪个能力 + 什么参数」派生请求号。
    /// <paramref name="clientMessageId"/> 为空时返回一次性 ID —— 防御性回退，
    /// 生产入口不会走到（Host 已判空）。不要删掉它，也不要把它当正常路径扩展。
    /// </summary>
    public static string Derive(
        string? conversationId,
        string? clientMessageId,
        string actionKey,
        JsonElement arguments)
    {
        if (string.IsNullOrWhiteSpace(clientMessageId))
            return Guid.NewGuid().ToString("N");

        var material = string.Join(
            '|',
            conversationId ?? string.Empty,
            clientMessageId,
            actionKey ?? string.Empty,
            Canonicalize(arguments));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..Length].ToLowerInvariant();
    }

    /// <summary>
    /// 把 JSON 规范化成与键序、空白无关的稳定文本。
    /// 对象按键名序数排序；数组保持原序（顺序有语义）；字符串统一走序列化器的转义规则。
    /// </summary>
    public static string Canonicalize(JsonElement element)
    {
        var builder = new StringBuilder();
        Write(element, builder);
        return builder.ToString();
    }

    private static void Write(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                        builder.Append(',');
                    firstProperty = false;
                    builder.Append(JsonSerializer.Serialize(property.Name));
                    builder.Append(':');
                    Write(property.Value, builder);
                }
                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                        builder.Append(',');
                    firstItem = false;
                    Write(item, builder);
                }
                builder.Append(']');
                break;

            case JsonValueKind.String:
                // 走序列化器而不是取原文：模型可能输出 "中" 或 "\u4e2d" 两种写法，
                // 原文参与计算会导致「语义相同、算出的号不同」。
                builder.Append(JsonSerializer.Serialize(element.GetString()));
                break;

            case JsonValueKind.Undefined:
                builder.Append("null");
                break;

            default:
                // 数字 / true / false / null：保留原文本，不做数值归一化（1 与 1.0 语义有别，
                // 归一化会把真实不同的两个请求合并成一个）。
                builder.Append(element.GetRawText());
                break;
        }
    }
}
