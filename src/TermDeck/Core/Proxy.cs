using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TermDeck.Core;

/// <summary>A named upstream proxy (Burp, Tor, a corporate proxy…) that runs can be sent through.</summary>
public sealed class ProxyProfile
{
    public string Name { get; set; } = "";
    /// <summary><c>http://host:port</c>, <c>socks5://host:port</c> or <c>socks5h://…</c>; <c>user:pass@</c> is allowed.</summary>
    public string Url { get; set; } = "";
    /// <summary>Comma-separated hosts/IPs/CIDRs that go direct (NO_PROXY). Direct connections to these are not reported.</summary>
    public string NoProxy { get; set; } = "localhost,127.0.0.1,::1";
}

/// <summary>
/// The proxy a run was started with. <see cref="Token"/> is exported as <c>TD_RUN</c> in WSL so the monitor can find
/// the run's Linux processes (their PIDs are not visible from Windows).
/// </summary>
public sealed record ProxyLaunch(ProxyProfile Profile, string Token, bool Wsl, string Distro);

/// <summary>
/// Proxy support kept deliberately simple: the active profile is handed to each new run as the usual proxy
/// environment variables (Windows: environment block; WSL: <c>export</c> before the command). Whether a tool honours
/// them is up to the tool, so <see cref="ProxyMonitor"/> watches the run's TCP connections and reports any that go
/// somewhere other than the proxy.
/// </summary>
public static class Proxy
{
    /// <summary>The active profile for new runs; null = off. Set by the main window from the config.</summary>
    public static ProxyProfile? Current { get; set; }

    /// <summary>Raised (on a worker thread) the first time a run is seen connecting directly to an endpoint.</summary>
    public static event Action<RunRecord, string>? LeakDetected;

    internal static void RaiseLeak(RunRecord r, string endpoint) => LeakDetected?.Invoke(r, endpoint);

    public static bool TryParse(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5" or "socks5h")) return false;
        if (string.IsNullOrEmpty(u.Host) || Port(u) <= 0) return false;
        uri = u;
        return true;
    }

    /// <summary>Explicit port, or the scheme's usual one (socks schemes have no default in <see cref="Uri"/>).</summary>
    public static int Port(Uri u) => u.Port > 0 ? u.Port : u.Scheme.StartsWith("socks") ? 1080 : u.Scheme == "https" ? 443 : 80;

    /// <summary>Host without IPv6 brackets.</summary>
    public static string Host(Uri u) => u.Host.Trim('[', ']');

    public static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>The URL with any password hidden, for display.</summary>
    public static string Masked(ProxyProfile p)
    {
        if (!TryParse(p.Url, out var u) || u.UserInfo.Length == 0) return p.Url.Trim();
        var user = u.UserInfo.Split(':')[0];
        return $"{u.Scheme}://{user}:***@{u.Host}:{Port(u)}";
    }

    static string Normalized(Uri u)
    {
        var auth = u.UserInfo.Length > 0 ? u.UserInfo + "@" : "";
        return $"{u.Scheme}://{auth}{u.Host}:{Port(u)}";
    }

