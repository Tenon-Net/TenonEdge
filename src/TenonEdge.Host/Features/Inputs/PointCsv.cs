using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Devices;

namespace TenonEdge.Host.Features.Inputs;

internal enum PointCsvMode { Overwrite, Incremental }
internal enum PointCsvEncoding { Utf8, Gbk }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PointCsvImportRequest(string ExpectedVersion, PointCsvMode? Mode, PointCsvEncoding? Encoding, byte[] Content);
internal sealed record PointCsvImportResult(string Version, int Added, int Updated, int Skipped);
internal sealed record PointCsvRow(int Line, string InputKey, ModbusPointSettings Point);
internal sealed record PointCsvIssue(int Line, string Message);
internal sealed class PointCsvException(params PointCsvIssue[] issues) : Exception("点表 CSV 无效。")
{
    public PointCsvIssue[] Issues { get; } = issues;
}

/// <summary>有界 CSV 编解码；只描述已有来源的点位，不创建来源、不修改周期或启用设备。</summary>
internal static class PointCsv
{
    public const int MaximumBytes = 5 * 1024 * 1024;
    public const int MaximumRows = 10000;
    private static readonly string[] Columns = ["inputKey", "key", "name", "functionCode", "address", "addressNotation", "type", "bit", "byteOrder", "unit", "enabled"];
    private static readonly UTF8Encoding Utf8 = new(false, true);

