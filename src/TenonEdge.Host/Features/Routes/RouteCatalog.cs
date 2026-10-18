using Microsoft.AspNetCore.Http.HttpResults;
using SqlSugar;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;

namespace TenonEdge.Host.Features.Routes;

internal sealed record RouteCatalogPage(string Version, RouteCatalogItem[] Items, long? NextAfterId);
internal sealed class RouteCatalogItem
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public long? DeviceId { get; set; }
    public string? DeviceKey { get; set; }
    public string? DeviceName { get; set; }
}

internal sealed partial class RouteDraftStore
{
    // Project metadata in SQLite; do not load point tables, sample JSON or credentials into the catalog.
    internal RouteCatalogPage ReadCatalog(string kind, long afterId, int limit, int moduleVersion = 1) => database.InTransaction(client =>
    {
        var extension = inputModules.Contains(kind, moduleVersion);
        if (afterId < 0 || afterId > 9007199254740991 || limit is < 1 or > 100 ||
            !extension && (moduleVersion != 1 || kind is not ("input.modbus" or "input.mqtt" or "input.http" or "input.simulator" or "output.mqtt" or "output.http")))
        { throw new ArgumentException("目录类型或分页参数无效。"); }
        var protocol = kind[(kind.IndexOf('.') + 1)..];
        var sql = kind.StartsWith("input.", StringComparison.Ordinal)
            ? """
              SELECT i.id AS Id, i.business_key AS Key, json_extract(i.content, '$.name') AS Name,
                (json_extract(i.content, '$.enabled') = 1 AND json_extract(d.content, '$.enabled') = 1
                  AND (d.connection_id IS NULL OR COALESCE(json_extract(c.content, '$.enabled'), 0) = 1)) AS Enabled,
                d.id AS DeviceId, d.business_key AS DeviceKey, json_extract(d.content, '$.name') AS DeviceName
              FROM input_draft i JOIN device_draft d ON d.id = i.device_id
                LEFT JOIN connection_draft c ON c.id = d.connection_id
              WHERE i.id > @after AND
                ((@extension = 0 AND json_extract(i.content, '$.kind') = @kind) OR
                 (@extension = 1 AND json_extract(i.content, '$.kind') = 'module'
                  AND json_extract(i.content, '$.module.kind') = @kind
                  AND json_extract(i.content, '$.module.version') = @moduleVersion))
              ORDER BY i.id LIMIT @take
              """
            : """
              SELECT id AS Id, business_key AS Key, json_extract(content, '$.name') AS Name,
                json_extract(content, '$.enabled') AS Enabled, NULL AS DeviceId, NULL AS DeviceKey, NULL AS DeviceName
              FROM connection_draft WHERE id > @after AND json_extract(content, '$.kind') = @kind
              ORDER BY id LIMIT @take
              """;
        var version = ConfigurationDraft.ReadVersion(client);
        var rows = client.Ado.SqlQuery<RouteCatalogItem>(sql, new SugarParameter("@after", afterId),
            new SugarParameter("@kind", extension ? kind : protocol), new SugarParameter("@take", limit + 1),
            new SugarParameter("@extension", extension ? 1 : 0), new SugarParameter("@moduleVersion", moduleVersion));
        var items = rows.Take(limit).ToArray();
        return new RouteCatalogPage(version, items, rows.Count > limit ? items[^1].Id : null);
    });
}

internal static class RouteCatalogEndpoints
{
    public static RouteGroupBuilder MapRouteCatalogEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/routes/catalog", Read).WithTags("Routes").WithName("getRouteCatalog")
            .WithSummary("分页读取同版本的来源或输出连接摘要，不返回配置、点表、样本或凭证");
        return api;
    }
    private static Results<Ok<RouteCatalogPage>, ValidationProblem> Read(string kind, RouteDraftStore drafts, long afterId = 0, int limit = 100, int moduleVersion = 1)
    {
        try
        { return TypedResults.Ok(drafts.ReadCatalog(kind, afterId, limit, moduleVersion)); }
        catch (ArgumentException) { return ApiProblems.Validation(new Dictionary<string, string[]> { ["catalog"] = ["请选择已安装的输入模块版本或内置输入/输出；页大小 1—100，游标为非负安全整数。"] }); }
    }
}
