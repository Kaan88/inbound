using DownloadManager.Sites;

// RcloneProbe <rclone> <state folder>: start rclone as the Host does, print its pid, then wait to be killed.
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: RcloneProbe <rclone> <state folder>");
    return 2;
}

var rclone = await RcloneProcess.StartAsync(args[0], args[1], line => Console.Error.WriteLine(line));
Console.WriteLine(rclone.ProcessId);
Console.Out.Flush();
await Task.Delay(Timeout.Infinite);
return 0;
