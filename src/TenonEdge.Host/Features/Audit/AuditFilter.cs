using System.Text.Json.Serialization;
using SqlSugar;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Audit;

/// <summary>列表与CSV共用精确筛选；from包含、to不包含，分页游标不是导出范围。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AuditFilter(DateTimeOffset? From = null, DateTimeOffset? To = null, string? Account = null,
    string? Action = null, string? Target = null)
{
    public void Validate()
    {
        if (From is { } from && To is { } to && from >= to || Account?.Length > 64 || Target?.Length > 64 || Action?.Length > 128)
        { throw new ArgumentException("审计筛选时间范围或字段长度无效。"); }
    }
    public ISugarQueryable<AuditLogRecord> Apply(ISqlSugarClient client) => client.Queryable<AuditLogRecord>()
        .WhereIF(From.HasValue, row => row.OccurredAt >= From!.Value.ToUnixTimeMilliseconds())
        .WhereIF(To.HasValue, row => row.OccurredAt < To!.Value.ToUnixTimeMilliseconds())
        .WhereIF(!string.IsNullOrEmpty(Account), row => row.AccountName == Account)
        .WhereIF(!string.IsNullOrEmpty(Action), row => row.Action == Action)
        .WhereIF(!string.IsNullOrEmpty(Target), row => row.Target == Target);
}
