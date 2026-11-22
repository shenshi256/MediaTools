using System.Net.Http;
using System.Text.Json;

namespace MediaDownloader.Core;

public sealed class QrSession
{
    public string AuthCode { get; init; } = "";
    public string Url { get; init; } = "";
}

public sealed class LoginResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public Dictionary<string, string> Cookies { get; init; } = new();
}

/// <summary>
/// TV 端扫码登录。用户用 B站手机客户端扫码确认，密码全程不经过本机以外的地方（也不经过本程序）。
/// </summary>
public sealed class LoginService : IDisposable
{
    private const string AppKey = "4409e2ce8ffd12b8";
    // TV 端 appsec（bilibili-API-collect）；注意尾段是 83dd
    private const string AppSecret = "59b43e04ad6965f34319062b478f83dd";
    private const string Base = "https://passport.bilibili.com/x/passport-tv-login/qrcode";

    private readonly HttpClient _http;

    public LoginService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Add("User-Agent", BilibiliClient.UserAgent);
    }

    public void Dispose() => _http.Dispose();

    public async Task<QrSession> RequestQrAsync(CancellationToken ct = default)
    {
        var form = new SortedDictionary<string, string>
        {
            ["appkey"] = AppKey,
            ["local_id"] = "0",
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
        };
        var doc = await PostAsync($"{Base}/auth_code", form, ct);
        CheckCode(doc);

        var data = doc.RootElement.GetProperty("data");
        return new QrSession
        {
            AuthCode = data.GetProperty("auth_code").GetString() ?? "",
            Url = data.GetProperty("url").GetString() ?? ""
        };
    }

    /// <summary>轮询直到扫码完成或超时。</summary>
    public async Task<LoginResult> PollUntilDoneAsync(QrSession session, TimeSpan timeout,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var form = new SortedDictionary<string, string>
            {
                ["appkey"] = AppKey,
                ["auth_code"] = session.AuthCode,
                ["local_id"] = "0",
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
            };

            JsonDocument doc;
            try { doc = await PostAsync($"{Base}/poll", form, ct); }
            catch (Exception ex) { return new LoginResult { Success = false, Message = ex.Message }; }

            using (doc)
            {
                var root = doc.RootElement;
                var code = root.GetProperty("code").GetInt32();

                if (code == 0)
                {
                    var cookies = new Dictionary<string, string>();
                    if (root.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("cookie_info", out var info) &&
                        info.TryGetProperty("cookies", out var list))
                    {
                        foreach (var c in list.EnumerateArray())
                        {
                            var name = c.GetProperty("name").GetString();
                            var value = c.GetProperty("value").GetString();
                            if (!string.IsNullOrEmpty(name)) cookies[name] = value ?? "";
                        }
                    }
                    status?.Report("登录成功");
                    return new LoginResult { Success = true, Cookies = cookies };
                }

                status?.Report(code switch
                {
                    86038 => "二维码已失效",
                    86039 => "等待扫码…",
                    86090 => "已扫码，请在手机上确认",
                    _ => $"等待中（{code}）"
                });

                if (code == 86038) return new LoginResult { Success = false, Message = "二维码已失效，请重新获取" };
                if (code is not (86039 or 86090))
                {
                    var msg2 = root.TryGetProperty("message", out var m2) ? m2.GetString() ?? "" : "";
                    return new LoginResult { Success = false, Message = $"登录接口返回 {code}：{msg2}" };
                }
            }

            await Task.Delay(2000, ct);
        }
        return new LoginResult { Success = false, Message = "登录超时" };
    }

    private static void CheckCode(JsonDocument doc)
    {
        var code = doc.RootElement.GetProperty("code").GetInt32();
        if (code == 0) return;
        var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        throw new BilibiliException(code, msg);
    }

    private async Task<JsonDocument> PostAsync(string url, SortedDictionary<string, string> form, CancellationToken ct)
    {
        var query = string.Join("&", form.Select(kv => $"{kv.Key}={kv.Value}"));
        var sign = Wbi.Md5Hex(query + AppSecret);
        var body = new FormUrlEncodedContent(
            form.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).Append(new("sign", sign)));

        using var resp = await _http.PostAsync(url, body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(text);
    }
}
