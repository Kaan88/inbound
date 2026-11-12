using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The Scheduler's Files tab (audit M2): the list follows the queue's members as the Host reports them. A list with
/// nothing staged is rebuilt from the Host's order; one with a staged reorder or move keeps the user's order, drops
/// downloads that left the queue and adds the ones that joined it at the end, so its Apply still matches the queue.
/// </summary>
public static class SchedulerFileRules
{
    /// <summary>The downloads waiting in <paramref name="queue"/>, by the jobs the Host reported.</summary>
    public static JobDto[] Members(IEnumerable<JobDto> jobs, int queueId)
        => jobs.Where(job => job.IsQueued && job.QueueId == queueId).ToArray();

    /// <summary>The Host's order: higher priority first, then the queue's saved order, then oldest first.</summary>
    public static List<Guid> HostOrder(IEnumerable<JobDto> members, IReadOnlyList<Guid> jobOrder)
        => members.OrderByDescending(job => job.Priority)
            .ThenBy(job => { var index = IndexOf(jobOrder, job.Id); return index < 0 ? int.MaxValue : index; })
            .ThenBy(job => job.CreatedAt)
            .Select(job => job.Id)
            .ToList();

    /// <summary>
    /// A staged order brought up to date: the user's order for the downloads still in the queue, then the ones that
    /// joined since, in the Host's order. Downloads staged to leave (<paramref name="stagedAway"/>) are left out.
    /// </summary>
    public static List<Guid> MergeOrder(IReadOnlyList<Guid> stagedOrder, IReadOnlyList<JobDto> members,
        IReadOnlyList<Guid> jobOrder, IReadOnlyCollection<Guid> stagedAway)
    {
        var current = members.Where(job => !stagedAway.Contains(job.Id)).ToArray();
        var ids = current.Select(job => job.Id).ToHashSet();
        var merged = stagedOrder.Where(ids.Contains).Distinct().ToList();
        var kept = merged.ToHashSet();
        merged.AddRange(HostOrder(current.Where(job => !kept.Contains(job.Id)), jobOrder));
        return merged;
    }

    /// <summary>True when the list shown is not the queue's members (less any staged to leave), so it must be loaded again.</summary>
    public static bool ListIsStale(IEnumerable<Guid> shown, IReadOnlyList<JobDto> members, IReadOnlyCollection<Guid> stagedAway)
        => !shown.ToHashSet().SetEquals(members.Where(job => !stagedAway.Contains(job.Id)).Select(job => job.Id));

    /// <summary>
    /// Audit M2 (review): the staged order to send on Apply, brought up to date with the queue's members as the Host
    /// has them now. Only the queue on screen is merged as events come, so a reorder staged on another queue would
    /// otherwise no longer match once a download joined or left it, and Apply refused it.
    /// </summary>
    public static List<Guid> OrderToApply(IReadOnlyList<Guid> stagedOrder, IEnumerable<JobDto> latestJobs, QueueDto queue,
        IReadOnlyCollection<Guid> stagedAway)
        => MergeOrder(stagedOrder, Members(latestJobs, queue.Id), queue.JobOrder, stagedAway);

    /// <summary>
    /// Audit M11 (review): job events that came while a <c>jobs.query</c> was out, put onto its reply. The reply may
    /// have been built before them, and taking it whole would undo them. An event older than the reply's own copy of
    /// that download is left out; a removal always applies.
    /// </summary>
    public static void ReplayJobEvents(List<JobDto> jobs, IEnumerable<(JobDto Job, string ChangeType)> events)
    {
        foreach (var (job, changeType) in events)
        {
            var index = jobs.FindIndex(item => item.Id == job.Id);
            if (changeType == "removed")
            {
                if (index >= 0) jobs.RemoveAt(index);
            }
            else if (index < 0) jobs.Add(job);
            else if (job.UpdatedAt >= jobs[index].UpdatedAt) jobs[index] = job;
        }
    }

    private static int IndexOf(IReadOnlyList<Guid> list, Guid id)
    {
        for (var index = 0; index < list.Count; index++)
            if (list[index] == id) return index;
        return -1;
    }
}
