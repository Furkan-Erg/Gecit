using System.ServiceProcess;
using Gecit.Dns;

// Test tool for the forwarder (the real service is hosted by Gecit.exe --dns-service).
// Usage: Gecit.Dns.exe <providerId> --console [--port N]   (forwarder in this console)
//        Gecit.Dns.exe <providerId> --query <name>         (one DoH lookup, prints the result or error)
//        Gecit.Dns.exe <providerId>                        (as a Windows service)
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
