using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Mapping;

public enum UpstreamTimeFormat { Rfc3339, UnixSeconds, UnixMilliseconds }

/// <summary>InvalidUpstreamTime 供调用方计数；无效时间不丢弃数据。纯函数不读取系统时钟或保存旧值。</summary>
public sealed record MappedTime(DateTimeOffset CollectedAt, TimeSource Source, bool InvalidUpstreamTime);

/// <summary>显式上游时间映射（设计 §6.4）；Unix 时间必须声明单位，RFC 时间必须携带偏移。</summary>
public sealed partial class UpstreamTimeMapping
{
    private readonly JsonFieldPointer _location;
    private readonly UpstreamTimeFormat _format;
    private readonly TimeSource _source;

    public UpstreamTimeMapping(string path, UpstreamTimeFormat format, TimeSource source)
    {
        if (!Enum.IsDefined(format))
        { throw new ArgumentOutOfRangeException(nameof(format)); }
        if (source is not (TimeSource.Device or TimeSource.Upstream))
        { throw new ArgumentOutOfRangeException(nameof(source)); }
        _location = new JsonFieldPointer(path);
        _format = format;
        _source = source;
    }

    public MappedTime Resolve(JsonElement payload, DateTimeOffset receivedAt, ClockSyncState clockState)
    {
        if (!Enum.IsDefined(clockState))
        { throw new ArgumentOutOfRangeException(nameof(clockState)); }
        receivedAt = receivedAt.ToUniversalTime();
        if (_location.TryResolve(payload, out var value) && TryParse(value, out var at) &&
            (clockState != ClockSyncState.Synchronized || at - receivedAt <= TimeSpan.FromMinutes(5)))
        {
            return new(at, _source, false);
        }
        return new(receivedAt, clockState == ClockSyncState.NotSynchronized ? TimeSource.GatewayUnsynced : TimeSource.Gateway, true);
    }

    private bool TryParse(JsonElement value, out DateTimeOffset at)
    {
        at = default;
        if (_format == UpstreamTimeFormat.Rfc3339)
        {
            if (value.ValueKind != JsonValueKind.String)
            { return false; }
            var text = value.GetString()!;
            var match = Rfc3339().Match(text);
            if (!match.Success)
            { return false; }
            // RFC 3339 不限定小数位数，内部精度为 100ns；多余位截断，避免改变时间先后。
            var fraction = match.Groups["fraction"].Value;
            if (fraction.Length > 8)
            { fraction = fraction[..8]; }
            var normalized = match.Groups["date"].Value + "T" + match.Groups["time"].Value + fraction;
            // DateTime 不表示闰秒；此类无法表示的时间走无效时间回退，不伪造采样时刻。
            if (!DateTime.TryParseExact(normalized,
                ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            { return false; }
            var zone = match.Groups["offset"].Value;
            var offset = TimeSpan.Zero;
            if (zone is not ("Z" or "z"))
            {
                var hours = int.Parse(zone.AsSpan(1, 2), CultureInfo.InvariantCulture);
                var minutes = int.Parse(zone.AsSpan(4, 2), CultureInfo.InvariantCulture);
                if (hours > 23 || minutes > 59)
                { return false; }
                offset = new TimeSpan(hours, minutes, 0);
                if (zone[0] == '-')
                { offset = -offset; }
            }
            try
            {
                // RFC 偏移范围不套用 DateTimeOffset 构造函数的 ±14h 限制，结果始终是 UTC。
                at = new DateTimeOffset(local.Ticks - offset.Ticks, TimeSpan.Zero);
                return true;
            }
            catch (ArgumentOutOfRangeException) { return false; }
        }

        if (value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
        { return false; }
        var raw = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        if (!UnixNumber().IsMatch(raw) || !decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out var unix))
        { return false; }
        try
        {
            var ticksPerUnit = _format == UpstreamTimeFormat.UnixSeconds ? TimeSpan.TicksPerSecond : TimeSpan.TicksPerMillisecond;
            // 不经 double，负时间同样相对于 UnixEpoch；只截断低于内部精度的部分。
            var ticks = decimal.ToInt64(decimal.Truncate(checked(unix * ticksPerUnit)));
            at = DateTimeOffset.UnixEpoch.AddTicks(ticks);
            return true;
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"\A[+-]?[0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex UnixNumber();

    [GeneratedRegex(@"\A(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})[Tt](?<time>[0-9]{2}:[0-9]{2}:[0-9]{2})(?<fraction>\.[0-9]+)?(?<offset>[Zz]|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Rfc3339();
}
