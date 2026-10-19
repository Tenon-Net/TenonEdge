using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Api;

/// <summary>
/// 内置 OpenAPI 生成的管理 API 契约：版本、稳定且唯一的 operationId、显式的响应类型（总设计 §10.1）。
/// </summary>
public sealed partial class OpenApiDocumentTests(DevelopmentApplicationFactory factory) : IClassFixture<DevelopmentApplicationFactory>
{
    private const string DocumentPath = "/api/edge-admin/v1/openapi/v1.json";

    private static readonly string[] OperationKeys = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    /// <summary>
    /// 当前契约的全部 operationId。新增或修改端点时显式更新本清单，
    /// 防止重命名内部类型等改动无意改变客户端契约。
    /// </summary>
    private static readonly string[] ExpectedOperationIds =
    [
        "analyzeRouteFields",
        "applyConfigurationDraft",
        "cancelAuditExport",
        "cancelConfigurationImport",
        "cancelConfigurationMigrationIntentRetryPreparation",
        "cancelConfigurationMigrationPreparation",
        "cancelDeliveryExport",
        "cancelDeliveryMigrationPreparation",
        "changePassword",
        "cleanupDeliveryRecords",
        "clearConnectionTlsTrust",
        "clearHttpCredential",
        "clearModuleCredential",
        "clearMqttCredential",
        "clearPendingDeliveries",
        "compareConfigurationVersionToDraft",
        "compareConfigurationVersions",
        "compileRouteDrafts",
        "completeSetup",
        "confirmConfigurationImport",
        "confirmConfigurationMigration",
        "confirmConfigurationMigrationIntentRetry",
        "confirmDeliveryExport",
        "confirmDeliveryMigration",
        "confirmRouteTestSend",
        "copyDeviceDraft",
        "copyRouteDraft",
        "createApiToken",
        "createConnectionDraft",
        "createDeviceDraft",
        "createInputDraft",
        "createRouteDraft",
        "createUser",
        "deleteConnectionDraft",
        "deleteDeviceDraft",
        "deleteInputDraft",
        "deleteRouteDraft",
        "deleteUser",
        "discardConfigurationDraft",
        "downloadAuditExport",
        "downloadDeliveryExport",
        "endUserSessions",
        "exportConfigurationDraft",
        "exportDevicePointCsv",
        "getAuditExport",
        "getConfigurationDraftSummary",
        "getConfigurationMigrationIntent",
        "getConfigurationMigrationIntentRetryPreparation",
        "getConfigurationMigrationIntents",
        "getConfigurationMigrationPreparation",
        "getConfigurationOperation",
        "getConfigurationRuntimeState",
        "getConfigurationVersion",
        "getConnectionDeletionReview",
        "getConnectionDrafts",
        "getConnectionModules",
        "getConnectionTlsTrust",
        "getCurrentConfigurationImport",
        "getDeliveryBody",
        "getDeliveryExport",
        "getDeliveryMigrationOutcome",
        "getDeliveryMigrationPreparation",
        "getDeliveryRecords",
        "getDeviceDeletionReview",
        "getDeviceDrafts",
        "getHttpCredentialStatus",
        "getInputDrafts",
        "getInputModules",
        "getLiveness",
        "getMigrationDeliveryRecords",
        "getModuleCredentialStatus",
        "getMqttCredentialStatus",
        "getOutboundPolicy",
        "getOutputControl",
        "getOutputModules",
        "getParameterSchema",
        "getQueueParameters",
        "getReadiness",
        "getRouteCatalog",
        "getRouteDraft",
        "getRouteDrafts",
        "getRuntimeConnections",
        "getRuntimeCurrentValues",
        "getRuntimeDevices",
        "getRuntimeMessageBody",
        "getRuntimeMessageObjects",
        "getRuntimeMessages",
        "getRuntimeOutput",
        "getRuntimeOutputs",
        "getRuntimeParameters",
        "getRuntimeStatus",
        "getRuntimeTrends",
        "getSession",
        "getSetupStatus",
        "getSystemInfo",
        "getTransformModules",
        "getTrustedProxies",
        "getUserCreatedTokens",
        "importDevicePointCsv",
        "inspectConfigurationMigrationIntent",
        "inspectConnectionOutbound",
        "inspectConnectionTrustCertificate",
        "listApiTokens",
        "listAuditRecords",
        "listConfigurationOperations",
        "listConfigurationVersions",
        "listRouteRecentMessages",
        "listRuntimeEvents",
        "listUsers",
        "login",
        "logout",
        "prepareConfigurationImport",
        "prepareConfigurationMigration",
        "prepareConfigurationMigrationIntentRetry",
        "prepareDeliveryExport",
        "prepareDeliveryMigration",
        "prepareRouteTestSend",
        "previewHttpDevice",
        "previewModbusDevice",
        "previewMqttSource",
        "previewRouteHttpSample",
        "previewRouteModbusSample",
        "previewRouteMqttSample",
        "previewRouteRecentSample",
        "previewRouteSample",
        "recoverCorruptSnapshot",
        "resetUserPassword",
        "retryConfigurationRuntime",
        "retryFailedDeliveries",
        "retryOutputNow",
        "reviewClientCertificateCredential",
        "reviewConfigurationDraft",
        "reviewConfigurationRollback",
        "reviewCorruptSnapshotRecovery",
        "reviewCredentialChange",
        "reviewModuleCredential",
        "revokeApiToken",
        "rollbackConfigurationVersion",
        "startAuditExport",
        "testHttpConnection",
        "testModbusConnection",
        "testMqttConnection",
        "unlockUser",
        "updateConnectionDraft",
        "updateDeviceDraft",
        "updateInputDraft",
        "updateRouteDraft",
        "updateUser",
        "writeConnectionTlsTrust",
        "writeGatewayName",
        "writeHttpCredential",
        "writeModuleCredential",
        "writeMqttCredential",
        "writeOutboundPolicy",
        "writeOutputControl",
        "writeQueueParameters",
        "writeRuntimeParameters",
        "writeTrustedProxies",
    ];

