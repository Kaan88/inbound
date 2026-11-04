var logPath = Environment.GetEnvironmentVariable("HOOK_SPY_LOG");
if (string.IsNullOrWhiteSpace(logPath))
{
    logPath = Path.Combine(AppContext.BaseDirectory, "hook-args.txt");
}

File.AppendAllText(logPath, string.Join('\n', args) + Environment.NewLine);
return args.Length == 1 ? 0 : 3;
