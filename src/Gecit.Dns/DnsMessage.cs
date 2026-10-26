using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Gecit.Dns;

// Just enough DNS wire format for forwarding, truncation and simple lookups.
public static class DnsMessage
{
    const int HeaderSize = 12;

    public static byte[] BuildQuery(string name, ushort type = 1, ushort id = 0)
    {
        var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        header[2] = 0x01;                                        // RD
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);  // QDCOUNT
        ms.Write(header);
        foreach (string label in name.TrimEnd('.').Split('.'))
        {
            byte[] b = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail, type);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1);    // IN
        ms.Write(tail);
        return ms.ToArray();
    }

    public static ushort Id(ReadOnlySpan<byte> msg) => BinaryPrimitives.ReadUInt16BigEndian(msg);

    // Largest UDP response the client accepts: its EDNS0 size, or 512 without EDNS.
    public static int MaxUdpSize(ReadOnlySpan<byte> query)
    {
        try
        {
            int arCount = BinaryPrimitives.ReadUInt16BigEndian(query[10..]);
            int pos = QuestionEnd(query);
            // OPT record: root name (0), TYPE 41, CLASS = payload size
            if (arCount > 0 && query[pos] == 0 && BinaryPrimitives.ReadUInt16BigEndian(query[(pos + 1)..]) == 41)
                return Math.Max(512, (int)BinaryPrimitives.ReadUInt16BigEndian(query[(pos + 3)..]));
        }
        catch (IndexOutOfRangeException) { }
        catch (ArgumentOutOfRangeException) { }
        return 512;
    }

    // Header + question only, with QR and TC set, so the client retries over TCP.
    public static byte[] Truncated(ReadOnlySpan<byte> query, ReadOnlySpan<byte> response)
    {
        int end = QuestionEnd(query);
        byte[] r = query[..end].ToArray();
        r[2] = (byte)(response[2] | 0x80 | 0x02);
        r[3] = response[3];
        r.AsSpan(6, 6).Clear();   // AN/NS/AR counts
        return r;
    }

    public static byte[] ServFail(ReadOnlySpan<byte> query)
    {
        int end = QuestionEnd(query);
        byte[] r = query[..end].ToArray();
        r[2] = (byte)(0x80 | (query[2] & 0x01));   // QR + copy RD
        r[3] = 0x80 | 2;                            // RA, RCODE=SERVFAIL
        r.AsSpan(6, 6).Clear();
        return r;
    }

    // IPv4 addresses from the answer section (CNAMEs etc. skipped).
    public static List<IPAddress> ReadA(ReadOnlySpan<byte> msg)
    {
        var list = new List<IPAddress>();
        int anCount = BinaryPrimitives.ReadUInt16BigEndian(msg[6..]);
        int pos = QuestionEnd(msg);
        for (int i = 0; i < anCount && pos < msg.Length; i++)
        {
            pos = SkipName(msg, pos);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(msg[pos..]);
            ushort rdLen = BinaryPrimitives.ReadUInt16BigEndian(msg[(pos + 8)..]);
            pos += 10;
            if (type == 1 && rdLen == 4)
                list.Add(new IPAddress(msg.Slice(pos, 4)));
            pos += rdLen;
        }
        return list;
    }

    // "1.1.168.192.in-addr.arpa" for 192.168.1.1.
    public static string ReverseName(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";
    }

    // Target of the first PTR record in the answer section, or null. Throws on a malformed message.
    public static string? ReadPtr(ReadOnlySpan<byte> msg)
    {
        int anCount = BinaryPrimitives.ReadUInt16BigEndian(msg[6..]);
        int pos = QuestionEnd(msg);
        for (int i = 0; i < anCount && pos < msg.Length; i++)
        {
            pos = SkipName(msg, pos);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(msg[pos..]);
            ushort rdLen = BinaryPrimitives.ReadUInt16BigEndian(msg[(pos + 8)..]);
            pos += 10;
            if (type == 12)
                return ReadName(msg, pos);
            pos += rdLen;
        }
        return null;
    }

    static string ReadName(ReadOnlySpan<byte> msg, int pos)
    {
        var sb = new StringBuilder();
        for (int jumps = 0; jumps < 16;)
        {
            byte len = msg[pos];
            if (len == 0) break;
            if ((len & 0xC0) == 0xC0)
            {
                pos = ((len & 0x3F) << 8) | msg[pos + 1];
                jumps++;
                continue;
            }
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(msg.Slice(pos + 1, len)));
            pos += len + 1;
        }
        return sb.ToString();
    }

    static int QuestionEnd(ReadOnlySpan<byte> msg)
    {
        int qdCount = BinaryPrimitives.ReadUInt16BigEndian(msg[4..]);
        int pos = HeaderSize;
        for (int i = 0; i < qdCount; i++)
            pos = SkipName(msg, pos) + 4;
        return pos;
    }

    static int SkipName(ReadOnlySpan<byte> msg, int pos)
    {
        while (true)
        {
            byte len = msg[pos];
            if (len == 0) return pos + 1;
            if ((len & 0xC0) == 0xC0) return pos + 2;   // compression pointer ends the name
            pos += len + 1;
        }
    }
}
