using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Credentials;

namespace TenonEdge.IntegrationTests.Adapters;

/// <summary>真实 Kestrel HTTPS 对端，使用独立测试 CA 验证客户端，不忽略证书链错误。</summary>
internal sealed class MutualTlsHttpPeer : IAsyncDisposable
{
    private readonly ECDsa _rootKey = ECDsa.Create();
    private readonly ECDsa _serverKey = ECDsa.Create();
    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _server;
    private WebApplication? _app;
    private MutualTlsHttpPeer()
    {
        _root = ConnectionTrustTests.Certificate(_rootKey, "fixture-client-ca", authority: true);
        _server = ConnectionTrustTests.Certificate(_serverKey, "localhost", issuer: _root);
    }

    internal string Recipient { get; private set; } = "";
    internal ConcurrentQueue<Request> Requests { get; } = new();
    internal TlsTrustSnapshot Trust => new(TlsTrustMode.PinnedCertificate, Convert.ToBase64String(_server.RawData), "fixture-trust");
    internal const string Response = "{\"counter\":9007199254740993}";

    internal static async Task<MutualTlsHttpPeer> StartAsync(bool requireCertificate, CancellationToken token)
    {
        var peer = new MutualTlsHttpPeer();
        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = peer._server;
                https.ClientCertificateMode = requireCertificate ? ClientCertificateMode.RequireCertificate : ClientCertificateMode.AllowCertificate;
                https.ClientCertificateValidation = (certificate, _, _) =>
                {
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(peer._root);
                    chain.ChainPolicy.DisableCertificateDownloads = true;
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
                    return chain.Build(certificate);
                };
            })));
            var app = peer._app = builder.Build();
            app.Run(async context =>
            {
                using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body, context.RequestAborted);
                var certificate = context.Connection.ClientCertificate;
                peer.Requests.Enqueue(new(context.Connection.Id, certificate?.Subject, certificate?.GetCertHashString(HashAlgorithmName.SHA256),
                    context.Request.Method, context.Request.Headers.Authorization.ToString(), body.ToArray()));
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(Response, context.RequestAborted);
            });
            await app.StartAsync(token);
            peer.Recipient = app.Urls.Single().Replace("127.0.0.1", "localhost", StringComparison.Ordinal).TrimEnd('/') + "/";
            return peer;
        }
        catch { await peer.DisposeAsync(); throw; }
    }

    internal ClientCertificateUpload Issue(string name = "gateway-client", bool pfx = false)
    {
        using var key = ECDsa.Create();
        using var certificate = ConnectionTrustTests.Certificate(key, name, issuer: _root, clientOnly: true);
        if (!pfx)
        { return new(CertificatePem: certificate.ExportCertificatePem() + "\n" + _root.ExportCertificatePem(), PrivateKeyPem: key.ExportPkcs8PrivateKeyPem()); }
        // 证书集合中的 CA 不能含另一份私钥。
        using var publicRoot = X509CertificateLoader.LoadCertificate(_root.RawData);
        var bytes = new X509Certificate2Collection { certificate, publicRoot }.Export(X509ContentType.Pfx, "fixture-password")!;
        try
        { return new(Pkcs12: Convert.ToBase64String(bytes), Password: "fixture-password"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        { await _app.DisposeAsync(); }
        _server.Dispose();
        _root.Dispose();
        _serverKey.Dispose();
        _rootKey.Dispose();
    }

    internal sealed record Request(string Connection, string? Subject, string? Fingerprint, string Method, string Authorization, byte[] Body);
}
