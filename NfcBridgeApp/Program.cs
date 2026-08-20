using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Fleck;
using PCSC;
using PCSC.Monitoring;

internal static class Program
{
    private const string ReadWsAddress = "ws://127.0.0.1:8080";
    private const int DedupWindowMs = 500;
    private const int ReaderRescanIntervalMs = 3000;
    private const string ErrorCodeNoNdefTextRecord = "ERR_NO_NDEF_TEXT_RECORD";
    private const string ErrorCodeReadFailed = "ERR_NFC_READ_FAILED";

    private const string GitHubLatestReleaseUrl =
        "https://api.github.com/repos/TSUNAGU-association/NFC-bridge-tool/releases/latest";
    private const string UpdateUserAgent = "NfcBridgeApp-AutoUpdater";
    private const string SkipUpdateEnvVar = "NFC_BRIDGE_SKIP_UPDATE";
    private const string ReleaseAssetPrefix = "NfcBridgeApp-win-x64-";

    private const string DefaultScannerUrl = "https://admin.tl.tsunagu-sep.org/leader/scanner?location_id=1";
    private const string ScannerUrlEnvVar = "NFC_BRIDGE_SCANNER_URL";

    // location_idは端末の設置場所ごとに異なるためenvで上書き可能にする
    private static readonly string ScannerUrl =
        Environment.GetEnvironmentVariable(ScannerUrlEnvVar) is { Length: > 0 } overridden
            ? overridden
            : DefaultScannerUrl;

    private static readonly ConcurrentDictionary<Guid, IWebSocketConnection> ReadSockets = new();
    private static readonly object DedupLock = new();
    private static string? _lastPayload;
    private static DateTime _lastTs = DateTime.MinValue;

    public static async Task Main()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        Log($"[APP] NfcBridgeApp v{GetCurrentVersion()}");
        await CheckAndApplyUpdateAsync();

        StartReadWebSocketServer();
        var autoLoginEnabled = AdminAutoLoginService.TryCreate(Log, out var autoLoginService);
        if (!autoLoginEnabled)
        {
            OpenScannerUrl();
        }

        try
        {
            var nfcTask = RunNfcLoopAsync(cts.Token);
            var autoLoginTask = autoLoginService?.RunAsync(cts.Token) ?? Task.CompletedTask;
            await Task.WhenAll(nfcTask, autoLoginTask);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            autoLoginService?.Dispose();
        }

