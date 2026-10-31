using System.Security.Cryptography;

namespace AgentWorkbench.Webapi.Auth;

/// <summary>
/// 密码派生（PBKDF2-SHA256）。库里只存派生值与盐，永不存明文。
/// </summary>
/// <remarks>
/// 为什么自己写而不引 Identity 全家桶：这里只需要「一个人的密码能不能验过」，
/// 引入 ASP.NET Identity 会带进角色存储、令牌表、邮件确认等一整套表结构，与本仓「底座不是产品」的定位冲突。
/// 迭代次数 10 万：本机实测约 40ms/次，足够挡住离线爆破，又不至于让登录明显变慢。
/// 存储格式带算法与迭代数，将来调参可以平滑升级（老哈希仍按旧参数验证）。
/// </remarks>
public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const string Prefix = "pbkdf2-sha256";

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    /// <summary>验密。格式非法一律 false（不抛异常，避免把存储损坏变成 500）。</summary>
    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix)
            return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0)
            return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
