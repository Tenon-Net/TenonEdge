namespace TenonEdge.Abstractions.Contracts;

/// <summary>运行时拥有连接实例。工厂只构造本地对象，不执行网络 I/O；同标识的配置不一致时拒绝借用。</summary>
public interface ISharedConnections
{
    IConnectionLease<T> Acquire<T>(long connectionId, string protocol, string configurationIdentity, Func<T> create)
        where T : class, IAsyncDisposable;
}

/// <summary>借用期间经串行执行器访问会话。调用方提供包含排队时间的期限；最后一个借用释放后销毁会话。</summary>
public interface IConnectionLease<T> : IAsyncDisposable where T : class, IAsyncDisposable
{
    Task<TResult> ExecuteAsync<TResult>(Func<T, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken);
}