    /// <summary>Environment variables for a Windows tool (Windows env names are case-insensitive, so one of each).</summary>
    public static Dictionary<string, string> WindowsEnv(ProxyProfile p)
    {
        var url = Normalized(Parse(p));
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["HTTP_PROXY"] = url,
            ["HTTPS_PROXY"] = url,
            ["ALL_PROXY"] = url,
            ["NO_PROXY"] = p.NoProxy.Trim(),
        };
    }

    /// <summary>
    /// Shell code put before a WSL command: exports the proxy variables (lower- and upper-case; many Linux tools only
    /// read the lower-case ones) plus <c>TD_RUN</c>. A proxy on Windows' loopback is reached through the Windows host
    /// (the default gateway) unless WSL runs in mirrored networking mode, where localhost is shared.
    /// </summary>
    public static string WslPrefix(ProxyProfile p, string token)
    {
        var u = Parse(p);
        var host = Host(u);
        var sb = new StringBuilder();
        if (IsLoopbackHost(host))
            sb.Append("__td_h=127.0.0.1; [ \"$(wslinfo --networking-mode 2>/dev/null)\" = mirrored ] || ")
              .Append("__td_h=$(ip route show default 2>/dev/null | awk '{print $3; exit}'); ");
        else
            sb.Append("__td_h=").Append(Sq(u.Host)).Append("; ");
        var auth = u.UserInfo.Length > 0 ? Sq(u.UserInfo + "@") : "";
        sb.Append("__td_u=").Append(Sq(u.Scheme + "://")).Append(auth).Append("\"${__td_h:-127.0.0.1}\"").Append(Sq(":" + Port(u))).Append("; ");
        sb.Append("export http_proxy=\"$__td_u\" https_proxy=\"$__td_u\" all_proxy=\"$__td_u\" ")
          .Append("HTTP_PROXY=\"$__td_u\" HTTPS_PROXY=\"$__td_u\" ALL_PROXY=\"$__td_u\" ")
          .Append("no_proxy=").Append(Sq(p.NoProxy.Trim())).Append(" NO_PROXY=").Append(Sq(p.NoProxy.Trim()))
          .Append(" TD_RUN=").Append(token).Append("; unset __td_h __td_u; ");
        return sb.ToString();
    }

    static Uri Parse(ProxyProfile p) => TryParse(p.Url, out var u) ? u : throw new ArgumentException("Invalid proxy URL: " + p.Url);

    /// <summary>POSIX single-quoting.</summary>
    static string Sq(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static string NewToken() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// Polls a run's TCP connections every few seconds and reports each remote endpoint it reaches without going through
/// the proxy. Windows tools: every process in the run's job object, via GetExtendedTcpTable. WSL tools: the Linux
/// processes carrying <c>TD_RUN=&lt;token&gt;</c>, via <c>ss -tnp</c> run as root in the distro. Loopback and
/// NO_PROXY addresses are ignored. Raw-socket traffic (SYN scans, ICMP) and UDP are not visible this way.
/// </summary>
public sealed class ProxyMonitor : IDisposable
{
    readonly ProxyLaunch _launch;
    readonly Func<int[]> _windowsPids;
    readonly Action<string> _onLeak;
    readonly HashSet<string> _seen = new();
    readonly int _proxyPort;
    readonly HashSet<IPAddress> _proxyIps = new();
    readonly List<(IPAddress Net, int Bits)> _noProxy = new();
    readonly Timer _timer;
    int _busy;
    bool _disposed;

    public ProxyMonitor(ProxyLaunch launch, Func<int[]> windowsPids, Action<string> onLeak)
    {
        _launch = launch;
        _windowsPids = windowsPids;
        _onLeak = onLeak;
        var u = Proxy.TryParse(launch.Profile.Url, out var uri) ? uri : null;
        _proxyPort = u != null ? Proxy.Port(u) : 0;
        if (u != null)
        {
            try
            {
                var host = Proxy.Host(u);
                foreach (var ip in IPAddress.TryParse(host, out var lit) ? [lit] : Dns.GetHostAddresses(host)) _proxyIps.Add(Norm(ip));
            }
            catch (SocketException) { }
        }
        foreach (var raw in launch.Profile.NoProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split('/');
            if (!IPAddress.TryParse(parts[0].Trim('[', ']'), out var net)) continue; // host names cannot be matched against IPs
            net = Norm(net);
            var max = net.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var bits = parts.Length > 1 && int.TryParse(parts[1], out var b) ? Math.Clamp(b, 0, max) : max;
            _noProxy.Add((net, bits));
        }
        _timer = new Timer(_ => Poll(), null, 1000, 3000);
    }

    void Poll()
    {
        if (_disposed || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var conns = _launch.Wsl ? WslConnections() : WindowsConnections();
            foreach (var (ip, port, proc) in conns)
            {
                if (_disposed || IsAllowed(ip, port)) continue;
                var ep = ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
                lock (_seen) if (!_seen.Add(ep)) continue;
                _onLeak(proc.Length > 0 ? $"{ep} ({proc})" : ep);
            }
        }
        catch { /* best effort: a failed poll just tries again next tick */ }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    // Set per poll for WSL: the Windows host as seen from WSL (where a loopback proxy is reached in NAT mode).
    IPAddress? _gateway;

    bool IsAllowed(IPAddress ip, int port)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (port == _proxyPort && (_proxyIps.Contains(ip) || ip.Equals(_gateway))) return true;
        return _noProxy.Any(n => InNet(ip, n.Net, n.Bits));
    }

    static bool InNet(IPAddress ip, IPAddress net, int bits)
    {
        if (ip.AddressFamily != net.AddressFamily) return false;
        var a = ip.GetAddressBytes();
        var b = net.GetAddressBytes();
        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (b[i] & mask)) return false;
        }
        return true;
    }

    static IPAddress Norm(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    // ───────────── Windows ─────────────

    List<(IPAddress, int, string)> WindowsConnections()
    {
        var pids = new HashSet<int>(_windowsPids());
        var list = new List<(IPAddress, int, string)>();
        if (pids.Count == 0) return list;
        foreach (var (ip, port, pid) in TcpTable.Connections())
        {
            if (!pids.Contains(pid)) continue;
            string name;
            try { name = Process.GetProcessById(pid).ProcessName; } catch { name = "pid " + pid; }
            list.Add((Norm(ip), port, name));
        }
        return list;
    }

    // ───────────── WSL ─────────────

    static readonly Regex PidRx = new(@"pid=(\d+)", RegexOptions.Compiled);
    static readonly Regex NameRx = new(@"\(\(""([^""]+)""", RegexOptions.Compiled);

    List<(IPAddress, int, string)> WslConnections()
    {
        var script =
            "echo \"G $(ip route show default 2>/dev/null | awk '{print $3; exit}')\"; " +
            "for d in /proc/[0-9]*; do tr '\\0' '\\n' < \"$d/environ\" 2>/dev/null | grep -qx 'TD_RUN=" + _launch.Token + "' && echo \"P ${d#/proc/}\"; done; " +
            "ss -tnpH 2>/dev/null";
        var psi = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (_launch.Distro.Length > 0) { psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(_launch.Distro); }
        foreach (var a in new[] { "-u", "root", "-e", "sh", "-c", script }) psi.ArgumentList.Add(a);
        psi.Environment["WSL_UTF8"] = "1";

        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch { } return new(); }
        var lines = outTask.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var pids = new HashSet<int>();
        _gateway = null;
        foreach (var l in lines)
        {
            if (l.StartsWith("G ") && IPAddress.TryParse(l[2..].Trim(), out var g)) _gateway = Norm(g);
            else if (l.StartsWith("P ") && int.TryParse(l[2..].Trim(), out var pid)) pids.Add(pid);
        }
        var list = new List<(IPAddress, int, string)>();
        if (pids.Count == 0) return list;
        foreach (var l in lines)
        {
            if (l.StartsWith("G ") || l.StartsWith("P ")) continue;
            // STATE RECV-Q SEND-Q LOCAL PEER users:(("name",pid=N,fd=M),...)
            var f = l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 6 || f[0] == "LISTEN") continue;
            if (!PidRx.Matches(l).Any(m => pids.Contains(int.Parse(m.Groups[1].Value)))) continue;
            if (!TryEndpoint(f[4], out var ip, out var port)) continue;
            var name = NameRx.Match(l) is { Success: true } nm ? nm.Groups[1].Value : "";
            list.Add((ip, port, name));
        }
        return list;
    }

    /// <summary>Parses ss peer addresses: <c>1.2.3.4:443</c>, <c>[2001:db8::1]:443</c>, <c>[::ffff:1.2.3.4]:443</c>, <c>fe80::1%eth0:22</c>.</summary>
    static bool TryEndpoint(string s, out IPAddress ip, out int port)
    {
        ip = IPAddress.None;
        port = 0;
        var colon = s.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(s[(colon + 1)..], out port)) return false;
        var host = s[..colon].Trim('[', ']');
        var pct = host.IndexOf('%');
        if (pct >= 0) host = host[..pct];
        if (!IPAddress.TryParse(host, out var parsed)) return false;
        ip = Norm(parsed);
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}

