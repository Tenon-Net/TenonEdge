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
    // 同一配置库的检查/观测事件沿用统一入口；不是请求上下文、连接或活跃事务。
    // Scope 与独立客户端均使用它，保留原 CopyNew 的统一事件行为。
    private readonly AopEvents _events = new()
    {
        CheckConnectionExecuted = (connection, _) => SqliteConnectionSettings.Apply(connection),
    };

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
            CreateConnectionConfig(),
            client => client.Ado.CommandTimeOut = CommandTimeoutSeconds);
    }

    public string FilePath { get; }

    public string ConnectionString { get; }

    public ISqlSugarClient Client => _scope;

    /// <summary>从统一配置创建独立客户端，不查找 Scope 的异步上下文；调用方负责释放。</summary>
    public SqlSugarClient CreateIsolatedClient()
    {
        var client = new SqlSugarClient(CreateConnectionConfig());
        client.Ado.CommandTimeOut = CommandTimeoutSeconds;
        return client;
    }

    private ConnectionConfig CreateConnectionConfig() => new()
    {
        DbType = DbType.Sqlite,
        ConnectionString = ConnectionString,
        IsAutoCloseConnection = true,
        // 连接配置和超时独立；检查/观测事件由配置库统一持有，每次连接检查保持幂等。
        AopEvents = _events,
    };

    /// <summary>候选验证：由单个发送者独占，只读连接不入池。每次查询结束即释放读快照，调用方负责释放连接。</summary>
    public SqliteConnection OpenSenderReadConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder(ConnectionString)
        {
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            // 显式复用统一物理连接校验，而不是绕开 SqlSugar AOP 后依赖驱动默认值。
            SqliteConnectionSettings.Apply(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON; PRAGMA query_only;";
            if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
            { throw new InvalidOperationException("发送者配置读取连接未启用只读保护。"); }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

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
