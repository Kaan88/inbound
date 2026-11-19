using Android.Content;

namespace Inbound.App.Screens;

/// <summary>Resolves the current theme's touch and keyboard feedback for programmatic rows.</summary>
internal sealed class TypedValueHolder(Context context)
{
    public Android.Graphics.Drawables.Drawable? SelectableBackground()
    {
        using var value = new Android.Util.TypedValue();
        return context.Theme!.ResolveAttribute(Android.Resource.Attribute.SelectableItemBackground, value, true)
            ? context.GetDrawable(value.ResourceId) : null;
    }
}
