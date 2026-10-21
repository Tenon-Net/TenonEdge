using Microsoft.Data.Sqlite;

namespace TenonEdge.Runtime.Deliveries;

/// <summary>
/// 管理面的队列查询与导出：只持有短连接工厂，不持有活跃连接或事务，也不参与写者调度。
/// 连接由原 DeliveryStore 完成初始化后提供；元数据查询不会加载正文，正文只从显式查看/导出入口读取。
/// 每个缓存只有一个读取器，导出核对凭据绑定该实例，不能跨缓存复用。
/// </summary>
internal sealed partial class DeliveryReader(Func<SqliteConnection> openConnection, TimeProvider time, string databasePath);
