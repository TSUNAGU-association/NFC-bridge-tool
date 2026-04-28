using System.Collections.Concurrent;
using System.Text;
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

    private static readonly ConcurrentDictionary<Guid, IWebSocketConnection> ReadSockets = new();
    private static readonly object DedupLock = new();
    private static string? _lastPayload;
    private static DateTime _lastTs = DateTime.MinValue;

    public static async Task Main()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        StartReadWebSocketServer();

        try
        {
            await RunNfcLoopAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        Log("[APP] shutting down");
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
                Log($"[READ] connected origin={socket.ConnectionInfo.Origin} ip={socket.ConnectionInfo.ClientIpAddress}");
            };
            socket.OnClose = () =>
            {
                ReadSockets.TryRemove(socket.ConnectionInfo.Id, out _);
                Log("[READ] disconnected");
            };
            socket.OnError = ex => Log($"[READ] error: {ex.Message}");
        });
        Log($"[READ] listening on {ReadWsAddress}");
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
                    Log("[NFC] no reader detected, retrying...");
                    await Task.Delay(ReaderRescanIntervalMs, ct);
                    continue;
                }
                Log($"[NFC] readers: {string.Join(", ", readerNames)}");

                monitor = MonitorFactory.Instance.Create(SCardScope.System);
                monitor.CardInserted += OnCardInserted;
                monitor.MonitorException += (_, ex) => Log($"[NFC] monitor exception: {ex.Message}");
                monitor.Start(readerNames);

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(ReaderRescanIntervalMs, ct);
                    var current = SafeGetReaders(ctx);
                    if (current.Length == 0 || !current.SequenceEqual(readerNames))
                    {
                        Log("[NFC] reader topology changed; restarting monitor");
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log($"[NFC] error: {ex.Message}; retrying in {ReaderRescanIntervalMs}ms");
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
                Log($"[READ] {args.ReaderName}: no NDEF Text record");
                BroadcastReadError(ErrorCodeNoNdefTextRecord);
                return;
            }
            if (result.Status == ReadTextStatus.ReadFailed)
            {
                Log($"[READ] {args.ReaderName}: read failed");
                BroadcastReadError(ErrorCodeReadFailed);
                return;
            }

            var text = result.Text;
            if (!ShouldEmit(text))
            {
                Log($"[READ] suppressed (dedupe): {Truncate(text)}");
                return;
            }
            Log($"[READ] -> {ReadSockets.Count} client(s): {Truncate(text)}");
            BroadcastRead(text);
        }
        catch (Exception ex)
        {
            Log($"[READ] inserted handler error: {ex.Message}");
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

            idx += payloadLen;
            if (messageEnd) break;
        }
        return texts;
    }

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
        Log($"[READ] -> {ReadSockets.Count} client(s): {code}");
        BroadcastRead(code);
    }

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "...";

    private static void Log(string msg) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {msg}");

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
