using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using HotspotApp.Logging;
using HotspotApp.Localization;

namespace HotspotApp.Services.Infrastructure;

// Executado apenas no supervisor, nunca na thread da interface. Não instala driver.
internal sealed partial class NdisDnsSession
{
    private ushort Port;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<(byte, string, ushort), long> flows = new();
    private readonly ConcurrentDictionary<int, Task> clients = new();
    private readonly SemaphoreSlim slots = new(32);
    private readonly List<Task> workers = [];
    private readonly string rule = "YHotspotDns-" + Guid.NewGuid().ToString("N");
    private readonly string journal = Path.Combine(LogPaths.DirectoryPath, "dns-backend-recovery.json");
    private nint handle, adapter;
    private bool tableOwned, modeOwned, journalOwned;
    private bool cleanupUnconfirmed;
    private static readonly Lazy<nint> sdkModule = new(() => NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "ndisapi.dll")));
    private IPAddress gateway = IPAddress.None;
    private uint network, mask;
    private DnsUpstreamResolver resolver = null!;
    private UdpClient? udp;
    private TcpListener? tcp;
    private Task? capture;
    private long received, sent, resolved, errors;
    private int nextClient, consecutiveErrors;
    public Task Failure => failure.Task;

    public async Task StartAsync(Guid upstreamId, Guid hotspotId, CancellationToken token, IPAddress[]? manualServers = null)
    {
        if (File.Exists(journal)) throw new IOException(AppStrings.Get("Text_CustomDnsPending"));
        var source = NetworkInterface.GetAllNetworkInterfaces().Single(n => Guid.Parse(n.Id) == upstreamId);
        var sourceIp = source.GetIPProperties().UnicastAddresses.Select(a => a.Address)
            .Single(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."));
        // Usar DNS DHCP da origem evita encaminhar ao proxy local de uma VPN.
        var dns = manualServers;
        if (dns == null)
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{" + upstreamId + "}");
            dns = ((string?)key?.GetValue("DhcpNameServer") ?? "").Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => IPAddress.TryParse(s, out var ip) ? ip : null)
                .Where(ip => ip?.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)).Cast<IPAddress>().Distinct().Take(2).ToArray();
            if (dns.Length == 0) dns = source.GetIPProperties().DnsAddresses
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)).Take(2).ToArray();
        }
        if (dns.Length == 0) throw new IOException(AppStrings.Get("Text_CustomDnsNoUpstream"));
        resolver = new(sourceIp, dns.Select(ip => new IPEndPoint(ip, 53)).ToArray(), TimeSpan.FromSeconds(2));
        await resolver.ResolveAsync(Convert.FromHexString("000001000001000000000000076578616D706C6503636F6D0000010001"), token);
        NetworkInterface? nic = null;
        for (var i = 0; i < 30; i++)
        {
            nic = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n => Guid.Parse(n.Id) == hotspotId);
            var address = nic?.GetIPProperties().UnicastAddresses.SingleOrDefault(a =>
                a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."));
            if (address != null)
            {
                gateway = address.Address;
                mask = BinaryPrimitives.ReadUInt32BigEndian(address.IPv4Mask.GetAddressBytes());
                network = BinaryPrimitives.ReadUInt32BigEndian(gateway.GetAddressBytes()) & mask;
                break;
            }
            await Task.Delay(300, token);
        }
        if (gateway.Equals(IPAddress.None) || nic == null) throw new IOException(AppStrings.Get("Text_CustomDnsInterface"));
        if (dns.Any(ip => ip.Equals(gateway) || ip.Equals(sourceIp)))
            throw new IOException(AppStrings.Get("Text_ManualDnsInvalid"));
        token.ThrowIfCancellationRequested();
        OpenDriver(hotspotId);
        // O diário precede regras/filtros. Pendência nunca autoriza apagar filtros alheios.
        File.WriteAllText(journal, JsonSerializer.Serialize(new { rule, hotspotId, pid = Environment.ProcessId }), new System.Text.UTF8Encoding(false));
        journalOwned = true;
        await BindListenersAsync(token);
        ChangeFirewall(true, nic.Name);
        InstallFilters();
        workers.Add(Task.Run(() => GuardAsync(UdpLoopAsync)));
        workers.Add(Task.Run(() => GuardAsync(TcpLoopAsync)));
        capture = Task.Run(Capture);
        ManualTestDiagnostics.Write($"DNS próprio ativo: interface={hotspotId}; gateway={gateway}; porta local={Port}; upstream={string.Join(',', dns.Select(x => x.ToString()))}");
    }

    private async Task BindListenersAsync(CancellationToken token)
    {
        // Porta atribuída pelo Windows evita disputar uma porta fixa após reinícios
        // e conexões TCP em encerramento. As duas regras usam a mesma porta escolhida.
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                // Reservas TCP/UDP diferem (inclusive com Hyper-V). Começar por TCP
                // e só aceitar a porta quando ambos os sockets conseguirem reservá-la.
                tcp = new TcpListener(gateway, 0); tcp.ExclusiveAddressUse = true; tcp.Start(16);
                Port = (ushort)((IPEndPoint)tcp.LocalEndpoint).Port;
                udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.ExclusiveAddressUse = true;
                udp.Client.Bind(new IPEndPoint(gateway, Port));
                return;
            }
            catch (SocketException ex)
            {
                udp?.Dispose(); tcp?.Stop(); udp = null; tcp = null;
                ManualTestDiagnostics.Write($"Abertura do DNS: socket={ex.SocketErrorCode}; tentativa={attempt + 1}");
                if (attempt >= 19 || ex.SocketErrorCode is not (SocketError.AddressAlreadyInUse or SocketError.AddressNotAvailable or SocketError.AccessDenied)) throw;
                await Task.Delay(300, token);
            }
        }
    }

    private unsafe void OpenDriver(Guid id)
    {
        try
        {
            // DLL do SDK compilada por nós; não carregar uma cópia de PATH arbitrário.
            _ = sdkModule.Value;
            handle = OpenFilterDriver("NDISRD");
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        { throw new IOException(AppStrings.Get("Text_CustomDnsDriver"), ex); }
        var list = new byte[8836];
        fixed (byte* p = list)
            if (!GetTcpipBoundAdaptersInfo(handle, (nint)p)) throw new IOException(AppStrings.Get("Text_CustomDnsDriver"));
        if (!GetPacketFilterTableSize(handle, out var count) || count != 0) throw new IOException(AppStrings.Get("Text_CustomDnsBusy"));
        var length = BinaryPrimitives.ReadInt32LittleEndian(list);
        if (length is < 1 or > 32) throw new IOException("Invalid adapter list");
        for (var i = 0; i < length; i++)
        {
            var h = (nint)BinaryPrimitives.ReadInt64LittleEndian(list.AsSpan(8196 + i * 8));
            var mode = new AdapterMode { Adapter = h };
            if (!GetAdapterMode(handle, ref mode) || mode.Flags != 0) throw new IOException(AppStrings.Get("Text_CustomDnsBusy"));
            var name = System.Text.Encoding.ASCII.GetString(list, 4 + i * 256, 256).TrimEnd('\0');
            if (name.Equals(@"\DEVICE\{" + id + "}", StringComparison.OrdinalIgnoreCase)) adapter = h;
        }
        if (adapter == 0) throw new IOException(AppStrings.Get("Text_CustomDnsInterface"));
    }

    private unsafe void InstallFilters()
    {
        var table = NdisDnsPackets.Filters(adapter, gateway, Port);
        fixed (byte* p = table)
            if (!SetPacketFilterTable(handle, (nint)p)) throw new IOException("Cannot install DNS filters");
        tableOwned = true;
        var mode = new AdapterMode { Adapter = adapter, Flags = 3 };
        modeOwned = true;
        if (!SetAdapterMode(handle, ref mode)) throw new IOException("Cannot activate DNS filters");
    }

    private bool Allow(byte proto, IPAddress address, ushort port, bool add)
    {
        var key = (proto, address.ToString(), port);
        var now = Environment.TickCount64;
        if (add)
        {
            foreach (var old in flows.Where(p => p.Value <= now)) flows.TryRemove(old.Key, out _);
            if (flows.Count >= 1024 && !flows.ContainsKey(key)) return false;
            flows[key] = now + (proto == 6 ? 300000 : 30000);
        }
        return flows.TryGetValue(key, out var until) && until > now;
    }

    private unsafe void Capture()
    {
        var buffer = new byte[1566];
        try
        {
            fixed (byte* p = buffer)
            {
                var request = new PacketRequest { Adapter = adapter, Buffer = (nint)p };
                var lastCheck = Environment.TickCount64;
                while (!stop.IsCancellationRequested)
                {
                    if (Environment.TickCount64 - lastCheck > 1000)
                    {
                        var state = new AdapterMode { Adapter = adapter };
                        if (!GetAdapterMode(handle, ref state) || state.Flags != 3)
                            throw new IOException("DNS driver/interface became unavailable");
                        lastCheck = Environment.TickCount64;
                    }
                    if (!ReadPacket(handle, ref request)) { Thread.Sleep(5); continue; }
                    var size = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(20));
                    if (size is < 14 or > 1514) throw new IOException("Invalid Ethernet frame");
                    var outbound = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16)) == 1;
                    if (outbound && NdisDnsPackets.AddMissingDhcpDns(buffer.AsSpan(52, 1514), ref size, gateway))
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(20), size);
                        ManualTestDiagnostics.Write($"DHCP: DNS ausente preenchido com {gateway}; resposta será transmitida uma vez");
                    }
                    if (NdisDnsPackets.Translate(buffer.AsSpan(52, size), outbound, gateway, network, mask, Port, Allow))
                    { if (outbound) Interlocked.Increment(ref sent); else Interlocked.Increment(ref received); }
                    if (!(outbound ? SendPacketToAdapter(handle, ref request) : SendPacketToMstcp(handle, ref request)))
                        throw new IOException("DNS reinjection failed");
                }
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void Fail(Exception error)
    {
        if (stop.IsCancellationRequested) return;
        ManualTestDiagnostics.Write("DNS próprio falhou; supervisor deve parar o hotspot", error);
        failure.TrySetResult(); stop.Cancel();
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        catch (Exception ex) { Fail(ex); }
    }

    private void Schedule(Func<Task> action)
    {
        var id = Interlocked.Increment(ref nextClient);
        var task = Task.Run(async () => { try { await GuardAsync(action); } finally { slots.Release(); } });
        clients[id] = task;
        _ = task.ContinueWith(_ => clients.TryRemove(id, out var ignored), TaskScheduler.Default);
    }

    private async Task<byte[]?> AnswerAsync(byte[] query)
    {
        try
        {
            var answer = await resolver.ResolveAsync(query, stop.Token);
            Interlocked.Increment(ref resolved); Interlocked.Exchange(ref consecutiveErrors, 0);
            return answer;
        }
        catch (InvalidDataException) { return null; } // Um cliente malformado não derruba o serviço.
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            Interlocked.Increment(ref errors);
            if (Interlocked.Increment(ref consecutiveErrors) >= 5) Fail(ex);
            return null;
        }
    }

    private async Task UdpLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            var request = await udp!.ReceiveAsync(stop.Token);
            if (!Allow(17, request.RemoteEndPoint.Address, (ushort)request.RemoteEndPoint.Port, false) || !slots.Wait(0)) continue;
            Schedule(async () =>
            {
                var answer = await AnswerAsync(request.Buffer);
                if (answer != null) await udp.SendAsync(NdisDnsPackets.BoundUdp(request.Buffer, answer), request.RemoteEndPoint, stop.Token);
            });
        }
    }

    private async Task TcpLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            var client = await tcp!.AcceptTcpClientAsync(stop.Token);
            var peer = (IPEndPoint)client.Client.RemoteEndPoint!;
            if (!Allow(6, peer.Address, (ushort)peer.Port, false) || !slots.Wait(0)) { client.Dispose(); continue; }
            Schedule(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream(); var prefix = new byte[2];
                        for (var i = 0; i < 64 && !stop.IsCancellationRequested; i++)
                        {
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                            deadline.CancelAfter(TimeSpan.FromSeconds(5));
                            await stream.ReadExactlyAsync(prefix, deadline.Token);
                            var size = BinaryPrimitives.ReadUInt16BigEndian(prefix);
                            if (size is < 17 or > 4096) return;
                            var query = new byte[size]; await stream.ReadExactlyAsync(query, deadline.Token);
                            var answer = await AnswerAsync(query); if (answer == null) return;
                            BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)answer.Length);
                            await stream.WriteAsync(prefix, deadline.Token); await stream.WriteAsync(answer, deadline.Token);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
                }
            });
        }
    }

    private void ChangeFirewall(bool add, string? interfaceName = null)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        try
        {
            foreach (var protocol in new[] { 6, 17 })
            {
                var name = rule + "-" + protocol;
                if (!add) { policy.Rules.Remove(name); continue; }
                dynamic entry = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
                try
                {
                    entry.Name = name; entry.Description = "Y-HOTSPOT scoped DNS listener";
                    entry.Protocol = protocol; entry.LocalPorts = Port.ToString(); entry.LocalAddresses = gateway.ToString();
                    var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, network);
                    var net = new IPAddress(b); BinaryPrimitives.WriteUInt32BigEndian(b, mask);
                    entry.RemoteAddresses = net + "/" + new IPAddress(b);
                    entry.Interfaces = new object[] { interfaceName! }; entry.Direction = 1; entry.Action = 1;
                    entry.Profiles = int.MaxValue; entry.Enabled = true; policy.Rules.Add(entry);
                }
                finally { Marshal.FinalReleaseComObject(entry); }
            }
        }
        finally { Marshal.FinalReleaseComObject(policy); }
    }

    public async Task StopAsync()
    {
        if (cleanupUnconfirmed) throw new IOException(AppStrings.Get("Text_CustomDnsPending"));
        stop.Cancel();
        if (capture != null) await capture.WaitAsync(TimeSpan.FromSeconds(3));
        var cleanup = new List<Exception>();
        if (handle != 0)
        {
            var mode = new AdapterMode { Adapter = adapter };
            if (modeOwned && !SetAdapterMode(handle, ref mode)) cleanup.Add(new IOException("DNS adapter cleanup failed"));
            if (tableOwned && !ResetPacketFilterTable(handle)) cleanup.Add(new IOException("DNS table cleanup failed"));
            if (modeOwned && (!GetAdapterMode(handle, ref mode) || mode.Flags != 0))
                cleanup.Add(new IOException("DNS adapter restoration not confirmed"));
            if (tableOwned && (!GetPacketFilterTableSize(handle, out var remaining) || remaining != 0))
                cleanup.Add(new IOException("DNS filter removal not confirmed"));
            CloseFilterDriver(handle); handle = 0;
            cleanupUnconfirmed = cleanup.Count != 0;
        }
        udp?.Dispose(); tcp?.Stop();
        await Task.WhenAll(workers.Concat(clients.Values)).WaitAsync(TimeSpan.FromSeconds(6));
        if (journalOwned)
        {
            try { ChangeFirewall(false); } catch (Exception ex) { cleanup.Add(ex); }
            if (cleanup.Count == 0) { File.Delete(journal); journalOwned = false; }
        }
        ManualTestDiagnostics.Write($"DNS próprio encerrado: entrada={received}; saída={sent}; resolvidas={resolved}; falhas={errors}; limpeza={cleanup.Count == 0}");
        if (cleanup.Count != 0) throw new AggregateException(cleanup);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct AdapterMode { public nint Adapter; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct PacketRequest { public nint Adapter, Buffer; }
    [LibraryImport("ndisapi.dll", StringMarshalling = StringMarshalling.Utf8)] private static partial nint OpenFilterDriver(string name);
    [LibraryImport("ndisapi.dll")] private static partial void CloseFilterDriver(nint h);
    [LibraryImport("ndisapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetTcpipBoundAdaptersInfo(nint h, nint list);
    [LibraryImport("ndisapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetAdapterMode(nint h, ref AdapterMode mode);
    [LibraryImport("ndisapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetAdapterMode(nint h, ref AdapterMode mode);
    [LibraryImport("ndisapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetPacketFilterTableSize(nint h, out uint size);
    [LibraryImport("ndisapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetPacketFilterTable(nint h, nint table);
    [LibraryImport("ndisapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ResetPacketFilterTable(nint h);
    [LibraryImport("ndisapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ReadPacket(nint h, ref PacketRequest request);
    [LibraryImport("ndisapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SendPacketToAdapter(nint h, ref PacketRequest request);
    [LibraryImport("ndisapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SendPacketToMstcp(nint h, ref PacketRequest request);
}
