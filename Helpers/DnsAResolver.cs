using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ByeWhiteList.Windows.Helpers
{
    internal static class DnsAResolver
    {
        public static async Task<IReadOnlyList<IPAddress>> ResolveAAsync(string host, TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(host))
                return Array.Empty<IPAddress>();

            // Fast path for literals.
            if (IPAddress.TryParse(host, out var ip))
                return new[] { ip };

            // Prefer system resolver (usually the most reliable on Windows).
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host).WaitAsync(timeout).ConfigureAwait(false);
                var v4 = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Distinct().ToList();
                if (v4.Count > 0)
                    return v4;
            }
            catch
            {
                // fall back to UDP query below (best-effort)
            }

            var dnsServers = GetDnsServersV4();
            if (dnsServers.Count == 0)
                return Array.Empty<IPAddress>();

            using var cts = new CancellationTokenSource(timeout);
            foreach (var dnsServer in dnsServers)
            {
                var ips = await QueryAAsync(dnsServer, host, cts.Token).ConfigureAwait(false);
                if (ips.Count > 0)
                    return ips;
            }

            return Array.Empty<IPAddress>();
        }

        private static List<IPAddress> GetDnsServersV4()
        {
            var servers = new List<IPAddress>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                // Avoid tunnel adapters; their DNS often isn't usable before routing is fully configured.
                if (nic.Name.Contains("xray", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("xray", StringComparison.OrdinalIgnoreCase) ||
                    nic.Name.Contains("wintun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Name.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Name.Contains("byewhitelist", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("byewhitelist", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var props = nic.GetIPProperties();
                    foreach (var dns in props.DnsAddresses)
                    {
                        if (dns.AddressFamily != AddressFamily.InterNetwork)
                            continue;

                        // Skip link-local DNS (almost always noise).
                        var b = dns.GetAddressBytes();
                        if (b.Length == 4 && b[0] == 169 && b[1] == 254)
                            continue;

                        if (!servers.Contains(dns))
                            servers.Add(dns);
                    }
                }
                catch { /* ignore */ }
            }

            return servers;
        }

        private static async Task<List<IPAddress>> QueryAAsync(IPAddress dnsServer, string host, CancellationToken token)
        {
            var result = new List<IPAddress>();

            try
            {
                var query = BuildQuery(host, out ushort id);
                using var client = new UdpClient(AddressFamily.InterNetwork);

                // Keep the local endpoint deterministic (no need for broadcast/multicast).
                client.Connect(new IPEndPoint(dnsServer, 53));
                token.ThrowIfCancellationRequested();
                await client.SendAsync(query, query.Length).ConfigureAwait(false);

                var res = await client.ReceiveAsync().WaitAsync(token).ConfigureAwait(false);

                if (res.Buffer == null || res.Buffer.Length < 12)
                    return result;

                ParseResponseA(res.Buffer, id, result);
            }
            catch (OperationCanceledException)
            {
                // timeout/cancel -> empty
            }
            catch
            {
                // best-effort -> empty
            }

            return result;
        }

        private static byte[] BuildQuery(string host, out ushort id)
        {
            id = (ushort)Random.Shared.Next(1, ushort.MaxValue);

            // Header (12 bytes)
            // ID, Flags(0x0100 recursion desired), QDCOUNT=1, ANCOUNT=0, NSCOUNT=0, ARCOUNT=0
            var buffer = new List<byte>(64);
            buffer.Add((byte)(id >> 8));
            buffer.Add((byte)(id & 0xFF));
            buffer.Add(0x01);
            buffer.Add(0x00);
            buffer.Add(0x00);
            buffer.Add(0x01);
            buffer.Add(0x00);
            buffer.Add(0x00);
            buffer.Add(0x00);
            buffer.Add(0x00);
            buffer.Add(0x00);
            buffer.Add(0x00);

            // Question
            foreach (var label in host.TrimEnd('.').Split('.'))
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes(label);
                buffer.Add((byte)bytes.Length);
                buffer.AddRange(bytes);
            }
            buffer.Add(0x00); // end of QNAME

            buffer.Add(0x00);
            buffer.Add(0x01); // QTYPE=A
            buffer.Add(0x00);
            buffer.Add(0x01); // QCLASS=IN

            return buffer.ToArray();
        }

        private static void ParseResponseA(byte[] data, ushort expectedId, List<IPAddress> output)
        {
            if (data.Length < 12)
                return;

            ushort id = (ushort)((data[0] << 8) | data[1]);
            if (id != expectedId)
                return;

            ushort flags = (ushort)((data[2] << 8) | data[3]);
            int rcode = flags & 0x000F;
            if (rcode != 0)
                return; // NXDOMAIN/servfail/etc -> treat as empty without throwing

            int qdCount = (data[4] << 8) | data[5];
            int anCount = (data[6] << 8) | data[7];

            int offset = 12;

            // Skip questions
            for (int i = 0; i < qdCount; i++)
            {
                if (!SkipName(data, ref offset))
                    return;
                offset += 4; // QTYPE + QCLASS
                if (offset > data.Length)
                    return;
            }

            // Parse answers
            for (int i = 0; i < anCount; i++)
            {
                if (!SkipName(data, ref offset))
                    return;
                if (offset + 10 > data.Length)
                    return;

                ushort type = (ushort)((data[offset] << 8) | data[offset + 1]);
                ushort klass = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
                // TTL: offset+4..+7
                ushort rdLength = (ushort)((data[offset + 8] << 8) | data[offset + 9]);
                offset += 10;

                if (offset + rdLength > data.Length)
                    return;

                if (type == 1 && klass == 1 && rdLength == 4)
                {
                    var ip = new IPAddress(new[] { data[offset], data[offset + 1], data[offset + 2], data[offset + 3] });
                    if (!output.Contains(ip))
                        output.Add(ip);
                }

                offset += rdLength;
            }
        }

        private static bool SkipName(byte[] data, ref int offset)
        {
            if (offset >= data.Length)
                return false;

            while (offset < data.Length)
            {
                byte len = data[offset];

                // Pointer (11xxxxxx xxxxxxxx)
                if ((len & 0xC0) == 0xC0)
                {
                    offset += 2;
                    return offset <= data.Length;
                }

                // End of name
                if (len == 0)
                {
                    offset += 1;
                    return offset <= data.Length;
                }

                offset += 1 + len;
                if (offset > data.Length)
                    return false;
            }

            return false;
        }
    }
}