        Log("[APP] 終了処理を開始します");
    }

    private static void StartReadWebSocketServer()
    {
        FleckLog.Level = LogLevel.Warn;
        var server = new WebSocketServer(ReadWsAddress) { RestartAfterListenError = true };
        server.Start(socket =>
        {
            socket.OnOpen = () =>
            {
                ReadSockets[socket.ConnectionInfo.Id] = socket;
                Log($"[READ] クライアントが接続しました origin={socket.ConnectionInfo.Origin} ip={socket.ConnectionInfo.ClientIpAddress}");
            };
            socket.OnClose = () =>
            {
                ReadSockets.TryRemove(socket.ConnectionInfo.Id, out _);
                Log("[READ] クライアントが切断しました");
            };
            socket.OnError = ex => Log($"[READ] エラーが発生しました: {ex.Message}");
        });
        Log($"[READ] {ReadWsAddress} で待ち受けを開始しました");
    }

    private static async Task RunNfcLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ISCardMonitor? monitor = null;
            ISCardContext? ctx = null;
            try
            {
                ctx = ContextFactory.Instance.Establish(SCardScope.System);
                var readerNames = SafeGetReaders(ctx);
                if (readerNames.Length == 0)
                {
                    Log("[NFC] リーダーが検出されません。再試行します...");
                    await Task.Delay(ReaderRescanIntervalMs, ct);
                    continue;
                }
                Log($"[NFC] 検出されたリーダー: {string.Join(", ", readerNames)}");

                monitor = MonitorFactory.Instance.Create(SCardScope.System);
                monitor.CardInserted += OnCardInserted;
                monitor.MonitorException += (_, ex) => Log($"[NFC] 監視中に例外が発生しました: {ex.Message}");
                monitor.Start(readerNames);

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(ReaderRescanIntervalMs, ct);
                    var current = SafeGetReaders(ctx);
                    if (current.Length == 0 || !current.SequenceEqual(readerNames))
                    {
                        Log("[NFC] リーダー構成が変化しました。監視を再起動します");
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log($"[NFC] エラーが発生しました: {ex.Message}; {ReaderRescanIntervalMs}ms 後に再試行します");
                try { await Task.Delay(ReaderRescanIntervalMs, ct); } catch (OperationCanceledException) { throw; }
            }
            finally
            {
                if (monitor is not null)
                {
                    try { monitor.Cancel(); } catch { }
                    monitor.CardInserted -= OnCardInserted;
                    monitor.Dispose();
                }
                ctx?.Dispose();
            }
        }
    }

    private static string[] SafeGetReaders(ISCardContext ctx)
    {
        try { return ctx.GetReaders() ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static void OnCardInserted(object? sender, CardStatusEventArgs args)
    {
        try
        {
            var result = ReadNdefText(args.ReaderName);
            if (result.Status == ReadTextStatus.NoTextRecord)
            {
                Log($"[READ] {args.ReaderName}: NDEFテキストレコードが見つかりません");
                BroadcastReadError(ErrorCodeNoNdefTextRecord);
                return;
            }
            if (result.Status == ReadTextStatus.ReadFailed)
            {
                Log($"[READ] {args.ReaderName}: 読み取りに失敗しました");
                BroadcastReadError(ErrorCodeReadFailed);
                return;
            }

            var text = result.Text;
            if (!ShouldEmit(text))
            {
                Log($"[READ] 重複のため抑制しました: {Truncate(text)}");
                return;
            }
            Log($"[READ] -> {ReadSockets.Count} 件のクライアントへ送信: {Truncate(text)}");
            BroadcastRead(text);
        }
        catch (Exception ex)
        {
            Log($"[READ] カード読み取り処理でエラーが発生しました: {ex.Message}");
            BroadcastReadError(ErrorCodeReadFailed);
        }
    }

    private static ReadTextResult ReadNdefText(string readerName)
    {
        using var ctx = ContextFactory.Instance.Establish(SCardScope.System);
        using var reader = ctx.ConnectReader(readerName, SCardShareMode.Shared, SCardProtocol.Any);

        var type4 = TryReadType4(reader);
        var type2 = type4.Status == NdefReadStatus.Success ? default : TryReadType2(reader);
        var ndef = type4.Status == NdefReadStatus.Success ? type4.Data : type2.Data;
        if (ndef is null || ndef.Length == 0)
        {
            if (type4.Status == NdefReadStatus.ReadFailed || type2.Status == NdefReadStatus.ReadFailed)
            {
                return new ReadTextResult(ReadTextStatus.ReadFailed, string.Empty);
            }

            return new ReadTextResult(ReadTextStatus.NoTextRecord, string.Empty);
        }

        var texts = ExtractTextRecords(ndef);
        return texts.Count == 0
            ? new ReadTextResult(ReadTextStatus.NoTextRecord, string.Empty)
            : new ReadTextResult(ReadTextStatus.Success, string.Join("\n", texts));
    }

    private static NdefReadResult TryReadType4(ICardReader reader)
    {
        try
        {
            var resp = Transmit(reader, new byte[] { 0x00, 0xA4, 0x04, 0x00, 0x07, 0xD2, 0x76, 0x00, 0x00, 0x85, 0x01, 0x01, 0x00 });
            if (resp is null) return new NdefReadResult(NdefReadStatus.ReadFailed, null);
            if (!IsOk(resp)) return new NdefReadResult(NdefReadStatus.NotFound, null);

            resp = Transmit(reader, new byte[] { 0x00, 0xA4, 0x00, 0x0C, 0x02, 0xE1, 0x03 });
            if (!IsOk(resp)) return new NdefReadResult(NdefReadStatus.ReadFailed, null);

            resp = Transmit(reader, new byte[] { 0x00, 0xB0, 0x00, 0x00, 0x0F });
            if (!IsOk(resp) || resp!.Length < 15 + 2) return new NdefReadResult(NdefReadStatus.ReadFailed, null);
            int maxLe = ((resp[3] << 8) | resp[4]) & 0xFFFF;
            if (maxLe <= 0 || maxLe > 0xF6) maxLe = 0xF6;
            byte ndefIdHi = resp[9];
            byte ndefIdLo = resp[10];

            resp = Transmit(reader, new byte[] { 0x00, 0xA4, 0x00, 0x0C, 0x02, ndefIdHi, ndefIdLo });
            if (!IsOk(resp)) return new NdefReadResult(NdefReadStatus.ReadFailed, null);

            resp = Transmit(reader, new byte[] { 0x00, 0xB0, 0x00, 0x00, 0x02 });
            if (!IsOk(resp) || resp!.Length < 4) return new NdefReadResult(NdefReadStatus.ReadFailed, null);
            int ndefLen = (resp[0] << 8) | resp[1];
            if (ndefLen <= 0) return new NdefReadResult(NdefReadStatus.NotFound, null);
            if (ndefLen > 0x7FFF) return new NdefReadResult(NdefReadStatus.ReadFailed, null);

            var data = new List<byte>(ndefLen);
            int offset = 2;
            while (data.Count < ndefLen)
            {
                int remaining = ndefLen - data.Count;
                int chunk = Math.Min(remaining, maxLe);
                resp = Transmit(reader, new byte[] { 0x00, 0xB0, (byte)(offset >> 8), (byte)(offset & 0xFF), (byte)chunk });
                if (!IsOk(resp) || resp!.Length < chunk + 2) return new NdefReadResult(NdefReadStatus.ReadFailed, null);
                for (int i = 0; i < chunk; i++) data.Add(resp[i]);
                offset += chunk;
            }
            return new NdefReadResult(NdefReadStatus.Success, data.ToArray());
        }
        catch
        {
            return new NdefReadResult(NdefReadStatus.ReadFailed, null);
        }
    }

    private static NdefReadResult TryReadType2(ICardReader reader)
    {
        try
        {
            var cc = Transmit(reader, new byte[] { 0xFF, 0xB0, 0x00, 0x03, 0x04 });
            if (cc is null) return new NdefReadResult(NdefReadStatus.ReadFailed, null);
            if (!IsOk(cc)) return new NdefReadResult(NdefReadStatus.NotFound, null);
            if (cc.Length < 4 + 2) return new NdefReadResult(NdefReadStatus.ReadFailed, null);
            if (cc[0] != 0xE1) return new NdefReadResult(NdefReadStatus.NotFound, null);
            int dataSize = cc[2] * 8;
            if (dataSize <= 0) return new NdefReadResult(NdefReadStatus.NotFound, null);
            if (dataSize > 8192) return new NdefReadResult(NdefReadStatus.ReadFailed, null);

            var data = new List<byte>(dataSize);
            int page = 4;
            while (data.Count < dataSize)
            {
                int remaining = dataSize - data.Count;
                int chunk = Math.Min(remaining, 16);
                var resp = Transmit(reader, new byte[] { 0xFF, 0xB0, 0x00, (byte)page, (byte)chunk });
                if (!IsOk(resp) || resp!.Length < chunk + 2) return new NdefReadResult(NdefReadStatus.ReadFailed, null);
                for (int i = 0; i < chunk; i++) data.Add(resp[i]);
                page += chunk / 4;
            }

            int idx = 0;
            while (idx < data.Count)
            {
                byte tag = data[idx];
                if (tag == 0x00) { idx++; continue; }
                if (tag == 0xFE) return new NdefReadResult(NdefReadStatus.NotFound, null);
                if (idx + 1 >= data.Count) return new NdefReadResult(NdefReadStatus.ReadFailed, null);
                int len, hdr;
                if (data[idx + 1] == 0xFF)
                {
                    if (idx + 3 >= data.Count) return new NdefReadResult(NdefReadStatus.ReadFailed, null);
                    len = (data[idx + 2] << 8) | data[idx + 3];
                    hdr = 4;
                }
                else
                {
                    len = data[idx + 1];
                    hdr = 2;
                }
                if (tag == 0x03)
                {
                    if (idx + hdr + len > data.Count) return new NdefReadResult(NdefReadStatus.ReadFailed, null);
                    var ndef = new byte[len];
                    data.CopyTo(idx + hdr, ndef, 0, len);
                    return new NdefReadResult(NdefReadStatus.Success, ndef);
                }
                idx += hdr + len;
            }
            return new NdefReadResult(NdefReadStatus.NotFound, null);
        }
        catch
        {
            return new NdefReadResult(NdefReadStatus.ReadFailed, null);
        }
    }

    private static List<string> ExtractTextRecords(byte[] ndef)
    {
        var texts = new List<string>();
        int idx = 0;
        while (idx < ndef.Length)
        {
            byte hdr = ndef[idx++];
            bool messageEnd = (hdr & 0x40) != 0;
            bool shortRecord = (hdr & 0x10) != 0;
            bool hasIdLen = (hdr & 0x08) != 0;
            int tnf = hdr & 0x07;

            if (idx >= ndef.Length) break;
            int typeLen = ndef[idx++];

            int payloadLen;
            if (shortRecord)
            {
                if (idx >= ndef.Length) break;
                payloadLen = ndef[idx++];
            }
            else
            {
                if (idx + 4 > ndef.Length) break;
                payloadLen = (ndef[idx] << 24) | (ndef[idx + 1] << 16) | (ndef[idx + 2] << 8) | ndef[idx + 3];
                idx += 4;
            }

            int idLen = 0;
            if (hasIdLen)
            {
                if (idx >= ndef.Length) break;
                idLen = ndef[idx++];
            }

            if (idx + typeLen > ndef.Length) break;
            var type = new byte[typeLen];
            Array.Copy(ndef, idx, type, 0, typeLen);
            idx += typeLen + idLen;

            if (payloadLen < 0 || idx + payloadLen > ndef.Length) break;

            if (tnf == 0x01 && typeLen == 1 && type[0] == 0x54 && payloadLen >= 1)
            {
                byte status = ndef[idx];
                int langLen = status & 0x3F;
                bool utf16 = (status & 0x80) != 0;
                int textOffset = idx + 1 + langLen;
                int textLen = payloadLen - 1 - langLen;
                if (textLen > 0 && textOffset + textLen <= ndef.Length)
                {
                    var encoding = utf16 ? Encoding.Unicode : Encoding.UTF8;
                    texts.Add(encoding.GetString(ndef, textOffset, textLen));
                }
            }
            else if (tnf == 0x01 && typeLen == 1 && type[0] == 0x55 && payloadLen >= 1)
            {
                byte prefixCode = ndef[idx];
                string prefix = prefixCode < UriPrefixes.Length ? UriPrefixes[prefixCode] : string.Empty;
                int uriOffset = idx + 1;
                int uriLen = payloadLen - 1;
                string suffix = uriLen > 0 ? Encoding.UTF8.GetString(ndef, uriOffset, uriLen) : string.Empty;
                var uri = prefix + suffix;
                if (uri.Length > 0) texts.Add(uri);
            }

            idx += payloadLen;
            if (messageEnd) break;
        }
        return texts;
    }

    private static readonly string[] UriPrefixes =
    {
        "",
        "http://www.",
        "https://www.",
        "http://",
        "https://",
        "tel:",
        "mailto:",
        "ftp://anonymous:anonymous@",
        "ftp://ftp.",
        "ftps://",
        "sftp://",
        "smb://",
        "nfs://",
        "ftp://",
        "dav://",
        "news:",
        "telnet://",
        "imap:",
        "rtsp://",
        "urn:",
        "pop:",
        "sip:",
        "sips:",
        "tftp:",
        "btspp://",
        "btl2cap://",
        "btgoep://",
        "tcpobex://",
        "irdaobex://",
        "file://",
        "urn:epc:id:",
        "urn:epc:tag:",
        "urn:epc:pat:",
        "urn:epc:raw:",
        "urn:epc:",
        "urn:nfc:",
    };

    private static byte[]? Transmit(ICardReader reader, byte[] apdu)
    {
        var rx = new byte[258];
        int received = reader.Transmit(apdu, rx);
        if (received < 2) return null;
        var result = new byte[received];
        Array.Copy(rx, result, received);
        return result;
    }

    private static bool IsOk(byte[]? resp) =>
        resp is not null && resp.Length >= 2 && resp[^2] == 0x90 && resp[^1] == 0x00;

    private static bool ShouldEmit(string payload)
    {
        lock (DedupLock)
        {
            var now = DateTime.UtcNow;
            if (payload == _lastPayload && (now - _lastTs).TotalMilliseconds < DedupWindowMs) return false;
            _lastPayload = payload;
            _lastTs = now;
            return true;
        }
    }

    private static void BroadcastRead(string payload)
    {
        foreach (var s in ReadSockets.Values)
        {
            try { s.Send(payload); } catch { }
        }
    }

    private static void BroadcastReadError(string code)
    {
        Log($"[READ] -> {ReadSockets.Count} 件のクライアントへ送信: {code}");
        BroadcastRead(code);
    }

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "...";

    private static void Log(string msg) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {msg}");

    private static void OpenScannerUrl()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ScannerUrl,
                    UseShellExecute = true,
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", ScannerUrl);
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", ScannerUrl);
            }
            else
            {
                Log($"[APP] このOSではブラウザを自動で開けません: {ScannerUrl}");
                return;
            }
            Log($"[APP] MID.スキャナーを開きました: {ScannerUrl}");
        }
        catch (Exception ex)
        {
            Log($"[APP] MID.スキャナーの起動に失敗しました: {ex.Message}");
        }
    }

    private static string GetCurrentVersion()
    {
        var asm = typeof(Program).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static async Task CheckAndApplyUpdateAsync()
    {
        if (Environment.GetEnvironmentVariable(SkipUpdateEnvVar) == "1")
        {
            Log("[UPDATE] NFC_BRIDGE_SKIP_UPDATE=1 が設定されているため更新をスキップしました");
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            Log("[UPDATE] Windows以外の環境のため自動更新は無効です");
            return;
        }

        try
        {
            Log("[UPDATE] 新しいリリースを確認しています...");
            var release = await FetchLatestReleaseAsync();
            if (release is null)
            {
                Log("[UPDATE] 対応するリリースアセットが見つかりません");
                return;
            }

            var current = GetCurrentVersion();
            if (!IsNewer(release.Tag, current))
            {
                Log($"[UPDATE] 最新版です (現在={current}, 最新={release.Tag})");
                return;
            }

            Log($"[UPDATE] 新しいバージョンが利用可能です: {release.Tag} (現在={current}); 適用しています...");
            await StageAndLaunchUpdateAsync(release);
            Log("[UPDATE] 更新の準備が完了しました。再起動します...");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log($"[UPDATE] 更新に失敗しました: {ex.Message}; 現在のバージョンのまま続行します");
        }
    }

    private static async Task<ReleaseInfo?> FetchLatestReleaseAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UpdateUserAgent);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await http.GetAsync(GitHubLatestReleaseUrl);
        if (!resp.IsSuccessStatusCode)
        {
            Log($"[UPDATE] リリース情報の取得に失敗 HTTP {(int)resp.StatusCode}");
            return null;
        }

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag)) return null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name is null || url is null) continue;
            if (name.StartsWith(ReleaseAssetPrefix, StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return new ReleaseInfo(tag, url);
            }
        }
        return null;
    }

    private static bool IsNewer(string latestTag, string current)
    {
        var latestNum = NormalizeVersion(latestTag);
        var currentNum = NormalizeVersion(current);
        if (!Version.TryParse(latestNum, out var lv)) return false;
        if (!Version.TryParse(currentNum, out var cv)) return false;
        return lv > cv;
    }

    private static string NormalizeVersion(string s)
    {
        s = s.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        return s;
    }

    private static async Task StageAndLaunchUpdateAsync(ReleaseInfo release)
    {
        var installDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var staging = Path.Combine(Path.GetTempPath(), $"NfcBridgeApp-update-{Guid.NewGuid():N}");
        var extractDir = Path.Combine(staging, "extracted");
        var zipPath = Path.Combine(staging, "release.zip");
        Directory.CreateDirectory(staging);

        Log($"[UPDATE] ダウンロード中 {release.DownloadUrl}");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UpdateUserAgent);
            await using var fs = File.Create(zipPath);
            await using var stream = await http.GetStreamAsync(release.DownloadUrl);
            await stream.CopyToAsync(fs);
        }

        Log($"[UPDATE] {extractDir} に展開しています");
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        var exePath = Path.Combine(installDir, "NfcBridgeApp.exe");
        var batPath = Path.Combine(staging, "apply-update.bat");
        var pid = Environment.ProcessId;

        var bat = $@"@echo off
setlocal
:wait
tasklist /FI ""PID eq {pid}"" 2>NUL | find ""{pid}"" >NUL
if not errorlevel 1 (
    timeout /t 1 /nobreak >NUL
    goto wait
)
robocopy ""{extractDir}"" ""{installDir}"" /E /NFL /NDL /NJH /NJS /NP /R:3 /W:1 >NUL
if %ERRORLEVEL% GEQ 8 (
    exit /b %ERRORLEVEL%
)
start """" ""{exePath}""
rmdir /S /Q ""{staging}""
endlocal
";
        await File.WriteAllTextAsync(batPath, bat, Encoding.ASCII);

        Process.Start(new ProcessStartInfo
        {
            FileName = batPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private sealed record ReleaseInfo(string Tag, string DownloadUrl);

    private enum ReadTextStatus
    {
        Success,
        NoTextRecord,
        ReadFailed
    }

    private readonly record struct ReadTextResult(ReadTextStatus Status, string Text);

    private enum NdefReadStatus
    {
        Success,
        NotFound,
        ReadFailed
    }

    private readonly record struct NdefReadResult(NdefReadStatus Status, byte[]? Data);
}
