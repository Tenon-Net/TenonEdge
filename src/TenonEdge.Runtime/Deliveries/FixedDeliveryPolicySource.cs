using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Runtime.Deliveries;

/// <summary>非Host调用者沿用原构造参数。没有热修改，不需要锁或第二套写者。</summary>
internal sealed class FixedDeliveryPolicySource(DeliveryPolicy policy) : IDeliveryPolicySource
{
    public DeliveryPolicy Read() => policy;
    public IDisposable AcquireRead() => EmptyLease.Instance;
    private sealed class EmptyLease : IDisposable
    {
        public static readonly EmptyLease Instance = new();
        public void Dispose() { }
    }
}
