using System.Globalization;
using System.Text.Json;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Devices;

namespace TenonEdge.Host.Features.Inputs;

internal sealed partial class InputDraftStore
{
    public byte[] ExportPoints(long deviceId, string expectedVersion, bool template) => database.InTransaction(client =>
    {
        ConfigurationDraft.CheckVersion(client, expectedVersion);
        var device = DeviceDraftStore.Read(client, deviceId) ?? throw new DraftWriteException("device.notFound");
        if (device.Kind != DeviceKind.Modbus)
        { throw new PointCsvException(new PointCsvIssue(1, "点表 CSV 只适用于 Modbus 设备。")); }
        var inputs = ReadForDevice(client, deviceId);
        return PointCsv.Export(template
            ? [(inputs.FirstOrDefault()?.Key ?? "input_1", new ModbusPointSettings { Key = "point_1", FunctionCode = 3, Address = 0, Type = "uint16", Enabled = false })]
            : inputs.SelectMany(input => input.Modbus!.Points.Select(point => (input.Key, point))), device.AddressNotation);
    });

    public PointCsvImportResult ImportPoints(long deviceId, PointCsvImportRequest request, string? account, string? sourceIp)
    {
        if (request.Mode is null || !Enum.IsDefined(request.Mode.Value) || request.Encoding is null ||
            string.IsNullOrEmpty(request.ExpectedVersion) || request.ExpectedVersion.Length > 64)
        { throw new PointCsvException(new PointCsvIssue(1, "须明确指定草稿版本、导入模式和文件编码。")); }
        var rows = PointCsv.Parse(request.Content, request.Encoding.Value);
        return database.InTransaction(client =>
        {
            ConfigurationDraft.CheckVersion(client, request.ExpectedVersion);
            var device = DeviceDraftStore.Read(client, deviceId) ?? throw new DraftWriteException("device.notFound");
            if (device.Kind != DeviceKind.Modbus)
            { throw new PointCsvException(new PointCsvIssue(1, "点表 CSV 只适用于 Modbus 设备。")); }
            var inputs = ReadForDevice(client, deviceId).ToDictionary(input => input.Key, StringComparer.Ordinal);
            var points = inputs.ToDictionary(pair => pair.Key, pair => pair.Value.Modbus!.Points.ToDictionary(point => point.Key, StringComparer.Ordinal), StringComparer.Ordinal);
            var owners = inputs.Values.SelectMany(input => input.Modbus!.Points.Select(point => (point.Key, InputKey: input.Key)))
                .ToDictionary(point => point.Key, point => point.InputKey, StringComparer.Ordinal);
            var issues = new List<PointCsvIssue>();
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var added = 0;
            var updated = 0;
            var skipped = 0;
            foreach (var row in rows)
            {
                if (!points.TryGetValue(row.InputKey, out var target))
                { issues.Add(new(row.Line, "来源不存在，请先在所选设备创建采集组。")); continue; }
                if (owners.TryGetValue(row.Point.Key, out var owner))
                {
                    if (request.Mode == PointCsvMode.Incremental)
                    { skipped++; continue; }
                    points[owner].Remove(row.Point.Key);
                    changed.Add(owner);
                    updated++;
                }
                else
                { added++; }
                target.Add(row.Point.Key, row.Point);
                changed.Add(row.InputKey);
            }
            // Check final group sizes after moves, not a transient intermediate size.
            foreach (var key in changed.Where(key => points[key].Count > PointCsv.MaximumRows))
            { issues.Add(new(rows.First(row => row.InputKey == key).Line, "导入后单个采集组超过 10000 点。")); }
            if (issues.Count != 0)
            { throw new PointCsvException(issues.ToArray()); }
            foreach (var key in changed)
            {
                var input = inputs[key] with { Modbus = inputs[key].Modbus! with { Points = points[key].Values.ToArray() } };
                client.Updateable(new InputDraftRecord
                {
                    Id = input.Id,
                    DeviceId = deviceId,
                    BusinessKey = input.Key,
                    Content = JsonSerializer.Serialize(input, Json),
                }).ExecuteCommand();
                ConfigurationEditJournal.Record(client, "input", input.Id, account, time);
            }
            // Even a wholly skipped import is an audited, version-fenced user operation.
            var version = ConfigurationDraft.Advance(client, time);
            audit.Append(client, new AuditEntry("input.pointsImported", AuditResults.Success, account, sourceIp,
                deviceId.ToString(CultureInfo.InvariantCulture), JsonSerializer.Serialize(new
                { mode = request.Mode.ToString(), added, updated, skipped, previousVersion = request.ExpectedVersion, version })));
            return new PointCsvImportResult(version, added, updated, skipped);
        });
    }
}
