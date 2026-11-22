using System.Security.Cryptography;
using System.Text;

namespace MediaDownloader.Core;

/// <summary>
/// B站 WBI 签名。算法：nav 接口取 img_key/sub_key，按固定重排表取前 32 位得 mixin key，
/// 参数按 key 升序排序、wts 时间戳、value 过滤 !'()* 后拼接，MD5(query + mixin_key) 作为 w_rid。
/// </summary>
public static class Wbi
{
    private static readonly int[] MixinKeyEncTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13
    };

    public static string BuildMixinKey(string imgKey, string subKey)
    {
        var raw = imgKey + subKey;
        var sb = new StringBuilder(32);
        foreach (var i in MixinKeyEncTab)
        {
            if (i < raw.Length) sb.Append(raw[i]);
        }
        return sb.ToString();
    }

    /// <summary>对参数字典追加 wts 与 w_rid。</summary>
    public static void Sign(SortedDictionary<string, string> query, string mixinKey)
    {
        var wts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        query["wts"] = wts;

        var sb = new StringBuilder();
        foreach (var (k, v) in query)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(Filter(v)));
        }
        query["w_rid"] = Md5Hex(sb.ToString() + mixinKey);
    }

    private static string Filter(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '!' or '\'' or '(' or ')' or '*') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Md5Hex(string s)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
