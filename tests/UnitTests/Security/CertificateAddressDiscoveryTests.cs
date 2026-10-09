using System.Net.NetworkInformation;
using TenonEdge.Host.Security;

namespace TenonEdge.UnitTests.Security;

public sealed class CertificateAddressDiscoveryTests
{
    [Fact]
    public void NetworkEnumerationFailuresReturnNoOptionalIpAddresses()
    {
        Assert.Empty(HttpsCertificateStore.LocalUnicastAddresses(() => throw new NetworkInformationException()));
        Assert.Empty(HttpsCertificateStore.LocalUnicastAddresses(() => throw new ArgumentException("An invalid IP address was specified.", "address")));
    }

    [Fact]
    public void UnrelatedProgrammingErrorsAreNotHidden()
    {
        Assert.Throws<InvalidOperationException>(() => HttpsCertificateStore.LocalUnicastAddresses(() => throw new InvalidOperationException("fixture")));
        Assert.Throws<ArgumentException>(() => HttpsCertificateStore.LocalUnicastAddresses(() => throw new ArgumentException("fixture", "other")));
    }
}
