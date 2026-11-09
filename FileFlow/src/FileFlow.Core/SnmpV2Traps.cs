using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace FileFlow.Core;

public enum TrapEvent
{
    Started = 1,
    TaskFailed = 2,
    Recovered = 3,
    Heartbeat = 4,
    Stopping = 5,
    Test = 6
}

/// <summary>
/// A minimal standards-compliant SNMPv2c TRAP sender (outbound UDP/162).
/// No listener, SNMP GET agent, or third-party package is required.
/// Lab/example OIDs use 1.3.6.1.3.9999; change to your registered PEN in production.
/// </summary>
public sealed class SnmpV2Traps
{
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    public const string BaseOid = "1.3.6.1.3.9999";

    public async Task SendAsync(SnmpSettings settings, TrapEvent kind,
        string taskName, string message, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) return;
        var payload = BuildPacket(settings.Community, kind, taskName, message);
        using var client = new UdpClient();
        await client.SendAsync(payload.AsMemory(), settings.Server, settings.Port, cancellationToken);
    }

    public static byte[] BuildPacket(string community, TrapEvent kind, string taskName, string message)
    {
        if (string.IsNullOrWhiteSpace(community))
            throw new ArgumentException("SNMP community must not be empty.", nameof(community));

        var notification = BaseOid + ".0." + (int)kind;
        var bindings = Sequence(
            VarBind("1.3.6.1.2.1.1.3.0", Timeticks((uint)Math.Min(
                Uptime.Elapsed.TotalMilliseconds / 10, uint.MaxValue))),
            VarBind("1.3.6.1.6.3.1.1.4.1.0", ObjectId(notification)),
            VarBind(BaseOid + ".1.1.0", OctetString("FileFlowService")),
            VarBind(BaseOid + ".1.2.0", OctetString(kind.ToString())),
            VarBind(BaseOid + ".1.3.0", OctetString(Limit(taskName, 160))),
            VarBind(BaseOid + ".1.4.0", OctetString(Limit(message, 350))),
            VarBind(BaseOid + ".1.5.0", OctetString(Environment.MachineName)));

        var pdu = Tlv(0xA7, Combine(
            Integer(Random.Shared.Next(1, int.MaxValue)), Integer(0), Integer(0), bindings));
        return Sequence(Integer(1), OctetString(community), pdu);
    }

    private static string Limit(string value, int maxBytes)
    {
        var bytes = 0;
        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes) break;
            bytes += rune.Utf8SequenceLength;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private static byte[] VarBind(string oid, byte[] value) => Sequence(ObjectId(oid), value);
    private static byte[] Sequence(params byte[][] values) => Tlv(0x30, Combine(values));
    private static byte[] OctetString(string value) => Tlv(0x04, Encoding.UTF8.GetBytes(value));
    private static byte[] ObjectId(string value) => Tlv(0x06, EncodeOid(value));
    private static byte[] Integer(int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        var first = 0;
        while (first < 3 && buffer[first] == 0 && (buffer[first + 1] & 0x80) == 0)
            first++;
        return Tlv(0x02, buffer[first..].ToArray());
    }

    private static byte[] Timeticks(uint value)
    {
        Span<byte> bytes = stackalloc byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(bytes[1..], value);
        var start = 1;
        while (start < 4 && bytes[start] == 0) start++;
        if ((bytes[start] & 0x80) != 0) start--;
        return Tlv(0x43, bytes[start..].ToArray());
    }

    private static byte[] EncodeOid(string dotted)
    {
        var arcs = dotted.Split('.').Select(uint.Parse).ToArray();
        if (arcs.Length < 2 || arcs[0] > 2 || (arcs[0] < 2 && arcs[1] > 39))
            throw new ArgumentException("Invalid SNMP object identifier.", nameof(dotted));
        var result = new List<byte>();
        AppendBase128(result, arcs[0] * 40 + arcs[1]);
        for (var i = 2; i < arcs.Length; i++) AppendBase128(result, arcs[i]);
        return result.ToArray();
    }

    private static void AppendBase128(List<byte> destination, uint number)
    {
        Span<byte> encoded = stackalloc byte[5];
        var count = 0;
        do
        {
            encoded[4 - count] = (byte)(number & 0x7F);
            number >>= 7;
            count++;
        } while (number > 0);
        for (var i = 5 - count; i < 5; i++)
            destination.Add((byte)(encoded[i] | (i < 4 ? 0x80 : 0)));
    }

    private static byte[] Tlv(byte tag, byte[] payload)
    {
        var length = payload.Length;
        byte[] encodedLength = length < 128 ? [(byte)length] : EncodeLength(length);
        var bytes = new byte[1 + encodedLength.Length + length];
        bytes[0] = tag;
        encodedLength.CopyTo(bytes, 1);
        payload.CopyTo(bytes, 1 + encodedLength.Length);
        return bytes;
    }

    private static byte[] EncodeLength(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        var i = 0;
        while (i < 3 && bytes[i] == 0) i++;
        var size = 4 - i;
        var result = new byte[size + 1];
        result[0] = (byte)(0x80 | size);
        bytes[i..].CopyTo(result.AsSpan(1));
        return result;
    }

    private static byte[] Combine(params byte[][] chunks)
    {
        var buffer = new byte[chunks.Sum(c => c.Length)];
        var position = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, buffer, position, chunk.Length);
            position += chunk.Length;
        }
        return buffer;
    }
}
