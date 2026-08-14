using System.Globalization;

namespace HeadphoneLogger.Storage;

/// <summary>时间序列化口径：一律存 UTC ISO8601（无小数秒），读取后按需转本地。详见 spec「时区口径」。</summary>
public static class TimeFmt
{
    public const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static string Utc(DateTimeOffset t) =>
        t.ToUniversalTime().ToString(UtcFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseUtc(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>宽容解析：坏字符串不抛异常（读侧聚合用于跳过坏行）。</summary>
    public static bool TryParseUtc(string s, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
}
