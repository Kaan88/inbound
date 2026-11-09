// Win32Interop from the Windows App SDK, which Uno lacks (Uno has WinRT.Interop.WindowNative). A WindowId carries the
// HWND here, as it does in the Windows App SDK; the shimmed pickers ignore it.
using Microsoft.UI.Xaml;

namespace Microsoft.UI
{
    public static class Win32Interop
    {
        public static WindowId GetWindowIdFromWindow(nint hwnd) => new((ulong)hwnd);
        public static nint GetWindowFromWindowId(WindowId id) => (nint)id.Value;
    }
}
