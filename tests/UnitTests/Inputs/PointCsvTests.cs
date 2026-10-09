using System.Text;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;

namespace TenonEdge.UnitTests.Inputs;

public sealed class PointCsvTests
{
    private const string Header = "inputKey,key,name,functionCode,address,addressNotation,type,bit,byteOrder,unit,enabled\r\n";
    private const string Valid = "fast,p,温度,3,0,offset,uint16,,,℃,true\r\n";

    [Theory]
    [InlineData("Offset")]
    [InlineData("Manual")]
    public void ExportHasBomAndRoundTripsQuotedMetadataAndAllAddressFamilies(string addressNotation)
    {
        var notation = Enum.Parse<ModbusAddressNotation>(addressNotation);
        var points = Enumerable.Range(1, 4).Select(fc => new ModbusPointSettings
        {
            Key = "p" + fc,
            FunctionCode = (byte)fc,
            Address = 65535,
            Type = fc < 3 ? "bool" : "uint16",
            Name = "设备,\"温度\"\r\n第二行",
            Unit = "'unit",
            Enabled = fc % 2 == 0,
        }).ToArray();
        var bytes = PointCsv.Export(points.Select(point => ("fast", point)), notation);
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, bytes[..3]);
        var rows = PointCsv.Parse(bytes, PointCsvEncoding.Utf8);
        Assert.Equal(points, rows.Select(row => row.Point));
        Assert.Equal(2, rows[0].Line);
        Assert.Equal(4, rows[1].Line);
        Assert.All(rows, row => Assert.Equal("fast", row.InputKey));
    }

    [Fact]
    public void GbkIsExplicitAndUtf8DoesNotSilentlyReplaceUndecodableBytes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(936).GetBytes(Header + Valid);
        Assert.Equal("温度", Assert.Single(PointCsv.Parse(bytes, PointCsvEncoding.Gbk)).Point.Name);
        Assert.Throws<PointCsvException>(() => PointCsv.Parse(bytes, PointCsvEncoding.Utf8));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://example.test\")")]
    [InlineData("+SUM(1,2)")]
    [InlineData("-formula")]
    [InlineData("@formula")]
    [InlineData("\t=cmd")]
    [InlineData(" =cmd")]
    [InlineData("\0=cmd")]
    [InlineData("\uFEFF=cmd")]
    [InlineData("'literal")]
    public void ExportNeutralizesSpreadsheetMetadataAndImportReversesItsEscape(string name)
    {
        var point = new ModbusPointSettings { Key = "p", Name = name, FunctionCode = 3, Address = 0, Type = "uint16", Unit = name };
        var bytes = PointCsv.Export([("fast", point)], ModbusAddressNotation.Offset);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"'", text);
        var restored = Assert.Single(PointCsv.Parse(bytes, PointCsvEncoding.Utf8));
        Assert.Equal(name, restored.Point.Name);
        Assert.Equal(name, restored.Point.Unit);
    }

    [Fact]
    public void SemanticErrorsCarryPhysicalLineNumbersAndDoNotReturnPartialRows()
    {
        var csv = Header + Valid + "fast,q,x,3,40001,manual,uint16,,,℃,true\r\n" +
            "fast,q,x,3,0,offset,uint16,,,℃,true\r\n" + "fast,bad key,x,3,0,offset,uint16,,,℃,true\r\n";
        var error = Assert.Throws<PointCsvException>(() => PointCsv.Parse(Encoding.UTF8.GetBytes(csv), PointCsvEncoding.Utf8));
        Assert.Equal([4, 5], error.Issues.Select(issue => issue.Line));
    }

    [Theory]
    [InlineData("fast,p,x,3,40001,unknown,uint16,,,u,true")]
    [InlineData("fast,p,x,4,40001,manual,uint16,,,u,true")]
    [InlineData("fast,p,x,3,65535,offset,uint32,,ABCD,u,true")]
    [InlineData("fast,p,x,3,0,offset,bit,16,,u,true")]
    [InlineData("fast,p,x,3,0,offset,uint16,,ABCD,u,true")]
    [InlineData("fast,p,x,3,0,offset,uint16,,,u,yes")]
    [InlineData("fast,p,\"unterminated")]
    [InlineData("fast,p,\"x\"oops,3,0,offset,uint16,,,u,true")]
    public void InvalidTypesAddressesBitsFlagsAndQuotesAreRejected(string row) =>
        Assert.Throws<PointCsvException>(() => PointCsv.Parse(Encoding.UTF8.GetBytes(Header + row), PointCsvEncoding.Utf8));

    [Fact]
    public void HeaderOrderCanChangeButUnknownOrDuplicateColumnsCannot()
    {
        var header = Header.Trim().Split(',');
        var values = Valid.Trim().Split(',');
        var bytes = Encoding.UTF8.GetBytes(string.Join(',', header.Reverse()) + "\n" + string.Join(',', values.Reverse()));
        Assert.Equal("p", Assert.Single(PointCsv.Parse(bytes, PointCsvEncoding.Utf8)).Point.Key);
        Assert.Throws<PointCsvException>(() => PointCsv.Parse(Encoding.UTF8.GetBytes(Header.Replace("name", "key", StringComparison.Ordinal) + Valid), PointCsvEncoding.Utf8));
        Assert.Empty(PointCsv.Parse(Encoding.UTF8.GetBytes(Header), PointCsvEncoding.Utf8));
    }

    [Fact]
    public void ByteAndRowLimitsAreEnforcedAtBoundary()
    {
        Assert.Throws<PointCsvException>(() => PointCsv.Parse(new byte[PointCsv.MaximumBytes + 1], PointCsvEncoding.Utf8));
        var boundary = Enumerable.Repeat((byte)'\n', PointCsv.MaximumBytes).ToArray();
        Encoding.UTF8.GetBytes(Header + Valid).CopyTo(boundary, 0);
        Assert.Single(PointCsv.Parse(boundary, PointCsvEncoding.Utf8));
        var rows = Enumerable.Range(0, 10000).Select(index => Valid.Replace(",p,", ",p" + index + ",", StringComparison.Ordinal));
        var csv = Header + string.Concat(rows);
        Assert.Equal(10000, PointCsv.Parse(Encoding.UTF8.GetBytes(csv), PointCsvEncoding.Utf8).Length);
        Assert.Throws<PointCsvException>(() => PointCsv.Parse(Encoding.UTF8.GetBytes(csv + Valid), PointCsvEncoding.Utf8));
    }
}