    [Fact]
    public async Task DocumentIsOpenApi31WithManagementApiInfo()
    {
        using var document = await GetDocumentAsync();
        var root = document.RootElement;

        Assert.StartsWith("3.1.", root.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        Assert.Equal("TenonEdge 管理 API", root.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("v1", root.GetProperty("info").GetProperty("version").GetString());
    }

    [Fact]
    public async Task EveryOperationHasExpectedUniqueLowerCamelCaseOperationId()
    {
        using var document = await GetDocumentAsync();

        var operationIds = Operations(document)
            .Select(operation => operation.TryGetProperty("operationId", out var id) ? id.GetString() : null)
            .ToList();

        Assert.All(operationIds, id => Assert.Matches(LowerCamelCase(), id ?? string.Empty));
        Assert.Equal(operationIds.Count, operationIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ExpectedOperationIds, operationIds.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("/healthz", "200")]
    [InlineData("/readyz", "200", "503")]
    public async Task HealthOperationsDeclarePlainTextResponses(string path, params string[] statusCodes)
    {
        using var document = await GetDocumentAsync();

        var responses = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("get").GetProperty("responses");

        Assert.Equal(statusCodes, responses.EnumerateObject().Select(response => response.Name).Order(StringComparer.Ordinal));
        Assert.All(
            responses.EnumerateObject(),
            response => Assert.True(response.Value.GetProperty("content").TryGetProperty("text/plain", out _)));
    }

    private static IEnumerable<JsonElement> Operations(JsonDocument document) =>
        document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .Where(member => OperationKeys.Contains(member.Name, StringComparer.Ordinal))
            .Select(member => member.Value);

    private async Task<JsonDocument> GetDocumentAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri(DocumentPath, UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
    private static partial Regex LowerCamelCase();
}
