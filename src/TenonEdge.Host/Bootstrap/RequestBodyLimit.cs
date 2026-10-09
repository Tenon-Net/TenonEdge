namespace TenonEdge.Host.Bootstrap;

/// <summary>少数批量草稿端点的有界请求体；认证/授权之后、模型绑定之前设置，其他端点沿用 1 MiB。</summary>
internal sealed record RequestBodyLimit(long Bytes);
