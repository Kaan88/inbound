using Tmds.DBus.Protocol;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The desktop's light or dark preference, read straight from the XDG desktop portal (org.freedesktop.appearance
/// color-scheme: 1 prefers dark, 2 prefers light, 0 no preference), which KDE and GNOME publish. Uno learns it only
/// after the first frames, so the System theme drew light for a moment on a dark desktop at a cold start.
/// Null when there is no portal or no preference.
/// </summary>
internal static class LinuxColorScheme
{
    public static bool? PrefersDark()
    {
        try
        {
            using var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("no session bus"));
            var connect = connection.ConnectAsync().AsTask();
            if (!connect.Wait(TimeSpan.FromMilliseconds(500))) return null;
            var read = connection.CallMethodAsync(Build(connection), static (message, _) =>
            {
                var reader = message.GetBodyReader();
                var value = reader.ReadVariantValue();
                // ReadOne returns the value; the older Read wraps it in one more variant.
                while (value.Type == VariantValueType.Variant) value = value.GetVariantValue();
                return value.Type == VariantValueType.UInt32 ? (uint?)value.GetUInt32() : null;
            }, null);
            if (!read.Wait(TimeSpan.FromMilliseconds(500))) return null;
            return read.Result switch { 1 => true, 2 => false, _ => null };
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or DBusReadException or IOException
                                       or InvalidOperationException or AggregateException)
        {
            return null;
        }
    }

    private static MessageBuffer Build(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.portal.Desktop", path: "/org/freedesktop/portal/desktop",
            @interface: "org.freedesktop.portal.Settings", member: "Read", signature: "ss");
        writer.WriteString("org.freedesktop.appearance");
        writer.WriteString("color-scheme");
        return writer.CreateMessage();
    }
}
