using TenonEdge.Abstractions.Messages;
using TenonEdge.Host.Features.Devices;

namespace TenonEdge.UnitTests.Devices;

public sealed class DeviceCopyKeyTests
{
    [Theory]
    [InlineData("a", 1)]
    [InlineData("meter-1.site_A", 9007199254740991)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 9007199254740991)]
    public void CopyKeyIsBoundedValidAndDistinguishesIds(string key, long id)
    {
        var result = DeviceDraftStore.CopyKey(key, id);
        Assert.True(BusinessKeys.IsValid(result));
        Assert.NotEqual(key, result);
        Assert.NotEqual(result, DeviceDraftStore.CopyKey(key, id - 1));
    }
}