/// <summary>TCP connections of all processes (IPv4 + IPv6), from the IP Helper API.</summary>
static class TcpTable
{
    const int AF_INET = 2, AF_INET6 = 23;
    const int TCP_TABLE_OWNER_PID_ALL = 5;
    const uint MIB_TCP_STATE_LISTEN = 2;

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    public static List<(IPAddress Ip, int Port, int Pid)> Connections()
    {
        var list = new List<(IPAddress, int, int)>();
        Read(AF_INET, list);
        Read(AF_INET6, list);
        return list;
    }

    static void Read(int af, List<(IPAddress, int, int)> list)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            size += 4096; // the table can grow between the two calls
            var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0) != 0) continue;
                var bytes = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(buf, bytes, 0, size);
                var n = BitConverter.ToInt32(bytes, 0);
                if (af == AF_INET)
                {
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (6 × DWORD)
                    for (var i = 0; i < n; i++)
                    {
                        var o = 4 + i * 24;
                        if (BitConverter.ToUInt32(bytes, o) == MIB_TCP_STATE_LISTEN) continue;
                        var ip = new IPAddress(BitConverter.ToUInt32(bytes, o + 12));
                        list.Add((ip, NetPort(bytes, o + 16), BitConverter.ToInt32(bytes, o + 20)));
                    }
                }
                else
                {
                    // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
                    for (var i = 0; i < n; i++)
                    {
                        var o = 4 + i * 56;
                        if (BitConverter.ToUInt32(bytes, o + 48) == MIB_TCP_STATE_LISTEN) continue;
                        var ip = new IPAddress(bytes.AsSpan(o + 24, 16));
                        list.Add((ip, NetPort(bytes, o + 44), BitConverter.ToInt32(bytes, o + 52)));
                    }
                }
                return;
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
        }
    }

    /// <summary>Ports are stored in network byte order in the low 16 bits.</summary>
    static int NetPort(byte[] b, int o) => (b[o] << 8) | b[o + 1];
}
