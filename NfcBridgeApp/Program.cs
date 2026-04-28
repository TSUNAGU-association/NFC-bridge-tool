using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Fleck;
using PCSC;
using PCSC.Monitoring;

internal static class Program
{
    private const string ReadWsAddress = "ws://127.0.0.1:8080";
    private const string WriteWsAddress = "ws://127.0.0.1:8090";
    private const int DedupWindowMs = 500;
    private const int ReaderRescanIntervalMs = 3000;
    private const int WriteTimeoutMs = 10000;
    private const int WritePollIntervalMs = 300;

    private static readonly ConcurrentDictionary<Guid, IWebSocketConnection> ReadSockets = new();
    private static readonly object DedupLock = new();
    private static string? _lastPayload;
    private static DateTime _lastTs = DateTime.MinValue;
    private static int _writeInProgress;

    public static async Task Main()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        StartReadWebSocketServer();
        StartWriteWebSocketServer();

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

    private static void StartWriteWebSocketServer()
    {
        var server = new WebSocketServer(WriteWsAddress) { RestartAfterListenError = true };
        server.Start(socket =>
        {
            socket.OnOpen = () => Log($"[WRITE] connected origin={socket.ConnectionInfo.Origin}");
            socket.OnClose = () => Log("[WRITE] disconnected");
            socket.OnError = ex => Log($"[WRITE] error: {ex.Message}");
            socket.OnMessage = msg => _ = HandleWriteMessageAsync(socket, msg);
        });
        Log($"[WRITE] listening on {WriteWsAddress}");
    }

