namespace TenonEdge.Abstractions.Contracts;

/// <summary>运行时拥有连接实例。工厂只构造本地对象，不执行网络 I/O；同标识的配置不一致时拒绝借用。</summary>
public interface ISharedConnections
{
    /// <summary>只读核对指定业务连接的当前会话。不存在或配置不匹配时返回 null；独立诊断不可查询。
    /// 调用者须使用有效计划的协议/配置身份，不能把草稿预览租约当作已生效连接。</summary>
    SharedConnectionObservation? Observe(long connectionId, string protocol, string configurationIdentity);

    /// <summary>独立诊断会话，不占用或复用业务连接标识；共享总连接上限、串行执行和释放责任。
    /// 只用于协议要求独立身份的短期操作（如 MQTT 采样）；工厂仍禁止网络 I/O。</summary>
    IConnectionLease<T> AcquireDiagnostic<T>(string protocol, Func<T> create) where T : class, IAsyncDisposable;

    IConnectionLease<T> Acquire<T>(long connectionId, string protocol, string configurationIdentity, Func<T> create)
        where T : class, IAsyncDisposable;
}

/// <summary>借用期间经串行执行器访问会话。调用方提供包含排队时间的期限；最后一个借用释放后销毁会话。</summary>
public interface IConnectionLease<T> : IAsyncDisposable where T : class, IAsyncDisposable
{
    Task<TResult> ExecuteAsync<TResult>(Func<T, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken);
}
