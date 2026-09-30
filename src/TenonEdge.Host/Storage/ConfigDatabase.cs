using Microsoft.Data.Sqlite;
using SqlSugar;

namespace TenonEdge.Host.Storage;

/// <summary>
/// 配置库 config.db 的访问入口（总设计 §2.3、§8.5）：SqlSugarCoreNoDrive 加 Microsoft.Data.Sqlite。
/// <list type="bullet">
/// <item><see cref="Client"/> 是单例 SqlSugarScope，按异步上下文为每个请求或后台流程提供各自的客户端，供顺序执行的操作使用。</item>
/// <item>同一异步流程内需要并发执行时，每个并发分支用 <see cref="CreateIsolatedClient"/> 取得独立客户端并负责释放。
/// 多个并发分支共用同一上下文的客户端时会共用一个连接，V-06 探针中因此出现过“集合被并发修改”的异常。</item>
/// <item>事务只在一次操作内开启并完成，不跨外部网络等待，也不保存在单例中（后端实施规范 §4）。</item>
/// </list>
/// 物理连接的设置见 <see cref="SqliteConnectionSettings"/>。释放时清空本库的连接池，关闭全部物理连接。
/// </summary>
internal sealed class ConfigDatabase : IDisposable
{
    public const string FileName = "config.db";

    /// <summary>
    /// 命令超时（秒），也是遇到锁时的最长等待。SqlSugar 默认 300 秒，这里改用 Microsoft.Data.Sqlite 的默认值 30 秒。
    /// </summary>
    public const int CommandTimeoutSeconds = 30;

    private readonly SqlSugarScope _scope;

    public ConfigDatabase(DataDirectoryLock dataDirectory)
    {
        FilePath = Path.Combine(dataDirectory.Directory.Root, FileName);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = CommandTimeoutSeconds,
        }.ToString();

        _scope = new SqlSugarScope(
            new ConnectionConfig
            {
                DbType = DbType.Sqlite,
                ConnectionString = ConnectionString,
                IsAutoCloseConnection = true,

                // 放在连接配置中而不是 configAction 里：CopyNew 复制连接配置，但不重放 configAction。
                AopEvents = new AopEvents
                {
                    CheckConnectionExecuted = (connection, _) => SqliteConnectionSettings.Apply(connection),
                },
            },
            client => client.Ado.CommandTimeOut = CommandTimeoutSeconds);
    }

    public string FilePath { get; }

    public string ConnectionString { get; }

    public ISqlSugarClient Client => _scope;

    /// <summary>为一个并发分支创建独立客户端（复制连接配置与命令超时）；调用方负责释放。</summary>
    public SqlSugarClient CreateIsolatedClient() => _scope.CopyNew();

    /// <summary>
    /// 在独立客户端上执行一个写事务：正常返回时提交，抛出异常时回滚并重新抛出。
    /// 事务以 BEGIN IMMEDIATE 开始，写事务依次排队；不要在其中等待外部网络或执行耗时计算。
    /// 不使用 SqlSugar 的 UseTran：它会吞掉异常。
    /// </summary>
    public T InTransaction<T>(Func<SqlSugarClient, T> work)
    {
        using var client = CreateIsolatedClient();
        client.Ado.BeginTran();
        try
        {
            var result = work(client);
            client.Ado.CommitTran();
            return result;
        }
        catch
        {
            client.Ado.RollbackTran();
            throw;
        }
    }

    /// <summary>
    /// 不经 SqlSugar、也不应用连接设置地打开一个连接，只用于启动初始化：尚未启用 WAL 的新库无法通过连接设置的回读校验。
    /// </summary>
    public SqliteConnection OpenInitializationConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _scope.Dispose();
        using var connection = new SqliteConnection(ConnectionString);
        SqliteConnection.ClearPool(connection);
    }
}
