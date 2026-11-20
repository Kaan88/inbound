using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using DownloadManager.Mobile.Links;

namespace Inbound.App.Screens;

/// <summary>Phone-local browsing. Only an explicit capture leaves this WebView for the selected Host.</summary>
internal sealed class BrowserScreen : IDisposable
{
    private readonly MainActivity _activity;
    private readonly Action<CapturedDownload> _caught;
    private readonly WebView _web;
    private readonly EditText _address;
    private readonly FrameLayout _body;
    private readonly bool _refresh;
    private string? _verifiedSecurePage;
    private PopupMenu? _options;
    private readonly BrowserRequests _requests = new();
    private CancellationTokenSource? _selectionLifetime;
    private readonly List<View> _chrome = [];
    private readonly Dictionary<string, CapturedDownload> _batchCaptures = new(StringComparer.Ordinal);
    private PageLinksScreen? _links;
    private string? _page;
    private int _navigation;
    private bool _disposed, _collecting;

    public BrowserScreen(MainActivity activity, string? initial, Action<CapturedDownload> caught, bool refresh = false)
    {
        _activity = activity; _caught = caught; _refresh = refresh;
        var root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(new Color(Motion.Attr(activity, Resource.Attribute.colorSurface)));
        Root = root;

        var top = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        var close = new ImageButton(activity) { ContentDescription = "Close browser" };
        close.SetImageResource(Resource.Drawable.ic_close);
        close.SetBackgroundColor(Color.Transparent);
        close.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(Motion.Attr(activity, Resource.Attribute.colorOnSurface)));
        close.Click += (_, _) => { if (!_disposed) activity.CloseRowPage(Root); };
        close.LayoutParameters = new LinearLayout.LayoutParams(Motion.Dp(activity, 48), Motion.Dp(activity, 48));
        top.SetPadding(Motion.Dp(activity, 4), Motion.Dp(activity, 8), Motion.Dp(activity, 8), Motion.Dp(activity, 8));
        top.SetGravity(GravityFlags.CenterVertical);
        top.AddView(close);
        _address = new EditText(activity) { Hint = "https://example.com",
            InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri,
            ImeOptions = Android.Views.InputMethods.ImeAction.Go, SaveEnabled = false };
        _address.TextSize = 16;
        var addressBackground = new Android.Graphics.Drawables.GradientDrawable();
        addressBackground.SetColor(Motion.Attr(activity, Resource.Attribute.colorSurfaceContainerHighest));
        addressBackground.SetCornerRadius(Motion.Dp(activity, 28));
        _address.Background = addressBackground;
        _address.SetPadding(Motion.Dp(activity, 16), Motion.Dp(activity, 8), Motion.Dp(activity, 16), Motion.Dp(activity, 8));
        _address.SetSingleLine(true);
        _address.SetSelectAllOnFocus(true);
        top.AddView(_address, new LinearLayout.LayoutParams(0, Motion.Dp(activity, 48), 1));
        var more = new ImageButton(activity) { ContentDescription = "Browser options" };
        more.SetImageResource(Resource.Drawable.ic_more);
        more.SetBackgroundColor(Color.Transparent);
        more.ImageTintList = close.ImageTintList;
        more.Click += (_, _) => ShowOptions(more);
        top.AddView(more, new LinearLayout.LayoutParams(Motion.Dp(activity, 48), Motion.Dp(activity, 48)));
        _address.TextChanged += (_, _) => DrawAddressLock();
        _address.EditorAction += (_, e) => { if (e.ActionId == Android.Views.InputMethods.ImeAction.Go) { Navigate(); e.Handled = true; } };
        root.AddView(top); _chrome.Add(top);
        InsetsListener.Pad(top, top: true, bottom: false);
        if (refresh)
        {
            var note = new TextView(activity)
            {
                Text = "Catch a replacement link for the existing download. The Host checks whether it can resume.",
                TextSize = 12
            };
            note.SetPadding(Motion.Dp(activity, 16), 0, Motion.Dp(activity, 16), Motion.Dp(activity, 8));
            root.AddView(note); _chrome.Add(note);
        }
        _body = new FrameLayout(activity);
        root.AddView(_body, new LinearLayout.LayoutParams(-1, 0, 1));
        _web = new WebView(activity) { SaveEnabled = false };
        var settings = _web.Settings;
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = true;
        settings.AllowFileAccess = false;
        settings.AllowContentAccess = false;
        settings.JavaScriptCanOpenWindowsAutomatically = false;
        settings.SetSupportMultipleWindows(false);
        settings.MixedContentMode = MixedContentHandling.NeverAllow;
        settings.SafeBrowsingEnabled = true;
        settings.SetGeolocationEnabled(false);
        CookieManager.Instance!.SetAcceptThirdPartyCookies(_web, false);
        _web.SetWebViewClient(new Client(this));
        _web.SetWebChromeClient(new WebChromeClient()); // default denies web permission/geolocation requests
        _web.SetDownloadListener(new CatchListener(this));
        var viewport = new FrameLayout(activity);
        viewport.AddView(_web, new FrameLayout.LayoutParams(-1, -1));
        _body.AddView(viewport, new FrameLayout.LayoutParams(-1, -1));
        InsetsListener.Pad(viewport, top: false, bottom: true);
        _address.Text = initial ?? "";
        if (initial is not null) Navigate();
    }

    public View Root { get; }
    public bool HandlesBack => _links is not null || _web.CanGoBack();
    public bool Back()
    {
        if (_disposed) return false;
        if (_links is not null) { CloseLinks(); return true; }
        if (_web.CanGoBack()) { _web.GoBack(); return true; }
        return false;
    }
    public void Pause() => _web.OnPause();
    public void Resume() => _web.OnResume();

    private void ShowOptions(View anchor)
    {
        if (_disposed || _options is not null) return;
        var menu = new PopupMenu(_activity, anchor);
        _options = menu;
        menu.Menu!.Add(0, 1, 0, "Go");
        if (!_refresh) menu.Menu.Add(0, 2, 1, "Links on page");
        menu.Menu.Add(0, 3, 2, "Open outside");
        menu.MenuItemClick += (_, e) =>
        {
            if (_disposed) return;
            switch (e.Item?.ItemId)
            {
                case 1: Navigate(); break;
                case 2: Collect(); break;
                case 3: OpenOutside(); break;
            }
        };
        menu.DismissEvent += (_, _) => { if (ReferenceEquals(_options, menu)) _options = null; };
        menu.Show();
    }

    private void DrawAddressLock()
    {
        var secure = _verifiedSecurePage is not null && _address.Text == _verifiedSecurePage;
        _address.SetCompoundDrawablesRelativeWithIntrinsicBounds(secure ? Resource.Drawable.ic_browser_lock : 0, 0, 0, 0);
        _address.CompoundDrawablePadding = Motion.Dp(_activity, 10);
    }

    private void ClearAddressLock()
    {
        _verifiedSecurePage = null;
        DrawAddressLock();
    }
    internal static string? Address(string? value)
    {
        try { return new CapturedDownload(value ?? "").Url; }
        catch (ArgumentException) { return null; }
    }
    private void Navigate()
    {
        if (Address(_address.Text?.Trim()) is not { } url) { _activity.ShowMessage("Enter a complete HTTP or HTTPS address."); return; }
        ClearAddressLock();
        CloseLinks(); _web.LoadUrl(url);
        ((Android.Views.InputMethods.InputMethodManager)_activity.GetSystemService(Context.InputMethodService)!).HideSoftInputFromWindow(_address.WindowToken, 0);
        _web.RequestFocus();
    }
    private void OpenOutside()
    {
        if (Address(_web.Url) is not { } url) return;
        try { _activity.StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(url))); }
        catch (ActivityNotFoundException) { _activity.ShowMessage("No browser is available."); }
    }
    private void Started(string? url)
    {
        _navigation++; CloseLinks();
        ClearAddressLock();
        if (Address(url) is { } valid) _address.Text = valid;
    }
    private void Committed(string? url)
    {
        _navigation++; CloseLinks();
        _page = Address(url);
        _requests.CommitPage(_page);
        _verifiedSecurePage = Uri.TryCreate(_page, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && _web.Url == _page && _web.Certificate is not null ? _page : null;
        if (_page is not null) _address.Text = _page;
        DrawAddressLock();
    }
    private CapturedDownload Capture(string url, string? name = null, string? type = null, long? size = null, string? ua = null, bool fromDom = false, string? sourcePage = null)
    {
        var evidence = fromDom ? null : _requests.Take(url);
        if (!fromDom && (evidence is null || evidence.KnownNonGet || evidence.Ambiguous)) throw new ArgumentException("This request cannot be safely replayed.");
        var proven = fromDom || evidence is { Ambiguous: false };
        var page = fromDom ? sourcePage : proven ? evidence?.PageUrl : null;
        // Unknown/ambiguous request provenance transfers no browser credentials. An observed missing Referer stays missing.
        var cookie = proven && CapturedDownload.SameOrigin(url, page) ? CookieManager.Instance?.GetCookie(url) : null;
        return new CapturedDownload(url, name, type, size, page, ua ?? _web.Settings.UserAgentString, cookie, proven ? evidence?.Referrer : null);
    }
    private void Download(string? url, string? ua, string? disposition, string? mime, long length)
    {
        if (_disposed) return;
        if (Address(url) is not { } valid) { _activity.ShowMessage("This download needs a regular browser. Only HTTP and HTTPS files can be caught."); return; }
        try { _caught(Capture(valid, URLUtil.GuessFileName(valid, disposition, mime), mime, length >= 0 ? length : null, ua)); }
        catch (ArgumentException) { _activity.ShowMessage("This browser request cannot be safely captured."); }
    }
    private void Collect()
    {
        if (_collecting || _links is not null || _page is null) return;
        _collecting = true;
        var generation = _navigation;
        var sourcePage = _page;
        _web.EvaluateJavascript(CollectScript, new JsResult(value =>
        {
            _collecting = false;
            if (_disposed || generation != _navigation || sourcePage != _page || _page is null) return;
            try
            {
                var model = PageLinkResults.Parse(value);
                if (model.TotalCount == 0) { _activity.ShowMessage("No HTTP or HTTPS links found on this page."); return; }
                _selectionLifetime = new CancellationTokenSource();
                var selectionToken = _selectionLifetime.Token;
                _links = new PageLinksScreen(_activity, model, new Uri(_page).IdnHost, _activity.LinkDestination,
                    async selected =>
                    {
                        if (selectionToken.IsCancellationRequested || generation != _navigation || sourcePage != _page) return false;
                        var captures = new List<CapturedDownload>();
                        foreach (var link in selected)
                        {
                            if (!_batchCaptures.TryGetValue(link.Url, out var capture)) _batchCaptures[link.Url] = capture = Capture(link.Url, fromDom: true, sourcePage: sourcePage);
                            captures.Add(capture);
                        }
                        return await _activity.AddPageLinksAsync(captures, selectionToken);
                    }, CloseLinks);
                _body.AddView(_links.Root, new FrameLayout.LayoutParams(-1, -1));
                _web.Visibility = ViewStates.Gone;
                foreach (var chrome in _chrome) chrome.Visibility = ViewStates.Gone;
                AndroidX.Core.View.ViewCompat.RequestApplyInsets(Root);
                AndroidX.Core.View.ViewCompat.RequestApplyInsets(_links.Root);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            { _activity.ShowMessage("Could not read links from this page."); }
        }));
    }
    private void CloseLinks()
    {
        _selectionLifetime?.Cancel(); _selectionLifetime = null;
        _batchCaptures.Clear();
        if (_links is null) return;
        _body.RemoveView(_links.Root); _links.Dispose(); _links = null;
        _web.Visibility = ViewStates.Visible;
        foreach (var chrome in _chrome) chrome.Visibility = ViewStates.Visible;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _options?.Dismiss();
        CloseLinks(); _batchCaptures.Clear(); _requests.CommitPage(null);
        _web.StopLoading(); _web.SetDownloadListener(null); _web.SetWebViewClient(new WebViewClient());
        _body.RemoveView(_web); _web.Destroy(); _web.Dispose();
    }
    private sealed class Client(BrowserScreen owner) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            if (Address(request?.Url?.ToString()) is not null) return false;
            owner._activity.ShowMessage("This link needs a regular browser."); return true;
        }
        public override void OnPageStarted(WebView? view, string? url, Bitmap? favicon) => owner.Started(url);
        public override void OnPageCommitVisible(WebView? view, string? url) => owner.Committed(url);
        public override void OnReceivedSslError(WebView? view, SslErrorHandler? handler, Android.Net.Http.SslError? error)
        { handler?.Cancel(); owner.ClearAddressLock(); owner._activity.ShowMessage("The site’s secure connection could not be verified."); }
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            if (Address(request?.Url?.ToString()) is not { } url)
                return new WebResourceResponse("text/plain", "UTF-8", new MemoryStream());
            string? referrer = null;
            if (request?.RequestHeaders is { } headers)
                foreach (var pair in headers)
                    if (pair.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase) && pair.Value.Length <= 8192) referrer = pair.Value;
            owner._requests.Observe(url, request?.Method, request?.IsForMainFrame == true, referrer);
            return null;
        }
    }
    private sealed class CatchListener(BrowserScreen owner) : Java.Lang.Object, IDownloadListener
    {
        public void OnDownloadStart(string? url, string? userAgent, string? contentDisposition, string? mimetype, long contentLength) =>
            owner._activity.RunOnUiThread(() => owner.Download(url, userAgent, contentDisposition, mimetype, contentLength));
    }
    private sealed class JsResult(Action<string?> result) : Java.Lang.Object, IValueCallback
    { public void OnReceiveValue(Java.Lang.Object? value) => result(value?.ToString()); }
    // Fixed script, no user/URL interpolation and no native JavaScript interface.
    private const string CollectScript = """
        (() => { const out=[]; let n=0; for(const a of document.querySelectorAll('a[href]')) {
          if(out.length>=1000) break; const u=a.href; if(!/^https?:\/\//i.test(u)||u.length>8192) continue;
          const name=(a.textContent||'').trim().slice(0,200); n+=u.length+name.length;
          if(n>262144) break;
          const raw=a.getAttribute('data-size-bytes');
          const size=raw && /^\d{1,16}$/.test(raw) ? Number(raw) : null;
          out.push({url:u,name:name,bytes:Number.isSafeInteger(size) ? size : null}); } return JSON.stringify(out); })()
        """;
}
