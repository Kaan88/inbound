namespace DownloadManager.Mobile.Updates;

/// <summary>Decides when a staged update must be withdrawn. Only a signature-verified feed answer may call this.</summary>
public static class AndroidUpdateWithdrawal
{
    /// <summary>True when the verified feed offers an older version than the staged one (the release was pulled).</summary>
    public static bool IsPulled(long stagedVersionCode, long verifiedFeedVersionCode) => verifiedFeedVersionCode < stagedVersionCode;
}
