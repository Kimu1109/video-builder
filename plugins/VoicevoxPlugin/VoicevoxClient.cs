using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoicevoxPlugin;

/// <summary>話者のスタイル 1 つ (VOICEVOX では、これが音声合成の「話者 ID」になる)。</summary>
public sealed record VoiceStyle(int Id, string Speaker, string Style)
{
    /// <summary>一覧に出す名前 (例: "ずんだもん (ノーマル)")。</summary>
    public string Label => $"{Speaker} ({Style})";
}

/// <summary>声の調整。値の範囲は VOICEVOX のエディタと同じ。</summary>
public sealed record VoiceParams
{
    /// <summary>話速 (0.5〜2、標準 1)。</summary>
    public double Speed { get; init; } = 1;
    /// <summary>音高 (-0.15〜0.15、標準 0)。</summary>
    public double Pitch { get; init; }
    /// <summary>抑揚 (0〜2、標準 1)。</summary>
    public double Intonation { get; init; } = 1;
    /// <summary>音量 (0〜2、標準 1)。</summary>
    public double Volume { get; init; } = 1;
    /// <summary>前の無音 (秒、0〜1.5)。</summary>
    public double PreSilence { get; init; } = 0.1;
    /// <summary>後の無音 (秒、0〜1.5)。</summary>
    public double PostSilence { get; init; } = 0.1;

    /// <summary>範囲に収めたもの。</summary>
    public VoiceParams Clamped() => this with
    {
        Speed = Math.Clamp(Speed, 0.5, 2),
        Pitch = Math.Clamp(Pitch, -0.15, 0.15),
        Intonation = Math.Clamp(Intonation, 0, 2),
        Volume = Math.Clamp(Volume, 0, 2),
        PreSilence = Math.Clamp(PreSilence, 0, 1.5),
        PostSilence = Math.Clamp(PostSilence, 0, 1.5),
    };
}

/// <summary>VOICEVOX のエンジンにつながらない・エラーを返した。Message はそのまま利用者に見せる。</summary>
public sealed class VoicevoxException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// VOICEVOX Engine の HTTP API を呼ぶ。同じ API を持つエンジン (AivisSpeech など) にもつながる。
/// 使うのは GET /version・GET /speakers・POST /audio_query・POST /synthesis。
/// </summary>
public sealed class VoicevoxClient
{
    public const string DefaultUrl = "http://127.0.0.1:50021";

    // 使い回す (待ち時間は呼び出しごとの CancellationToken で決める)。
    // 同じ PC のエンジンには、プロキシの設定 (環境変数 HTTP_PROXY など) があっても直接つなぐ
    private static readonly HttpClient Direct = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly HttpClient Proxied = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _http;
    public Uri BaseUri { get; }

    public VoicevoxClient(string baseUrl, HttpClient? http = null)
    {
        BaseUri = ParseUrl(baseUrl) ?? throw new VoicevoxException($"エンジンの URL が正しくありません: {baseUrl}");
        _http = http ?? (IsLocal(BaseUri) ? Direct : Proxied);
    }

