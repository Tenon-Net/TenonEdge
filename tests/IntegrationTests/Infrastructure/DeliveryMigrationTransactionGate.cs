using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>测试子进程中观察原 SQLite 连接，不替换写者/事务，不安装 SQL 触发器。
/// update_hook 的通知不保证发生在行写入之前还是之后，故只声称处于多行 UPDATE 的通知窗口。
/// commit_hook 返回 0，保留原 COMMIT；所有回调内均不执行 SQL、不调用同一连接的托管 API。
/// https://www.sqlite.org/c3ref/auto_extension.html / update_hook.html / commit_hook.html</summary>
internal sealed class DeliveryMigrationTransactionGate
{
    internal const string ArmedFile = "migration.armed";
    internal const string EnteredFile = "migration.entered";
    internal const string ReleasedFile = "migration.released";
    internal const string DuringUpdate = "queue-update";
    internal const string QueueCommit = "queue-commit";
    internal const string ResultCommit = "result-commit";

    // 原生回调及连接观察对象留存至这个限时测试子进程退出；不能在 sqlite3_close 前回收 delegate。
    private static DeliveryMigrationTransactionGate? _installed;
    private readonly ConcurrentBag<ConnectionObserver> _connections = [];
    private readonly string _directory;
    private readonly string _data;
    private readonly Extension _extension;
    private readonly SetUpdate _setUpdate;
    private readonly SetCommit _setCommit;
    private readonly SetRollback _setRollback;
    private readonly DatabaseFilename _filename;
    private int _entered;

    private DeliveryMigrationTransactionGate(string directory, string data)
    {
        _directory = directory;
        _data = data;
        // 初始化实际产品依赖的 SQLitePCL provider，再加载同一 e_sqlite3；不引入另一个 SQLite 发行版。
        using (var initialize = new SqliteConnection("Data Source=:memory:;Pooling=False"))
        { initialize.Open(); }
        var library = NativeLibrary.Load("e_sqlite3", typeof(SQLitePCL.raw).Assembly, null);
        T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        _setUpdate = Export<SetUpdate>("sqlite3_update_hook");
        _setCommit = Export<SetCommit>("sqlite3_commit_hook");
        _setRollback = Export<SetRollback>("sqlite3_rollback_hook");
        _filename = Export<DatabaseFilename>("sqlite3_db_filename");
        _extension = Observe;
        if (Export<AutoExtension>("sqlite3_auto_extension")(_extension) != 0)
        { throw new InvalidOperationException("无法登记测试 SQLite 连接观察回调。"); }
    }

    internal static void Install(string directory, string data)
    {
        if (_installed is not null)
        { throw new InvalidOperationException("迁移探针只能在专用子进程登记一次。"); }
        _installed = new(directory, data);
    }

    private int Observe(nint database, nint error, nint api)
    {
        try
        {
            var path = Marshal.PtrToStringUTF8(_filename(database, "main"));
            if (path != Path.Combine(_data, "queue.db") && path != Path.Combine(_data, "config.db"))
            { return 0; }
            var observer = new ConnectionObserver(this, path);
            _connections.Add(observer);
            // 这些 API 返回旧的 context（并非旧函数指针）。这里核对意外的非零 context；
            // 仓库的原连接未登记这些 hook，探针只在专用子进程的新连接初始化时安装。
            if (_setUpdate(database, observer.UpdateCallback, 0) != 0 ||
                _setCommit(database, observer.CommitCallback, 0) != 0 ||
                _setRollback(database, observer.RollbackCallback, 0) != 0)
            { throw new InvalidOperationException("SQLite hook 返回了意外的旧 context，须重新核对测试探针。"); }
            return 0;
        }
        catch (Exception exception)
        {
            Environment.FailFast("测试 SQLite 连接探针失败；不得把原生回调异常传播到 SQLite。", exception);
            return 1;
        }
    }

    private void Hold(ConnectionObserver observer, string window)
    {
        if (!File.Exists(Path.Combine(_directory, ArmedFile)) ||
            File.ReadAllText(Path.Combine(_directory, ArmedFile)) != window ||
            Interlocked.CompareExchange(ref _entered, 1, 0) != 0)
        { return; }
        var json = JsonSerializer.Serialize(new
        {
            window,
            database = observer.Path,
            receiptNotifications = observer.Receipts,
            deliveryUpdateNotifications = observer.Deliveries,
            resultUpdateNotifications = observer.Results,
            auditInsertNotifications = observer.Audits,
        });
        var path = Path.Combine(_directory, EnteredFile);
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path);
        Console.WriteLine($"TEST MIGRATION GATE ENTERED: {json}");
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(_directory, ReleasedFile)))
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(60))
            { throw new TimeoutException("迁移事务探针未在期限内释放。"); }
            Thread.Sleep(10);
        }
        Console.WriteLine("TEST MIGRATION GATE RELEASED");
    }

    private sealed class ConnectionObserver
    {
        private readonly DeliveryMigrationTransactionGate _owner;
        internal ConnectionObserver(DeliveryMigrationTransactionGate owner, string path)
        {
            _owner = owner;
            Path = path;
            UpdateCallback = Update;
            CommitCallback = Commit;
            RollbackCallback = Reset;
        }
        internal string Path { get; }
        internal int Receipts { get; private set; }
        internal int Deliveries { get; private set; }
        internal int Results { get; private set; }
        internal int Audits { get; private set; }
        internal UpdateHook UpdateCallback { get; }
        internal CommitHook CommitCallback { get; }
        internal RollbackHook RollbackCallback { get; }

        private void Update(nint context, int action, nint database, nint table, long row)
        {
            try
            {
                if (Marshal.PtrToStringUTF8(database) != "main")
                { return; }
                var name = Marshal.PtrToStringUTF8(table);
                if (action == 18 && name == "delivery_migration_receipt")
                { Receipts++; }
                if (action == 23 && name == "delivery" && Receipts > 0)
                {
                    Deliveries++;
                    if (Deliveries == 2)
                    { _owner.Hold(this, DuringUpdate); }
                }
                if (action == 23 && name == "delivery_migration")
                { Results++; }
                if (action == 18 && name == "audit_log")
                { Audits++; }
            }
            catch (Exception exception) { Environment.FailFast("测试 SQLite UPDATE 探针失败。", exception); }
        }

        private int Commit(nint context)
        {
            try
            {
                if (Receipts > 0)
                { _owner.Hold(this, QueueCommit); }
                if (Results > 0 && Audits > 0)
                { _owner.Hold(this, ResultCommit); }
                Reset(context);
                return 0; // 观察并延长原窗口；绝不通过非零返回值回滚真实事务。
            }
            catch (Exception exception)
            {
                Environment.FailFast("测试 SQLite COMMIT 探针失败。", exception);
                return 1;
            }
        }

        private void Reset(nint context) { Receipts = 0; Deliveries = 0; Results = 0; Audits = 0; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AutoExtension(Extension callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Extension(nint database, nint error, nint api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint DatabaseFilename(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint SetUpdate(nint database, UpdateHook callback, nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint SetCommit(nint database, CommitHook callback, nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint SetRollback(nint database, RollbackHook callback, nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void UpdateHook(nint context, int action, nint database, nint table, long row);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CommitHook(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RollbackHook(nint context);
}
