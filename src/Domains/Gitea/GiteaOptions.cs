namespace AgentWorkbench.Gitea;

/// <summary>
/// 连接参数。plain record，不引 DI / Options 包（动作域不依赖框架配置体系）。
/// 由组合根从配置/环境变量构造后传入；缺任何一项都必须 fail-closed。
/// </summary>
/// <param name="BaseUrl">站点根地址，如 https://gitea.com。</param>
/// <param name="Token">API token。只存在于本对象与 HTTP 头，绝不进入 TraceEvent / ActionResult.Data。</param>
/// <param name="DefaultOwner">默认仓库归属，参数未给 owner 时使用。</param>
/// <param name="DefaultRepo">默认仓库名。</param>
/// <param name="TimeoutSeconds">单次 HTTP 超时。与调用方 CancellationToken 分开处理。</param>
/// <param name="AllowWrites">
/// M2 只读边界开关。false 时写能力在 adapter 内直接拒绝且<b>零 HTTP</b>，
/// 不回落到任何内存假实现 —— 比「不能写」更糟的是「以为写了其实写进内存」。
/// M3 再由组合根显式打开。
/// </param>
public sealed record GiteaOptions(
    string BaseUrl,
    string Token,
    string DefaultOwner = "",
    string DefaultRepo = "",
    int TimeoutSeconds = 15,
    bool AllowWrites = false,
    int MaxReadRetries = 2)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        !string.IsNullOrWhiteSpace(Token);

    public static GiteaOptions Unconfigured => new("", "");
}
