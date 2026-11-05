using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task PerDownloadProgramOverridesGlobalAndMarkerPrecedesActionsWithoutRestartReplay()
    {
        var executor = new CompletionActionObserver();
        var markerHook = new MarkerObservingPostDownloadHook();
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false,
            completionActions: executor, postDownloadHook: markerHook);
        var globalProgram = Path.Combine(ctx.Destination, "global-fake.exe");
        var perDownloadProgram = Path.Combine(ctx.Destination, "per-download-fake.exe");
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.PostDownloadCommand = globalProgram;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/completion-options.bin?size=32768&seed=406",
            DestinationDirectory = ctx.Destination,
            FileName = "completion-options.bin",
            Source = "cli",
            StartMode = "later",
        });
        Assert.True(added.Accepted, added.Error);
        var jobId = added.Job!.Id;
        markerHook.SetJobDocumentPath(Path.Combine(ctx.Paths.JobsDirectory, $"{jobId:N}.json"));

        var edited = await ctx.Service.UpdateJobAsync(new JobUpdateRequest
        {
            JobId = jobId,
            CompletionOptions = new JobCompletionOptionsDto
            {
                ProgramPath = perDownloadProgram,
                OpenFile = true,
                SystemAction = "none",
            },
        });
        Assert.True(edited.Ok, edited.Error);
        Assert.True((await ctx.Service.ResumeAsync(jobId)).Ok);
        var completed = await ctx.WaitFor(jobId, job => job.State == "completed");

        Assert.True(await markerHook.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            "The fake hook should observe the persisted execution marker before being invoked.");
        await executor.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { perDownloadProgram }, markerHook.Commands);
        Assert.True(markerHook.MarkerWasPersisted);
        Assert.Equal(new[] { completed.FinalFilePath }, executor.OpenedPaths);
        Assert.Equal(0, executor.ExitCount);
        Assert.Equal(0, executor.ShutdownCount);

        var completedJson = await File.ReadAllTextAsync(Path.Combine(ctx.Paths.JobsDirectory, $"{jobId:N}.json"));
        using (var document = JsonDocument.Parse(completedJson))
            Assert.True(document.RootElement.GetProperty("completionActionsStarted").GetBoolean());
        var rejectedEdit = await ctx.Service.UpdateJobAsync(new JobUpdateRequest
        {
            JobId = jobId,
            CompletionOptions = new JobCompletionOptionsDto { ProgramPath = string.Empty },
        });
        Assert.False(rejectedEdit.Ok);

        await ctx.Service.ShutdownAsync();
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination,
            autoResume: false, completionActions: executor, postDownloadHook: markerHook);
        Assert.Equal("completed", restarted.Service.GetJob(jobId)!.State);
        Assert.Single(markerHook.Commands);
        Assert.Single(executor.OpenedPaths);
        Assert.Equal(new[] { perDownloadProgram }, markerHook.Commands);
    }

    [Fact]
    public async Task EmptyPerDownloadProgramDisablesGlobalHookWhenNoActionsAreSelected()
    {
        var executor = new CompletionActionObserver();
        var hook = new RecordingPostDownloadHook();
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false,
            completionActions: executor, postDownloadHook: hook);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.PostDownloadCommand = Path.Combine(ctx.Destination, "global-disabled-fake.exe");
        settings.AllowSystemCompletionActions = true;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/no-global-completion.bin?size=32768&seed=407",
            DestinationDirectory = ctx.Destination,
            FileName = "no-global-completion.bin",
            Source = "cli",
            StartMode = "later",
        });
        Assert.True(added.Accepted, added.Error);
        var edited = await ctx.Service.UpdateJobAsync(new JobUpdateRequest
        {
            JobId = added.Job!.Id,
            CompletionOptions = new JobCompletionOptionsDto
            {
                ProgramPath = string.Empty,
                OpenFile = false,
                SystemAction = "none",
            },
        });
        Assert.True(edited.Ok, edited.Error);
        Assert.True((await ctx.Service.ResumeAsync(added.Job.Id)).Ok);
        await ctx.WaitFor(added.Job.Id, job => job.State == "completed");

        await ctx.Service.ShutdownAsync();
        Assert.Empty(hook.Calls);
        Assert.Empty(executor.OpenedPaths);
        Assert.Equal(0, executor.ExitCount);
        Assert.Equal(0, executor.ShutdownCount);
    }

    [Fact]
    public async Task SystemCompletionOptionRequiresOptInAndOtherLaterJobBlocksUntilRemoved()
    {
        var executor = new CompletionActionObserver();
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false,
            completionActions: executor);
        var blocked = await AddLater("blocked-system-action.bin", seed: 408);

        var rejected = await ctx.Service.UpdateJobAsync(new JobUpdateRequest
        {
            JobId = blocked.Id,
            CompletionOptions = RestartOptions(),
        });
        Assert.False(rejected.Ok);
        Assert.Contains("Enable system completion actions", rejected.Error);

        var enabled = ctx.Service.GetSnapshot().Settings;
        enabled.AllowSystemCompletionActions = true;
        Assert.True((await ctx.Service.UpdateSettingsAsync(enabled)).Ok);
        var accepted = await ctx.Service.UpdateJobAsync(new JobUpdateRequest
        {
            JobId = blocked.Id,
            CompletionOptions = RestartOptions(),
        });
        Assert.True(accepted.Ok, accepted.Error);

        var blocker = await AddLater("unfinished-blocker.bin", seed: 409);
        Assert.True((await ctx.Service.ResumeAsync(blocked.Id)).Ok);
        await ctx.WaitFor(blocked.Id, job => job.State == "completed");
        Assert.Equal(0, executor.ShutdownCount);
        Assert.Equal("queued", ctx.Service.GetJob(blocker.Id)!.State);

        var removeBlocker = await ctx.Service.RemoveAsync(blocker.Id, deleteFile: true);
        Assert.True(removeBlocker.Ok, removeBlocker.Error);
        var finalJob = await AddLater("allowed-system-action.bin", seed: 410);
        var finalOptions = await ctx.Service.UpdateJobAsync(new JobUpdateRequest
        {
            JobId = finalJob.Id,
            CompletionOptions = RestartOptions(),
        });
        Assert.True(finalOptions.Ok, finalOptions.Error);
        Assert.True((await ctx.Service.ResumeAsync(finalJob.Id)).Ok);
        await ctx.WaitFor(finalJob.Id, job => job.State == "completed");
        var shutdown = await executor.ShutdownRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, executor.ShutdownCount);
        Assert.Equal("restart", shutdown.Mode);
        Assert.True(shutdown.Force);
        Assert.Empty(executor.OpenedPaths);
        Assert.Equal(0, executor.ExitCount);

        async Task<JobDto> AddLater(string fileName, int seed)
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/{fileName}?size=32768&seed={seed}",
                DestinationDirectory = ctx.Destination,
                FileName = fileName,
                Source = "cli",
                StartMode = "later",
            });
            Assert.True(response.Accepted, response.Error);
            return response.Job!;
        }

        static JobCompletionOptionsDto RestartOptions() => new()
        {
            ProgramPath = string.Empty,
            SystemAction = "restart",
            ForceSystemAction = true,
        };
    }

    private sealed class MarkerObservingPostDownloadHook : IPostDownloadHook
    {
        private string? _jobPath;
        public List<string> Commands { get; } = new();
        public bool MarkerWasPersisted { get; private set; }
        public TaskCompletionSource<bool> Invoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetJobDocumentPath(string jobPath) => _jobPath = jobPath;

        public async Task RunAsync(string command, string filePath, CancellationToken cancellationToken)
        {
            // Read only the durable boolean needed by this assertion; do not retain or log the job document.
            var json = await File.ReadAllTextAsync(_jobPath!, cancellationToken);
            using var document = JsonDocument.Parse(json);
            MarkerWasPersisted = document.RootElement.GetProperty("completionActionsStarted").GetBoolean();
            Commands.Add(command);
            Invoked.TrySetResult(MarkerWasPersisted);
        }
    }

    private sealed class CompletionActionObserver : ICompletionActionExecutor
    {
        public List<string> OpenedPaths { get; } = new();
        public int ExitCount { get; private set; }
        public int ShutdownCount { get; private set; }
        public TaskCompletionSource<string> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<(string Mode, bool Force)> ShutdownRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OpenFileAsync(string path, CancellationToken cancellationToken)
        {
            OpenedPaths.Add(path);
            Opened.TrySetResult(path);
            return Task.CompletedTask;
        }

        public Task RequestExitAsync(CancellationToken cancellationToken)
        {
            ExitCount++;
            return Task.CompletedTask;
        }

        public Task RequestShutdownAsync(string mode, bool force, CancellationToken cancellationToken)
        {
            ShutdownCount++;
            ShutdownRequested.TrySetResult((mode, force));
            return Task.CompletedTask;
        }
    }
}