    static PointCsv() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static PointCsvRow[] Parse(byte[] bytes, PointCsvEncoding encoding)
    {
        if (bytes is null || bytes.Length > MaximumBytes || !Enum.IsDefined(encoding))
        { throw new PointCsvException(new PointCsvIssue(1, "文件为空、编码无效或超过 5 MiB。")); }
        string text;
        try
        {
            var decoder = encoding == PointCsvEncoding.Utf8 ? Utf8 : Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            text = decoder.GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException) { throw new PointCsvException(new PointCsvIssue(1, "文件编码与所选 UTF-8/GBK 不符。")); }
        var records = ReadRecords(text);
        if (records.Count == 0 || records[0].Cells.Length != Columns.Length ||
            !records[0].Cells.Order(StringComparer.Ordinal).SequenceEqual(Columns.Order(StringComparer.Ordinal)))
        { throw new PointCsvException(new PointCsvIssue(1, "CSV 表头须包含模板中的全部列，不能重复或增加未知列。")); }
        var indexes = Columns.Select(column => Array.IndexOf(records[0].Cells, column)).ToArray();
        var result = new List<PointCsvRow>();
        var issues = new List<PointCsvIssue>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records.Skip(1))
        {
            if (record.Cells.Length != Columns.Length)
            { issues.Add(new(record.Line, "列数与表头不一致。")); continue; }
            var cells = indexes.Select(index => record.Cells[index]).ToArray();
            try
            {
                TenonEdge.Abstractions.Messages.BusinessKeys.Validate(cells[0], "inputKey");
                var function = byte.Parse(cells[3], NumberStyles.None, CultureInfo.InvariantCulture);
                var address = int.Parse(cells[4], NumberStyles.None, CultureInfo.InvariantCulture);
                if (cells[5] == "manual")
                {
                    if (cells[4].Length is not (5 or 6) || cells[4].Any(c => c is < '0' or > '9'))
                    { throw new ArgumentException("CSV 字段值无效。"); }
                    var expected = cells[4][0] switch { '0' => 1, '1' => 2, '3' => 4, '4' => 3, _ => 0 };
                    if (expected != function)
                    { throw new ArgumentException("CSV 字段值无效。"); }
                    address = int.Parse(cells[4].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture) - 1;
                }
                else if (cells[5] != "offset")
                { throw new ArgumentException("CSV 字段值无效。"); }
                var point = new ModbusPointSettings
                {
                    Key = cells[1],
                    Name = DecodeText(cells[2]),
                    FunctionCode = function,
                    Address = address,
                    Type = cells[6],
                    Bit = cells[7].Length == 0 ? null : int.Parse(cells[7], NumberStyles.None, CultureInfo.InvariantCulture),
                    ByteOrder = cells[8].Length == 0 ? null : cells[8],
                    Unit = DecodeText(cells[9]),
                    Enabled = cells[10] switch { "true" => true, "false" => false, _ => throw new ArgumentException("CSV 字段值无效。") },
                };
                point.ValidateDefinition();
                if (!keys.Add(point.Key))
                { issues.Add(new(record.Line, "文件内出现重复点位 key。")); continue; }
                result.Add(new(record.Line, cells[0], point));
            }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
            { issues.Add(new(record.Line, "来源/key、地址写法/功能码、点位类型/位号/字节序、名称/单位或启用标记无效。")); }
        }
        if (issues.Count != 0)
        { throw new PointCsvException(issues.ToArray()); }
        return result.ToArray();
    }

    public static byte[] Export(IEnumerable<(string InputKey, ModbusPointSettings Point)> points, ModbusAddressNotation notation)
    {
        var text = new StringBuilder();
        Append(text, Columns);
        var count = 0;
        foreach (var (inputKey, point) in points)
        {
            if (++count > MaximumRows)
            { throw new PointCsvException(new PointCsvIssue(1, "设备点位超过单个 CSV 的 10000 行上限。")); }
            var address = point.Address!.Value.ToString(CultureInfo.InvariantCulture);
            if (notation == ModbusAddressNotation.Manual)
            {
                var number = point.Address.Value + 1;
                var prefix = point.FunctionCode switch { 1 => "0", 2 => "1", 3 => "4", 4 => "3", _ => throw new ArgumentException("CSV 字段值无效。") };
                address = prefix + number.ToString(number <= 9999 ? "D4" : "D5", CultureInfo.InvariantCulture);
            }
            Append(text, [inputKey, point.Key, EncodeText(point.Name ?? ""), point.FunctionCode.ToString(CultureInfo.InvariantCulture),
                address, notation == ModbusAddressNotation.Manual ? "manual" : "offset", point.Type,
                point.Bit?.ToString(CultureInfo.InvariantCulture) ?? "", point.ByteOrder ?? "", EncodeText(point.Unit ?? ""), point.Enabled ? "true" : "false"]);
            if (text.Length > MaximumBytes)
            { throw new PointCsvException(new PointCsvIssue(1, "CSV 超过 5 MiB。")); }
        }
        var body = Utf8.GetBytes(text.ToString());
        if (body.Length + 3 > MaximumBytes)
        { throw new PointCsvException(new PointCsvIssue(1, "CSV 超过 5 MiB。")); }
        return [0xef, 0xbb, 0xbf, .. body];
    }

    // A leading apostrophe is an explicit reversible spreadsheet-text escape.
    // Literal apostrophes are doubled; potentially executable metadata is prefixed.
    private static bool NeedsEscape(string value) => value.StartsWith('\'') ||
        value.Length != 0 && (char.IsWhiteSpace(value[0]) || char.IsControl(value[0]) || value[0] == '\uFEFF' || "=+-@".Contains(value[0]));
    private static string EncodeText(string value) => NeedsEscape(value) ? "'" + value : value;
    private static string DecodeText(string value) => value.StartsWith('\'') && NeedsEscape(value[1..]) ? value[1..] : value;
    private static void Append(StringBuilder text, IEnumerable<string> cells) =>
        text.AppendJoin(',', cells.Select(cell => "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")).Append("\r\n");

    private sealed record Record(int Line, string[] Cells);
    private static List<Record> ReadRecords(string text)
    {
        var result = new List<Record>();
        var cells = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        var closed = false;
        var line = 1;
        var startLine = 1;
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (quoted)
            {
                if (c == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    { value.Append('"'); index++; }
                    else
                    { quoted = false; closed = true; }
                }
                else
                {
                    value.Append(c);
                    if (c == '\n' || c == '\r' && (index + 1 == text.Length || text[index + 1] != '\n'))
                    { line++; }
                }
            }
            else if (c is ',' or '\r' or '\n')
            {
                cells.Add(value.ToString());
                value.Clear();
                closed = false;
                if (cells.Count > Columns.Length)
                { throw new PointCsvException(new PointCsvIssue(startLine, "CSV 列数超过模板。")); }
                if (c != ',')
                {
                    if (cells.Count != 1 || cells[0].Length != 0)
                    { AddRecord(); }
                    cells.Clear();
                    if (c == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                    { index++; }
                    line++;
                    startLine = line;
                }
            }
            else if (c == '"' && value.Length == 0 && !closed)
            { quoted = true; }
            else if (closed || c == '"')
            { throw new PointCsvException(new PointCsvIssue(startLine, "CSV 引号格式无效。")); }
            else
            { value.Append(c); }
            if (value.Length > 4096)
            { throw new PointCsvException(new PointCsvIssue(startLine, "CSV 单元格超过 4096 字符。")); }
        }
        if (quoted)
        { throw new PointCsvException(new PointCsvIssue(startLine, "CSV 引号未闭合。")); }
        if (cells.Count != 0 || value.Length != 0 || closed)
        { cells.Add(value.ToString()); AddRecord(); }
        return result;
        void AddRecord()
        {
            if (result.Count > MaximumRows)
            { throw new PointCsvException(new PointCsvIssue(startLine, "CSV 超过 10000 数据行。")); }
            result.Add(new(startLine, cells.ToArray()));
        }
    }
}
