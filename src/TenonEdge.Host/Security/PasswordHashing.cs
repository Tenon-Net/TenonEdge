using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Security;

/// <summary>
/// 密码哈希（总设计 §3.3，S-16）：单独使用 Identity.Core 的 PasswordHasher（随 ASP.NET Core 共享框架提供），
/// V3 格式为 PBKDF2-HMAC-SHA512、128 位盐、256 位子密钥、10 万次迭代。库中只保存哈希，不保存明文。
/// 对不存在或没有密码的账号也执行一次等量的校验，使哈希计算的耗时不暴露账号是否存在（已有账号失败时另有一次计数写入，见 LoginService）。
/// </summary>
internal sealed class PasswordHashing
{
    private readonly PasswordHasher<UserAccountRecord> _hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
    }));

    private readonly string _dummyHash;

    public PasswordHashing() => _dummyHash = _hasher.HashPassword(new UserAccountRecord(), Guid.NewGuid().ToString("N"));

    public string Hash(string password) => _hasher.HashPassword(new UserAccountRecord(), password);

    /// <summary>校验密码；needsRehash 表示哈希参数已过时，应在登录成功后以当前参数重新计算。</summary>
    public bool Verify(string? hash, string password, out bool needsRehash)
    {
        if (hash is null)
        {
            _hasher.VerifyHashedPassword(new UserAccountRecord(), _dummyHash, password);
            needsRehash = false;
            return false;
        }

        var result = _hasher.VerifyHashedPassword(new UserAccountRecord(), hash, password);
        needsRehash = result == PasswordVerificationResult.SuccessRehashNeeded;
        return result != PasswordVerificationResult.Failed;
    }
}
