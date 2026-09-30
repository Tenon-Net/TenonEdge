namespace TenonEdge.Host.Storage.Migrations;

/// <summary>
/// 一次配置库迁移：版本号从 1 开始连续递增，语句按顺序在迁移事务中执行。
/// 版本号与名称写入迁移历史；发布之后，已有迁移的版本、名称和语句都不得再修改，变更只能追加新的迁移。
/// </summary>
internal sealed record ConfigMigration(int Version, string Name, IReadOnlyList<string> Statements);
