using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TestDbHub.Core.Sessions;

/// <summary>
/// Minimal PostgreSQL client using the simple query protocol only.
/// HeliosDB-Nano accepts its branch statements (CREATE/USE/MERGE/DROP BRANCH)
/// on the simple protocol but not on the extended protocol that drivers such
/// as Npgsql always use, so we speak the wire protocol directly. Supports
/// trust, cleartext, MD5 and SCRAM-SHA-256 authentication, without TLS.
/// </summary>
internal sealed class PgSimpleSession : ISqlSession
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly TcpClient _tcp;
    private NetworkStream _stream = null!;
    private readonly byte[] _header = new byte[5];

    public string? ServerVersion { get; private set; }

    /// <summary>Statement run (best effort) before the session closes.</summary>
    public string? OnCloseSql { get; set; }

    private PgSimpleSession(TcpClient tcp)
    {
        _tcp = tcp;
    }

    public static async Task<PgSimpleSession> ConnectAsync(string host, int port, string user, string password, string database,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        var session = new PgSimpleSession(tcp);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await tcp.ConnectAsync(host, port, cts.Token);
                session._stream = tcp.GetStream();
                await session.StartupAsync(user, password, database, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TestDbException($"Hết thời gian kết nối tới {host}:{port}");
            }
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private async Task StartupAsync(string user, string password, string database, CancellationToken ct)
    {
        var body = new MemoryStream();
        WriteInt32(body, 196608); // protocol 3.0
        foreach (var (k, v) in new[] { ("user", user), ("database", database), ("client_encoding", "UTF8"), ("application_name", "TestDbHub") })
        {
            WriteCString(body, k);
            WriteCString(body, v);
        }
        body.WriteByte(0);
        await SendAsync(null, body.ToArray(), ct);

        ScramState? scram = null;
        while (true)
        {
            var (type, payload) = await ReadMessageAsync(ct);
            switch (type)
            {
                case 'R':
                    scram = await HandleAuthAsync(payload, user, password, scram, ct);
                    break;
                case 'S':
                    var parts = SplitCStrings(payload);
                    if (parts.Count >= 2 && parts[0] == "server_version") ServerVersion = parts[1];
                    break;
                case 'E':
                    throw new TestDbException(ParseError(payload));
                case 'Z':
                    return;
            }
        }
    }

    private async Task<ScramState?> HandleAuthAsync(byte[] payload, string user, string password, ScramState? scram, CancellationToken ct)
    {
        int code = BinaryPrimitives.ReadInt32BigEndian(payload);
        switch (code)
        {
            case 0: // AuthenticationOk
                return scram;
            case 3: // cleartext
                await SendAsync('p', CString(password), ct);
                return scram;
            case 5: // MD5
            {
                var salt = payload.AsSpan(4, 4).ToArray();
                string inner = Md5Hex(Utf8.GetBytes(password + user));
                string outer = "md5" + Md5Hex([.. Utf8.GetBytes(inner), .. salt]);
                await SendAsync('p', CString(outer), ct);
                return scram;
            }
            case 10: // SASL: pick SCRAM-SHA-256
            {
                var mechanisms = SplitCStrings(payload[4..]);
                if (!mechanisms.Contains("SCRAM-SHA-256"))
                    throw new TestDbException("Máy chủ yêu cầu kiểu xác thực SASL không được hỗ trợ");
                var state = new ScramState(password);
                var first = Utf8.GetBytes(state.ClientFirst);
                var msg = new MemoryStream();
                WriteCString(msg, "SCRAM-SHA-256");
                WriteInt32(msg, first.Length);
                msg.Write(first);
                await SendAsync('p', msg.ToArray(), ct);
                return state;
            }
            case 11: // SASLContinue
            {
                if (scram is null) throw new TestDbException("Phản hồi SCRAM không hợp lệ");
                var final = scram.ClientFinal(Utf8.GetString(payload, 4, payload.Length - 4));
                await SendAsync('p', Utf8.GetBytes(final), ct);
                return scram;
            }
            case 12: // SASLFinal
            {
                if (scram is null || !scram.VerifyServerFinal(Utf8.GetString(payload, 4, payload.Length - 4)))
                    throw new TestDbException("Không xác minh được máy chủ (SCRAM)");
                return scram;
            }
            default:
                throw new TestDbException($"Kiểu xác thực PostgreSQL {code} không được hỗ trợ");
        }
    }

    public async Task<RawResult> RunAsync(string sql, int maxRows = int.MaxValue, CancellationToken ct = default)
    {
        await using var reg = ct.Register(() => _tcp.Close());
        try
        {
            await SendAsync('Q', CString(sql), CancellationToken.None);
            List<PgField>? fields = null;
            var rows = new List<object?[]>();
            long count = 0, affected = 0;
            string? error = null;
            while (true)
            {
                var (type, payload) = await ReadMessageAsync(CancellationToken.None);
                switch (type)
                {
                    case 'T':
                        fields = ParseRowDescription(payload);
                        rows.Clear();
                        count = 0;
                        break;
                    case 'D':
                        if (count++ < maxRows && fields is not null) rows.Add(ParseDataRow(payload, fields));
                        break;
                    case 'C':
                        affected = ParseAffected(Utf8.GetString(payload).TrimEnd('\0'));
                        break;
                    case 'E':
                        error ??= ParseError(payload);
                        break;
                    case 'Z':
                        if (error is not null) throw new SqlExecutionException(error);
                        var columns = fields?.Select(f => new PortableColumn(f.Name, f.Type, f.Precision, f.Scale)).ToList();
                        return new RawResult(columns, rows, count, columns is null ? affected : 0);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            ct.ThrowIfCancellationRequested();
            throw new TestDbException($"Mất kết nối tới HeliosDB: {ex.Message}");
        }
    }

    private sealed record PgField(string Name, int TypeOid, PortableType Type, int? Precision, int? Scale);

    private static List<PgField> ParseRowDescription(byte[] p)
    {
        int count = BinaryPrimitives.ReadInt16BigEndian(p);
        int pos = 2;
        var fields = new List<PgField>(count);
        for (int i = 0; i < count; i++)
        {
            string name = ReadCString(p, ref pos);
            pos += 4 + 2; // table oid, column attnum
            int oid = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(pos));
            pos += 4 + 2; // type oid, type size
            int typmod = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(pos));
            pos += 4 + 2; // typmod, format
            int? precision = null, scale = null;
            if (oid == 1700 && typmod >= 4)
            {
                precision = ((typmod - 4) >> 16) & 0xFFFF;
                scale = (typmod - 4) & 0xFFFF;
            }
            fields.Add(new PgField(name, oid, MapOid(oid), precision, scale));
        }
        return fields;
    }

    internal static PortableType MapOid(int oid) => oid switch
    {
        16 => PortableType.Boolean,
        21 or 23 => PortableType.Int,
        20 or 26 => PortableType.BigInt,
        700 or 701 => PortableType.Double,
        1700 => PortableType.Decimal,
        1082 => PortableType.Date,
        1114 or 1184 => PortableType.DateTime,
        1083 or 1266 => PortableType.Time,
        114 or 3802 => PortableType.Json,
        17 => PortableType.Binary,
        2950 => PortableType.Uuid,
        _ => PortableType.Text,
    };

    private static object?[] ParseDataRow(byte[] p, List<PgField> fields)
    {
        int count = BinaryPrimitives.ReadInt16BigEndian(p);
        int pos = 2;
        var row = new object?[fields.Count];
        for (int i = 0; i < count; i++)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(pos));
            pos += 4;
            if (len < 0) continue;
            string text = Utf8.GetString(p, pos, len);
            pos += len;
            if (i < row.Length) row[i] = ConvertText(text, fields[i].TypeOid);
        }
        return row;
    }

    /// <summary>Turn a text-format value into a CLR value; dates and times stay strings.</summary>
    internal static object ConvertText(string text, int oid)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (oid)
        {
            case 16:
                return text is "t" or "true" or "TRUE" or "1";
            case 21:
            case 23:
                return int.TryParse(text, NumberStyles.Integer, inv, out var i) ? i : text;
            case 20:
            case 26:
                return long.TryParse(text, NumberStyles.Integer, inv, out var l) ? l : text;
            case 700:
            case 701:
                return double.TryParse(text, NumberStyles.Float, inv, out var d) ? d : text;
            case 1700:
                return decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowExponent, inv, out var m) ? m : text;
            case 17:
                if (text.StartsWith(@"\x", StringComparison.Ordinal) || text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    try { return Convert.FromHexString(text.AsSpan(2)); } catch (FormatException) { }
                }
                return text;
            default:
                return text;
        }
    }

    internal static long ParseAffected(string tag)
    {
        // e.g. "INSERT 0 3", "UPDATE 2", "DELETE 1", "OK 0"
        int space = tag.LastIndexOf(' ');
        return space >= 0 && long.TryParse(tag.AsSpan(space + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private static string ParseError(byte[] p)
    {
        string? message = null, code = null, detail = null;
        int pos = 0;
        while (pos < p.Length && p[pos] != 0)
        {
            char field = (char)p[pos++];
            string value = ReadCString(p, ref pos);
            switch (field)
            {
                case 'M': message = value; break;
                case 'C': code = value; break;
                case 'D': detail = value; break;
            }
        }
        var text = message ?? "Lỗi không xác định";
        if (detail is not null) text += $" ({detail})";
        return code is null ? text : $"{code}: {text}";
    }

    private async Task<(char Type, byte[] Payload)> ReadMessageAsync(CancellationToken ct)
    {
        await _stream.ReadExactlyAsync(_header, ct);
        int len = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(1)) - 4;
        if (len < 0 || len > 256 * 1024 * 1024) throw new IOException("Gói tin PostgreSQL không hợp lệ");
        var payload = new byte[len];
        await _stream.ReadExactlyAsync(payload, ct);
        return ((char)_header[0], payload);
    }

    private async Task SendAsync(char? type, byte[] body, CancellationToken ct)
    {
        var msg = new byte[(type is null ? 0 : 1) + 4 + body.Length];
        int pos = 0;
        if (type is char t) msg[pos++] = (byte)t;
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(pos), body.Length + 4);
        body.CopyTo(msg, pos + 4);
        await _stream.WriteAsync(msg, ct);
    }

    private static byte[] CString(string s) => [.. Utf8.GetBytes(s), 0];

    private static void WriteCString(Stream s, string value)
    {
        s.Write(Utf8.GetBytes(value));
        s.WriteByte(0);
    }

    private static void WriteInt32(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        s.Write(b);
    }

    private static string ReadCString(byte[] p, ref int pos)
    {
        int end = Array.IndexOf(p, (byte)0, pos);
        if (end < 0) end = p.Length;
        var s = Utf8.GetString(p, pos, end - pos);
        pos = end + 1;
        return s;
    }

    private static List<string> SplitCStrings(byte[] p)
    {
        var list = new List<string>();
        int pos = 0;
        while (pos < p.Length && p[pos] != 0) list.Add(ReadCString(p, ref pos));
        return list;
    }

    private static string Md5Hex(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_tcp.Connected && _stream is not null)
            {
                if (OnCloseSql is not null) await RunAsync(OnCloseSql, 0, CancellationToken.None);
                await SendAsync('X', [], CancellationToken.None); // Terminate
            }
        }
        catch (Exception ex) when (ex is TestDbException or IOException or SocketException or ObjectDisposedException)
        {
        }
        _tcp.Dispose();
    }

    /// <summary>SCRAM-SHA-256 client (RFC 5802 / 7677) without channel binding.</summary>
    private sealed class ScramState(string password)
    {
        private readonly string _nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        private byte[]? _serverSignature;

        private string ClientFirstBare => $"n=,r={_nonce}";
        public string ClientFirst => "n,," + ClientFirstBare;

        public string ClientFinal(string serverFirst)
        {
            var attrs = serverFirst.Split(',').Where(a => a.Length > 2).ToDictionary(a => a[0], a => a[2..]);
            string nonce = attrs['r'];
            if (!nonce.StartsWith(_nonce, StringComparison.Ordinal)) throw new TestDbException("Nonce SCRAM không hợp lệ");
            byte[] salt = Convert.FromBase64String(attrs['s']);
            int iterations = int.Parse(attrs['i'], CultureInfo.InvariantCulture);

            byte[] salted = Rfc2898DeriveBytes.Pbkdf2(Utf8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
            byte[] clientKey = HMACSHA256.HashData(salted, Utf8.GetBytes("Client Key"));
            byte[] storedKey = SHA256.HashData(clientKey);
            string withoutProof = $"c=biws,r={nonce}";
            string authMessage = $"{ClientFirstBare},{serverFirst},{withoutProof}";
            byte[] clientSignature = HMACSHA256.HashData(storedKey, Utf8.GetBytes(authMessage));
            byte[] proof = new byte[clientKey.Length];
            for (int i = 0; i < proof.Length; i++) proof[i] = (byte)(clientKey[i] ^ clientSignature[i]);
            byte[] serverKey = HMACSHA256.HashData(salted, Utf8.GetBytes("Server Key"));
            _serverSignature = HMACSHA256.HashData(serverKey, Utf8.GetBytes(authMessage));
            return $"{withoutProof},p={Convert.ToBase64String(proof)}";
        }

        public bool VerifyServerFinal(string serverFinal)
        {
            var v = serverFinal.Split(',').FirstOrDefault(a => a.StartsWith("v=", StringComparison.Ordinal));
            return v is not null && _serverSignature is not null
                && CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(v[2..]), _serverSignature);
        }
    }
}