    private static async Task HandleWriteMessageAsync(IWebSocketConnection socket, string message)
    {
        string? requestedId = null;
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type != "write")
            {
                await socket.Send(JsonError($"unknown type: {type}"));
                return;
            }
            requestedId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrEmpty(requestedId))
            {
                await socket.Send(JsonError("idが指定されていません"));
                return;
            }
            await HandleWriteRequestAsync(socket, requestedId);
        }
        catch (JsonException ex)
        {
            await socket.Send(JsonError($"不正なJSON: {ex.Message}"));
        }
        catch (Exception ex)
        {
            await socket.Send(JsonError($"内部エラー: {ex.Message}"));
        }
    }

    private static async Task HandleWriteRequestAsync(IWebSocketConnection socket, string id)
    {
        if (Interlocked.CompareExchange(ref _writeInProgress, 1, 0) != 0)
        {
            await socket.Send(JsonError("別の書き込み処理が進行中です"));
            return;
        }
        try
        {
            Log($"[WRITE] request id={id} (timeout={WriteTimeoutMs}ms)");
            var (ok, error) = await TryWriteWithTimeoutAsync(id, TimeSpan.FromMilliseconds(WriteTimeoutMs));
            if (ok)
            {
                Log($"[WRITE] OK id={id}");
                await socket.Send(JsonOk(id));
            }
            else
            {
                Log($"[WRITE] FAIL id={id}: {error}");
                await socket.Send(JsonError(error ?? "カードが検出されません"));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _writeInProgress, 0);
        }
    }

    private static async Task<(bool ok, string? error)> TryWriteWithTimeoutAsync(string id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var ctx = ContextFactory.Instance.Establish(SCardScope.System);
                var readers = ctx.GetReaders();
                if (readers != null && readers.Length > 0)
                {
                    var ndef = BuildNdefTextRecord(id);
                    var errors = new List<string>();
                    foreach (var readerName in readers)
                    {
                        try
                        {
                            using var reader = ctx.ConnectReader(readerName, SCardShareMode.Shared, SCardProtocol.Any);
                            Log($"[WRITE] trying reader: {readerName}");

                            var t4 = WriteNdefType4(reader, ndef);
                            if (t4.ok) { Log($"[WRITE] OK via Type4 on {readerName}"); return (true, null); }

                            var t2 = WriteNdefType2(reader, ndef);
                            if (t2.ok) { Log($"[WRITE] OK via Type2 on {readerName}"); return (true, null); }

                            errors.Add($"{readerName}: T4={t4.error}; T2={t2.error}");
                            Log($"[WRITE] {readerName} failed: T4={t4.error} | T2={t2.error}");
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{readerName}: no card ({ex.Message.Trim()})");
                        }
                    }
                    if (errors.Count > 0) lastError = string.Join(" || ", errors);
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
            await Task.Delay(WritePollIntervalMs);
        }
        return (false, "カードが検出されません" + (lastError is null ? "" : $" ({lastError})"));
    }

    private static (bool ok, string? error) WriteNdefType2(ICardReader reader, List<byte> ndef)
    {
        try
        {
            // Diagnostic: dump lock bytes (page 2) and CC (page 3)
            var page2 = Transmit(reader, new byte[] { 0xFF, 0xB0, 0x00, 0x02, 0x04 });
            if (IsOk(page2) && page2!.Length >= 6)
                Log($"[WRITE] lock bytes (page 2)={Convert.ToHexString(page2, 0, 4)}");
            var page3 = Transmit(reader, new byte[] { 0xFF, 0xB0, 0x00, 0x03, 0x04 });
            if (IsOk(page3) && page3!.Length >= 6)
                Log($"[WRITE] CC (page 3)={Convert.ToHexString(page3, 0, 4)}");

            var tlv = WrapNdefInTlv(ndef);

            // Try 4-byte (single-page) writes — ACS standard for NTAG/Ultralight
            var r4 = WriteType2Pages(reader, tlv, 4);
            if (r4.ok) return r4;

            // Fall back to PN532 direct NTAG WRITE (A2) — bypasses buggy FF D6 translation
            var rPn = WriteType2PN532(reader, tlv);
            if (rPn.ok) return rPn;

            return (false, $"4B: {r4.error}; PN532: {rPn.error}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool ok, string? error) WriteType2Pages(ICardReader reader, List<byte> tlv, int chunkBytes)
    {
        var data = new List<byte>(tlv);
        while (data.Count % chunkBytes != 0) data.Add(0x00);

        int page = 4;
        for (int i = 0; i < data.Count; i += chunkBytes)
        {
            var apdu = new byte[5 + chunkBytes];
            apdu[0] = 0xFF; apdu[1] = 0xD6; apdu[2] = 0x00;
            apdu[3] = (byte)page;
            apdu[4] = (byte)chunkBytes;
            for (int j = 0; j < chunkBytes; j++) apdu[5 + j] = data[i + j];
            var resp = Transmit(reader, apdu);
            if (!IsOk(resp)) return (false, $"page {page} {chunkBytes}B write failed (SW={Sw(resp)})");
            page += chunkBytes / 4;
        }

        return VerifyType2ReadBack(reader, data, $"{chunkBytes}B");
    }

    private static (bool ok, string? error) WriteType2PN532(ICardReader reader, List<byte> tlv)
    {
        var data = new List<byte>(tlv);
        while (data.Count % 4 != 0) data.Add(0x00);

        int page = 4;
        for (int i = 0; i < data.Count; i += 4)
        {
            // FF 00 00 00 Lc D4 40 Tg <NTAG cmd>
            // NTAG WRITE: A2 <page> <4 bytes>
            var apdu = new byte[]
            {
                0xFF, 0x00, 0x00, 0x00, 0x09,
                0xD4, 0x40, 0x01,
                0xA2, (byte)page,
                data[i], data[i + 1], data[i + 2], data[i + 3]
            };
            var resp = Transmit(reader, apdu);
            if (!IsOk(resp)) return (false, $"page {page} escape failed (SW={Sw(resp)})");
            // Strip trailing SW. Inner response should start with D5 41 <status>
            if (resp.Length < 5 || resp[0] != 0xD5 || resp[1] != 0x41)
                return (false, $"page {page} unexpected response: {Convert.ToHexString(resp)}");
            if (resp[2] != 0x00)
                return (false, $"page {page} PN532 status=0x{resp[2]:X2}");
            page++;
        }

        return VerifyType2ReadBack(reader, data, "PN532");
    }

    private static (bool ok, string? error) VerifyType2ReadBack(ICardReader reader, List<byte> expected, string tag)
    {
        int vp = 4;
        var actual = new List<byte>(expected.Count);
        while (actual.Count < expected.Count)
        {
            int remaining = expected.Count - actual.Count;
            int rc = Math.Min(remaining, 16);
            var readApdu = new byte[] { 0xFF, 0xB0, 0x00, (byte)vp, (byte)rc };
            var readResp = Transmit(reader, readApdu);
            if (!IsOk(readResp) || readResp!.Length < rc + 2)
                return (false, $"{tag} verify read at page {vp} failed (SW={Sw(readResp)})");
            for (int j = 0; j < rc; j++) actual.Add(readResp[j]);
            vp += rc / 4;
        }

        for (int k = 0; k < expected.Count; k++)
        {
            if (actual[k] != expected[k])
            {
                var wrote = Convert.ToHexString(expected.ToArray());
                var read = Convert.ToHexString(actual.ToArray());
                return (false, $"{tag} verify mismatch at byte {k}: wrote={wrote} read={read}");
            }
        }
        Log($"[WRITE] {tag} write+verify OK ({expected.Count} bytes)");
        return (true, null);
    }

    private static (bool ok, string? error) WriteNdefType4(ICardReader reader, List<byte> ndef)
    {
        try
        {
            // ATR for diagnostics
            var atr = TryGetAtr(reader);
            if (atr is not null) Log($"[WRITE] card ATR={Convert.ToHexString(atr)}");

            // 1) Select NDEF Application — try new AID then old (v1) AID
            byte[][] aids =
            {
                new byte[] { 0xD2, 0x76, 0x00, 0x00, 0x85, 0x01, 0x01 }, // NFC Forum NDEF Tag App v2
                new byte[] { 0xD2, 0x76, 0x00, 0x00, 0x85, 0x01, 0x00 }  // NFC Forum NDEF Tag App v1 (legacy)
            };
            byte[]? resp = null;
            string? aidError = null;
            bool selected = false;
            foreach (var aid in aids)
            {
                var apdu = new byte[6 + aid.Length];
                apdu[0] = 0x00; apdu[1] = 0xA4; apdu[2] = 0x04; apdu[3] = 0x00;
                apdu[4] = (byte)aid.Length;
                Array.Copy(aid, 0, apdu, 5, aid.Length);
                apdu[5 + aid.Length] = 0x00; // Le
                resp = Transmit(reader, apdu);
                if (IsOk(resp)) { selected = true; Log($"[WRITE] selected AID={Convert.ToHexString(aid)}"); break; }
                aidError = $"AID {Convert.ToHexString(aid)} -> SW={Sw(resp)}";
            }
            if (!selected) return (false, $"select NDEF app failed ({aidError})");

            // 2) Select Capability Container (E103)
            resp = Transmit(reader, new byte[] { 0x00, 0xA4, 0x00, 0x0C, 0x02, 0xE1, 0x03 });
            if (!IsOk(resp)) return (false, $"select CC failed (SW={Sw(resp)})");

            // 3) Read CC (15 bytes)
            resp = Transmit(reader, new byte[] { 0x00, 0xB0, 0x00, 0x00, 0x0F });
            if (!IsOk(resp) || resp!.Length < 15 + 2) return (false, $"read CC failed (SW={Sw(resp)})");
            int maxLc = ((resp[5] << 8) | resp[6]) & 0xFFFF;
            if (maxLc <= 5 || maxLc > 0xFF) maxLc = 0xFA;
            byte ndefIdHi = resp[9];
            byte ndefIdLo = resp[10];
            int maxNdefSize = ((resp[11] << 8) | resp[12]) & 0xFFFF;
            byte writeAccess = resp[14];
            if (writeAccess != 0x00) return (false, $"NDEF file is read-only (write access=0x{writeAccess:X2})");

            // 4) Select NDEF File
            resp = Transmit(reader, new byte[] { 0x00, 0xA4, 0x00, 0x0C, 0x02, ndefIdHi, ndefIdLo });
            if (!IsOk(resp)) return (false, $"select NDEF file failed (SW={Sw(resp)})");

            if (ndef.Count + 2 > maxNdefSize) return (false, $"NDEF too large ({ndef.Count} > {maxNdefSize - 2})");

            // 5) Erase NLEN (write 0x0000 to offset 0) — invalidate before rewriting
            resp = Transmit(reader, new byte[] { 0x00, 0xD6, 0x00, 0x00, 0x02, 0x00, 0x00 });
            if (!IsOk(resp)) return (false, $"erase NLEN failed (SW={Sw(resp)})");

            // 6) Write NDEF data starting at offset 2
            int chunkMax = Math.Min(maxLc - 5, 0xFA);
            if (chunkMax <= 0) chunkMax = 0xFA;
            int offset = 2;
            int written = 0;
            while (written < ndef.Count)
            {
                int chunk = Math.Min(ndef.Count - written, chunkMax);
                var apdu = new byte[5 + chunk];
                apdu[0] = 0x00; apdu[1] = 0xD6;
                apdu[2] = (byte)(offset >> 8);
                apdu[3] = (byte)(offset & 0xFF);
                apdu[4] = (byte)chunk;
                for (int i = 0; i < chunk; i++) apdu[5 + i] = ndef[written + i];
                resp = Transmit(reader, apdu);
                if (!IsOk(resp)) return (false, $"write at offset {offset} failed (SW={Sw(resp)})");
                offset += chunk;
                written += chunk;
            }

            // 7) Write NLEN at offset 0 (this commits the NDEF message)
            ushort nlen = (ushort)ndef.Count;
            resp = Transmit(reader, new byte[] { 0x00, 0xD6, 0x00, 0x00, 0x02, (byte)(nlen >> 8), (byte)(nlen & 0xFF) });
            if (!IsOk(resp)) return (false, $"write NLEN failed (SW={Sw(resp)})");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string Sw(byte[]? resp) =>
        resp is null || resp.Length < 2 ? "no-response" : $"{resp[^2]:X2}{resp[^1]:X2}";

    private static byte[]? TryGetAtr(ICardReader reader)
    {
        try
        {
            // PC/SC pseudo-APDU "Get UID" doesn't return ATR; use GetAttrib via low-level call
            // Fleck's ICardReader doesn't expose GetAttrib directly in v7. Skip if not available.
            var method = reader.GetType().GetMethod("GetAttrib", new[] { typeof(SCardAttribute) });
            if (method is null) return null;
            var result = method.Invoke(reader, new object[] { SCardAttribute.AtrString });
            return result as byte[];
        }
        catch
        {
            return null;
        }
    }

    private static List<byte> BuildNdefTextRecord(string text)
    {
        var lang = Encoding.ASCII.GetBytes("en");
        var textBytes = Encoding.UTF8.GetBytes(text);
        int payloadLen = 1 + lang.Length + textBytes.Length;

        var ndef = new List<byte>(payloadLen + 8);
        bool shortRecord = payloadLen <= 0xFF;
        byte header = (byte)(0xC0 | 0x01); // MB=1 ME=1 CF=0 IL=0 TNF=001
        if (shortRecord) header |= 0x10; // SR
        ndef.Add(header);
        ndef.Add(0x01); // Type Length
        if (shortRecord)
        {
            ndef.Add((byte)payloadLen);
        }
        else
        {
            ndef.Add((byte)((payloadLen >> 24) & 0xFF));
            ndef.Add((byte)((payloadLen >> 16) & 0xFF));
            ndef.Add((byte)((payloadLen >> 8) & 0xFF));
            ndef.Add((byte)(payloadLen & 0xFF));
        }
        ndef.Add(0x54); // Type "T"
        ndef.Add((byte)lang.Length); // status byte: UTF-8 + langLen
        ndef.AddRange(lang);
        ndef.AddRange(textBytes);
        return ndef;
    }

    private static List<byte> WrapNdefInTlv(List<byte> ndef)
    {
        var tlv = new List<byte>(ndef.Count + 5) { 0x03 };
        if (ndef.Count < 0xFF)
        {
            tlv.Add((byte)ndef.Count);
        }
        else
        {
            tlv.Add(0xFF);
            tlv.Add((byte)((ndef.Count >> 8) & 0xFF));
            tlv.Add((byte)(ndef.Count & 0xFF));
        }
        tlv.AddRange(ndef);
        tlv.Add(0xFE);
        return tlv;
    }

    private static string JsonOk(string id) =>
        JsonSerializer.Serialize(new { type = "write_result", success = true, id });

    private static string JsonError(string error) =>
        JsonSerializer.Serialize(new { type = "write_result", success = false, error });

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
        if (Volatile.Read(ref _writeInProgress) != 0)
        {
            Log("[READ] skipped (write in progress)");
            return;
        }
        try
        {
            var text = ReadNdefText(args.ReaderName);
            if (string.IsNullOrEmpty(text))
            {
                Log($"[READ] {args.ReaderName}: no NDEF Text record");
                return;
            }
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
        }
    }

    private static string ReadNdefText(string readerName)
    {
        using var ctx = ContextFactory.Instance.Establish(SCardScope.System);
        using var reader = ctx.ConnectReader(readerName, SCardShareMode.Shared, SCardProtocol.Any);

        var ndef = TryReadType4(reader) ?? TryReadType2(reader);
        if (ndef is null || ndef.Length == 0) return string.Empty;

        var texts = ExtractTextRecords(ndef);
        return texts.Count == 0 ? string.Empty : string.Join("\n", texts);
    }

    private static byte[]? TryReadType4(ICardReader reader)
    {
        try
        {
            var resp = Transmit(reader, new byte[] { 0x00, 0xA4, 0x04, 0x00, 0x07, 0xD2, 0x76, 0x00, 0x00, 0x85, 0x01, 0x01, 0x00 });
            if (!IsOk(resp)) return null;

            resp = Transmit(reader, new byte[] { 0x00, 0xA4, 0x00, 0x0C, 0x02, 0xE1, 0x03 });
            if (!IsOk(resp)) return null;

            resp = Transmit(reader, new byte[] { 0x00, 0xB0, 0x00, 0x00, 0x0F });
            if (!IsOk(resp) || resp!.Length < 15 + 2) return null;
            int maxLe = ((resp[3] << 8) | resp[4]) & 0xFFFF;
            if (maxLe <= 0 || maxLe > 0xF6) maxLe = 0xF6;
            byte ndefIdHi = resp[9];
            byte ndefIdLo = resp[10];

            resp = Transmit(reader, new byte[] { 0x00, 0xA4, 0x00, 0x0C, 0x02, ndefIdHi, ndefIdLo });
            if (!IsOk(resp)) return null;

            resp = Transmit(reader, new byte[] { 0x00, 0xB0, 0x00, 0x00, 0x02 });
            if (!IsOk(resp) || resp!.Length < 4) return null;
            int ndefLen = (resp[0] << 8) | resp[1];
            if (ndefLen <= 0 || ndefLen > 0x7FFF) return null;

            var data = new List<byte>(ndefLen);
            int offset = 2;
            while (data.Count < ndefLen)
            {
                int remaining = ndefLen - data.Count;
                int chunk = Math.Min(remaining, maxLe);
                resp = Transmit(reader, new byte[] { 0x00, 0xB0, (byte)(offset >> 8), (byte)(offset & 0xFF), (byte)chunk });
                if (!IsOk(resp) || resp!.Length < chunk + 2) return null;
                for (int i = 0; i < chunk; i++) data.Add(resp[i]);
                offset += chunk;
            }
            return data.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryReadType2(ICardReader reader)
    {
        try
        {
            var cc = Transmit(reader, new byte[] { 0xFF, 0xB0, 0x00, 0x03, 0x04 });
            if (!IsOk(cc) || cc!.Length < 4 + 2 || cc[0] != 0xE1) return null;
            int dataSize = cc[2] * 8;
            if (dataSize <= 0 || dataSize > 8192) return null;

            var data = new List<byte>(dataSize);
            int page = 4;
            while (data.Count < dataSize)
            {
                int remaining = dataSize - data.Count;
                int chunk = Math.Min(remaining, 16);
                var resp = Transmit(reader, new byte[] { 0xFF, 0xB0, 0x00, (byte)page, (byte)chunk });
                if (!IsOk(resp) || resp!.Length < chunk + 2) break;
                for (int i = 0; i < chunk; i++) data.Add(resp[i]);
                page += chunk / 4;
            }

            int idx = 0;
            while (idx < data.Count)
            {
                byte tag = data[idx];
                if (tag == 0x00) { idx++; continue; }
                if (tag == 0xFE) return null;
                if (idx + 1 >= data.Count) return null;
                int len, hdr;
                if (data[idx + 1] == 0xFF)
                {
                    if (idx + 3 >= data.Count) return null;
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
                    if (idx + hdr + len > data.Count) return null;
                    var ndef = new byte[len];
                    data.CopyTo(idx + hdr, ndef, 0, len);
                    return ndef;
                }
                idx += hdr + len;
            }
            return null;
        }
        catch
        {
            return null;
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

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "...";

    private static void Log(string msg) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {msg}");
}
