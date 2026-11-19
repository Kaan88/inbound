using Android.App.Job;
using Android.OS;

namespace Inbound.App.Background;

// Start/stop arguments are separate Parcelable objects. Compare the scheduled lineage,
// not Java/managed object identity; rescheduling the same numeric ID gets a new token.
internal sealed record JobRunIdentity(int JobId, string? ScheduleToken)
{
    private const string TokenKey = "inbound_schedule_token";
    public static JobRunIdentity From(JobParameters parameters) => new(parameters.JobId, parameters.Extras?.GetString(TokenKey));
    public static PersistableBundle NewExtras()
    {
        var extras = new PersistableBundle(); extras.PutString(TokenKey, Guid.NewGuid().ToString("N")); return extras;
    }
}
