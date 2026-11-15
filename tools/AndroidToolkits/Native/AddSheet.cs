using Android.OS;
using Android.Runtime;
using Android.Views;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.TextField;

namespace AndroidTrial.Native;

/// <summary>The Add sheet (mockup A4): a modal Material bottom sheet with a link field and an Add button. Scrim and motion are the standard M3 ones.</summary>
[Register("app.inbound.tk.nativeui.AddSheet")]
public sealed class AddSheet : BottomSheetDialogFragment
{
    /// <summary>Set by the activity before Show; called with the entered link.</summary>
    public static Action<string>? OnAdd;

    public AddSheet() { }

    private AddSheet(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var v = inflater.Inflate(Resource.Layout.add_sheet, container, false)!;
        var url = v.FindViewById<TextInputEditText>(Resource.Id.url)!;
        var add = v.FindViewById<MaterialButton>(Resource.Id.add_btn)!;
        url.TextChanged += (_, _) => add.Enabled = !string.IsNullOrWhiteSpace(url.Text);
        void Submit()
        {
            var text = url.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            OnAdd?.Invoke(text);
            Dismiss();
        }

        add.Click += (_, _) => Submit();
        url.EditorAction += (_, e) =>
        {
            if (e.ActionId == Android.Views.InputMethods.ImeAction.Done) { Submit(); e.Handled = true; }
        };
        return v;
    }
}
