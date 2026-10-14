using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SqlSugar;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;

namespace TenonEdge.Host.Features.Tokens;

internal sealed class ApiTokenStore(ConfigDatabase database, SnowflakeIdGenerator ids, AuditTrail audit, TimeProvider time)
{
    // 保证创建者停用/删除的确认清单和单次事务有界；吊销后保留元数据，列表分页读取。
    internal const int MaximumUnrevoked = 1000;
    public ApiTokenCreated Create(ApiTokenCreateRequest request, ManagementActor actor)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 64 || name.Any(char.IsControl) || request.Role is not { } role || !Enum.IsDefined(role) ||
            request.ExpiresAt is { } expires && expires <= time.GetUtcNow())
        { throw new ApiTokenException(400, "token.invalidRequest"); }
        var id = ids.NextId();
        var secret = ApiTokenSecrets.Create(id);
        var row = database.InTransaction(client =>
        {
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            var authenticated = actor.RequireAdministrator(client, now);
            if (client.Queryable<ApiTokenRecord>().Any(row => row.Name == name))
            { throw new ApiTokenException(409, "token.nameExists"); }
            if (client.Queryable<ApiTokenRecord>().Count(row => row.RevokedAt == null) >= MaximumUnrevoked)
            { throw new ApiTokenException(409, "token.limitReached"); }
            if (request.ExpiresAt is { } expiration && expiration.ToUnixTimeMilliseconds() <= now)
            { throw new ApiTokenException(400, "token.invalidRequest"); }
            var token = new ApiTokenRecord
            {
                Id = id,
                Name = name,
                Role = role.ToString(),
                SecretHash = ApiTokenSecrets.Hash(secret),
                CreatedAt = now,
                ExpiresAt = request.ExpiresAt?.ToUnixTimeMilliseconds(),
                CreatorUserId = authenticated.CreatorUserId,
                CreatorAccountName = authenticated.Audit.Name!,
                CreatedByTokenName = authenticated.Audit.TokenName
            };
            client.Insertable(token).ExecuteCommand();
            WriteAudit(client, "token.create", token, authenticated.Audit, actor.SourceIp);
            return token;
        });
        return new(ApiTokenSummary.From(row), secret);
    }
    public ApiTokenPage Read(long after, int limit)
    {
        if (after is < 0 or > 9007199254740991L || limit is < 1 or > 100)
        { throw new ApiTokenException(400, "token.invalidQuery"); }
        using var client = database.CreateIsolatedClient();
        // 明确投影非秘密列，不把哈希批量读进列表对象。
        var rows = client.Queryable<ApiTokenRecord>().Where(row => row.Id > after).OrderBy(row => row.Id).Take(limit + 1)
            .Select(row => new ApiTokenRecord
            {
                Id = row.Id,
                Name = row.Name,
                Role = row.Role,
                CreatedAt = row.CreatedAt,
                ExpiresAt = row.ExpiresAt,
                RevokedAt = row.RevokedAt,
                LastUsedAt = row.LastUsedAt,
                CreatorUserId = row.CreatorUserId,
                CreatorAccountName = row.CreatorAccountName,
                CreatedByTokenName = row.CreatedByTokenName
            }).ToList();
        return new(rows.Take(limit).Select(ApiTokenSummary.From).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }
    public void Revoke(long id, bool confirmed, ManagementActor actor) => database.InTransaction(client =>
    {
        if (!confirmed || id is < 1 or > 9007199254740991L)
        { throw new ApiTokenException(400, "token.confirmationRequired"); }
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var authenticated = actor.RequireAdministrator(client, now);
        var token = client.Queryable<ApiTokenRecord>().InSingle(id) ?? throw new ApiTokenException(404, "token.notFound");
        if (token.RevokedAt is not null)
        { throw new ApiTokenException(409, "token.alreadyRevoked"); }
        Revoke(client, token, authenticated.Audit, actor.SourceIp, now);
        return true;
    });
    public ApiTokenRecord? Authenticate(string secret)
    {
        if (!ApiTokenSecrets.TryId(secret, out var id))
        { return null; }
        var hash = ApiTokenSecrets.Hash(secret);
        return database.InTransaction(client =>
        {
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            var row = client.Queryable<ApiTokenRecord>().InSingle(id);
            if (!IsUsable(row, now) || !CryptographicOperations.FixedTimeEquals(hash, row!.SecretHash))
            { return null; }
            row.LastUsedAt = Math.Max(row.LastUsedAt ?? 0, now);
            client.Updateable(row).UpdateColumns(token => token.LastUsedAt).ExecuteCommand();
            return row;
        });
    }
    internal static bool IsUsable(ApiTokenRecord? row, long now) => row is not null && row.RevokedAt is null &&
        (row.ExpiresAt is null || row.ExpiresAt > now) && Enum.TryParse<UserRole>(row.Role, out var role) && Enum.IsDefined(role);
    public CreatorTokenReview ReadCreator(long userId) => database.InTransaction(client =>
    {
        if (client.Queryable<UserAccountRecord>().InSingle(userId) is null)
        { throw new ApiTokenException(404, "user.notFound"); }
        return Review(CreatorRows(client, userId));
    });
    internal void ApplyCreatorChoice(SqlSugarClient client, long userId, string? expectedVersion, bool? revoke,
        AuditAccount actor, string? ip)
    {
        var rows = CreatorRows(client, userId);
        if (rows.Length == 0 && expectedVersion is null && revoke is null)
        { return; }
        if (revoke is null || expectedVersion is null)
        { throw new ApiTokenException(409, "token.creatorChoiceRequired"); }
        if (Review(rows).Version != expectedVersion)
        { throw new ApiTokenException(409, "token.creatorListChanged"); }
        if (revoke == true)
        {
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            foreach (var row in rows)
            { Revoke(client, row, actor, ip, now); }
        }
    }
    private static ApiTokenRecord[] CreatorRows(SqlSugarClient client, long userId) => [.. client.Queryable<ApiTokenRecord>()
        .Where(row => row.CreatorUserId == userId && row.RevokedAt == null).OrderBy(row => row.Id).Take(MaximumUnrevoked + 1).ToList()];
    private static CreatorTokenReview Review(ApiTokenRecord[] rows)
    {
        if (rows.Length > MaximumUnrevoked)
        { throw new ApiTokenException(503, "token.creatorListUnavailable"); }
        return new(Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rows.Select(row => row.Id)))),
            rows.Select(ApiTokenSummary.From).ToArray());
    }
    private void Revoke(SqlSugarClient client, ApiTokenRecord row, AuditAccount actor, string? ip, long now)
    {
        row.RevokedAt = now;
        client.Updateable(row).UpdateColumns(token => token.RevokedAt).ExecuteCommand();
        WriteAudit(client, "token.revoke", row, actor, ip);
    }
    private void WriteAudit(SqlSugarClient client, string action, ApiTokenRecord row, AuditAccount actor, string? ip) =>
        audit.Append(client, new(action, AuditResults.Success, actor, ip, row.Id.ToString(CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(new { row.Name, row.Role, row.ExpiresAt, row.CreatorUserId })));
}