    /// <summary>http / https の URL なら、末尾を "/" にそろえて返す。正しくなければ null。</summary>
    public static Uri? ParseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        var s = uri.GetLeftPart(UriPartial.Path);
        return new Uri(s.EndsWith('/') ? s : s + "/");
    }

    /// <summary>同じ PC (localhost・127.0.0.1・::1) のエンジンか。</summary>
    public static bool IsLocal(Uri uri) => uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>エンジンのバージョン (例: "0.21.1")。</summary>
    public async Task<string> GetVersionAsync(CancellationToken ct)
    {
        using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Url("version")), ct);
        var text = (await res.Content.ReadAsStringAsync(ct)).Trim();
        try { return JsonSerializer.Deserialize<string>(text) ?? text; }
        catch (JsonException) { return text; }
    }

    /// <summary>歌う用のスタイルの種類 (VOICEVOX Engine の StyleType)。</summary>
    private static readonly HashSet<string> SingingTypes = new(StringComparer.Ordinal) { "singing_teacher", "frame_decode", "sing" };

    /// <summary>しゃべる用のスタイルの一覧 (歌う用のスタイルは除く)。</summary>
    public async Task<List<VoiceStyle>> GetStylesAsync(CancellationToken ct)
    {
        using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Url("speakers")), ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        var result = new List<VoiceStyle>();
        try
        {
            foreach (var speaker in JsonNode.Parse(json)?.AsArray() ?? new JsonArray())
            {
                string name = speaker?["name"]?.GetValue<string>() ?? "";
                foreach (var style in speaker?["styles"]?.AsArray() ?? new JsonArray())
                {
                    // 歌う用のスタイルは audio_query で使えないので除く。しゃべる用は "talk" のほか、
                    // エンジン 0.26 から "streaming_talk" もある (これから増えても使えるよう、歌う用だけを除く)。type がないのは古いエンジン
                    var type = style?["type"]?.GetValue<string>();
                    if (type != null && SingingTypes.Contains(type)) continue;
                    var id = style?["id"]?.GetValue<int>();
                    if (id == null) continue;
                    result.Add(new VoiceStyle(id.Value, name, style?["name"]?.GetValue<string>() ?? ""));
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new VoicevoxException("話者の一覧を読めませんでした。VOICEVOX のエンジンか確かめてください。", e);
        }
        return result;
    }

    /// <summary>セリフを音声 (WAV) にする。</summary>
    public async Task<byte[]> SynthesizeAsync(string text, int styleId, VoiceParams voice, CancellationToken ct)
    {
        var speaker = styleId.ToString(CultureInfo.InvariantCulture);
        string queryJson;
        using (var res = await SendAsync(new HttpRequestMessage(HttpMethod.Post, Url($"audio_query?text={Uri.EscapeDataString(text)}&speaker={speaker}")), ct))
            queryJson = await res.Content.ReadAsStringAsync(ct);

        var body = ApplyParams(queryJson, voice);
        var request = new HttpRequestMessage(HttpMethod.Post, Url($"synthesis?speaker={speaker}"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/wav"));
        using var synth = await SendAsync(request, ct);
        var wav = await synth.Content.ReadAsByteArrayAsync(ct);
        if (!WavInfo.TryGetDuration(wav, out _)) throw new VoicevoxException("エンジンから返ってきた音声を読めませんでした。");
        return wav;
    }

    /// <summary>audio_query の結果に、声の調整を書き込む。</summary>
    public static string ApplyParams(string queryJson, VoiceParams voice)
    {
        JsonObject query;
        try { query = JsonNode.Parse(queryJson)?.AsObject() ?? throw new VoicevoxException("audio_query の結果が空です。"); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { throw new VoicevoxException("audio_query の結果を読めませんでした。", e); }
        var v = voice.Clamped();
        query["speedScale"] = v.Speed;
        query["pitchScale"] = v.Pitch;
        query["intonationScale"] = v.Intonation;
        query["volumeScale"] = v.Volume;
        query["prePhonemeLength"] = v.PreSilence;
        query["postPhonemeLength"] = v.PostSilence;
        return query.ToJsonString();
    }

    private Uri Url(string relative) => new(BaseUri, relative);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await _http.SendAsync(request, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            throw new VoicevoxException($"VOICEVOX のエンジン ({BaseUri}) につながりません。VOICEVOX を起動してから、もう一度試してください。", e);
        }
        finally { request.Dispose(); }
        if (res.IsSuccessStatusCode) return res;
        string detail = "";
        try { detail = (await res.Content.ReadAsStringAsync(ct)).Trim(); } catch (HttpRequestException) { }
        if (detail.Length > 300) detail = detail[..300] + "...";
        int code = (int)res.StatusCode;
        res.Dispose();
        throw new VoicevoxException($"VOICEVOX のエンジンがエラーを返しました ({code})。" + (detail.Length > 0 ? "\n" + detail : ""));
    }
}

/// <summary>WAV の長さを、ヘッダから求める (FFmpeg を使わずに済むように)。</summary>
public static class WavInfo
{
    public static bool TryGetDuration(ReadOnlySpan<byte> wav, out double seconds)
    {
        seconds = 0;
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8)) return false;
        int byteRate = 0;
        long dataSize = -1;
        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(pos + 4, 4));
            int body = pos + 8;
            if (id.SequenceEqual("fmt "u8) && body + 12 <= wav.Length)
                byteRate = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(body + 8, 4));
            else if (id.SequenceEqual("data"u8))
            {
                // 書き出し途中のファイルなどで大きさが合わないときは、ファイルの残りを使う
                dataSize = Math.Min(size, wav.Length - body);
                break;
            }
            pos = (int)Math.Min(int.MaxValue, body + size + (size & 1));
        }
        if (byteRate <= 0 || dataSize < 0) return false;
        seconds = (double)dataSize / byteRate;
        return true;
    }
}
