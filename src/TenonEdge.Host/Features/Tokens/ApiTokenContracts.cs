using System.Text.Json.Serialization;
using SqlSugar;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Tokens;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ApiTokenCreateRequest(string? Name, UserRole? Role, DateTimeOffset? ExpiresAt = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ApiTokenRevokeRequest(bool Confirmed);
internal sealed record ApiTokenCreated(ApiTokenSummary Token, string Secret)
{
    public override string ToString() => "API token creation (secret omitted)";
}
internal sealed record ApiTokenPage(ApiTokenSummary[] Items, long? NextCursor);
internal sealed record ApiTokenSummary(long Id, string Name, UserRole Role, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt, long CreatorUserId, string CreatorAccountName, string? CreatedByTokenName)
{
    internal static ApiTokenSummary From(ApiTokenRecord row) => new(row.Id, row.Name, Enum.Parse<UserRole>(row.Role),
        DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAt), At(row.ExpiresAt), At(row.RevokedAt), At(row.LastUsedAt),
        row.CreatorUserId, row.CreatorAccountName, row.CreatedByTokenName);
    private static DateTimeOffset? At(long? value) => value is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null;
}
internal sealed record CreatorTokenReview(string Version, ApiTokenSummary[] Items);
internal sealed class ApiTokenException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

[SugarTable("api_token")]
internal sealed class ApiTokenRecord
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(ColumnName = "name")] public string Name { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "role")] public string Role { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "secret_hash")] public byte[] SecretHash { get; set; } = [];
    [SugarColumn(ColumnName = "created_at")] public long CreatedAt { get; set; }
    [SugarColumn(ColumnName = "expires_at")] public long? ExpiresAt { get; set; }
    [SugarColumn(ColumnName = "revoked_at")] public long? RevokedAt { get; set; }
    [SugarColumn(ColumnName = "last_used_at")] public long? LastUsedAt { get; set; }
    // 历史归属，不做用户外键/级联删除或动态权限继承。管理员令牌再创建时沿用来源账号，操作人另记令牌。
    [SugarColumn(ColumnName = "creator_user_id")] public long CreatorUserId { get; set; }
    [SugarColumn(ColumnName = "creator_account_name")] public string CreatorAccountName { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "created_by_token_name")] public string? CreatedByTokenName { get; set; }
    public override string ToString() => "API token record (hash omitted)";
}
