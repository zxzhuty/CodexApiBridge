namespace CodexApiBridge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(arg => arg is "-h" or "--help"))
        {
            PrintHelp();
            return 0;
        }

        var configPath = Path.GetFullPath(GetOption(args, "--config") ?? "bridge-config.json");
        var settingsStore = new SettingsStore(configPath);
        var settings = settingsStore.Current;
        var state = new BridgeState(settings.LogDirectory);
        await using var server = new BridgeServer(state, settingsStore);
        using var shutdown = new CancellationTokenSource();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            Validate(settings);
            state.Info($"配置文件：{configPath}");
            state.Info($"监听地址：{settings.ListenUrl}");
            state.Info($"管理页面：{settings.ListenUrl.TrimEnd('/')}/admin");
            state.Info($"上游地址：{settings.UpstreamBaseUrl}/v1/chat/completions");

            await server.StartAsync(shutdown.Token);
            state.Info("代理运行中。按 Ctrl+C 停止。");
            await RunStatsLoopAsync(state, shutdown.Token);
            await server.StopAsync();
            return 0;
        }
        catch (OperationCanceledException)
        {
            await server.StopAsync();
            return 0;
        }
        catch (Exception ex)
        {
            state.Error(ex.ToString());
            return 1;
        }
    }

    private static async Task RunStatsLoopAsync(BridgeState state, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            state.PrintStats();
        }
    }

    private static void Validate(BridgeSettings settings)
    {
        if (!Uri.TryCreate(settings.ListenUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("监听地址不是有效 URL。");
        }

        if (!Uri.TryCreate(settings.UpstreamBaseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("上游 API Base URL 不是有效 URL。");
        }

        if (settings.RequestTimeoutSeconds < 30)
        {
            throw new InvalidOperationException("请求超时不能小于 30 秒。");
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length ? args[i + 1] : null;
            }
        }

        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Codex API Bridge");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  dotnet run -- --config bridge-config.json");
        Console.WriteLine("  CodexApiBridge --config /path/to/bridge-config.json");
        Console.WriteLine();
        Console.WriteLine("启动后打开 /admin 管理页面。");
    }
}
