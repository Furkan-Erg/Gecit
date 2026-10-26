using System.ServiceProcess;
using Gecit.Dns;

// Usage: Gecit.Dns.exe <providerId>                     (started by the service manager as GecitDNS)
//        Gecit.Dns.exe <providerId> --console [--port N] (manual testing)
//        Gecit.Dns.exe <providerId> --query <name>         (one DoH lookup, prints the result or error)
var provider = DnsProviders.Find(args.FirstOrDefault()) ?? DnsProviders.All[0];

if (args.Contains("--query"))
{
    string name = args[Array.IndexOf(args, "--query") + 1];
    using var doh = new DohClient(provider);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        byte[] resp = await doh.QueryAsync(DnsMessage.BuildQuery(name), TimeSpan.FromSeconds(5));
        Console.WriteLine($"{provider.Id}: {string.Join(", ", DnsMessage.ReadA(resp))} ({sw.ElapsedMilliseconds} ms)");
    }
    catch (Exception e)
    {
        Console.WriteLine($"{provider.Id}: HATA {e}");
    }
    return;
}

if (args.Contains("--console"))
{
    int i = Array.IndexOf(args, "--port");
    int port = i >= 0 ? int.Parse(args[i + 1]) : 53;
    await using var fwd = new Forwarder(provider, port) { Log = Console.Error.WriteLine };
    fwd.Start();
    Console.WriteLine($"GecitDNS: 127.0.0.1:{port} ve [::1]:{port} -> {provider.DohUrl}  (Ctrl+C ile çık)");
    var done = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
    await done.Task;
    return;
}

ServiceBase.Run(new DnsWindowsService(provider));

sealed class DnsWindowsService : ServiceBase
{
    readonly DnsProvider provider;
    Forwarder? forwarder;

    public DnsWindowsService(DnsProvider provider)
    {
        this.provider = provider;
        ServiceName = "GecitDNS";
        CanShutdown = true;
        // AutoLog would register an event source, which LocalService isn't allowed to do.
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        forwarder = new Forwarder(provider);
        forwarder.Start();
    }

    protected override void OnStop() => forwarder?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));

    protected override void OnShutdown() => OnStop();
}
