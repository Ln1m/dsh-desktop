using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// 启动片头层：盖在宿主窗体最上层播一段片头，直到网页渲染出来。
// 运行时目录约定：index.html（播放页）/ config.json（选片与停顿）/ videos/*.mp4（片库）。
public sealed class BootSplashOverlay : IDisposable
{
    public const string DefaultVirtualHost = "splash.local";
    private const string DefaultVideo = "cyberpunk-intro.mp4";

    private readonly string _root;
    private readonly string _host;
    private readonly WebView2 _view;
    private bool _visible;

    public event EventHandler Released;

    public string Root { get { return _root; } }
    public bool Visible { get { return _visible; } }

    public BootSplashOverlay(string root, string virtualHost)
    {
        if (string.IsNullOrEmpty(root)) throw new ArgumentNullException("root");
        _root = root;
        _host = string.IsNullOrEmpty(virtualHost) ? DefaultVirtualHost : virtualHost;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "videos"));
        _view = new WebView2();
        _view.Dock = DockStyle.Fill;
        _view.Visible = true;
        _visible = true;
        try { _view.DefaultBackgroundColor = Color.Black; } catch { }
    }

    /// <summary>把播放页与默认素材摊到 root：页面每次用模板覆盖，配置与片库归用户。</summary>
    public static void Ensure(string root, string templateDir)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "videos"));
        File.Copy(Path.Combine(templateDir, "index.html"), Path.Combine(root, "index.html"), true);
        string cfg = Path.Combine(root, "config.json");
        if (!File.Exists(cfg))
        {
            File.WriteAllText(cfg,
                "{\r\n  \"video\": \"" + DefaultVideo + "\",\r\n  \"gapMs\": 3000\r\n}\r\n",
                new System.Text.UTF8Encoding(false));
        }
        string media = Path.Combine(root, "videos", DefaultVideo);
        if (!File.Exists(media))
        {
            File.Copy(Path.Combine(templateDir, DefaultVideo), media, true);
        }
    }

    public void Attach(Form host)
    {
        host.Controls.Add(_view);
        _view.BringToFront();
    }

    /// <summary>与其他 WebView2 共用同一个 Environment，避免多起一个浏览器进程。</summary>
    public async System.Threading.Tasks.Task StartAsync(CoreWebView2Environment env)
    {
        await _view.EnsureCoreWebView2Async(env);
        _view.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _view.CoreWebView2.WebMessageReceived += OnMessage;
        _view.CoreWebView2.SetVirtualHostNameToFolderMapping(
            _host, _root, CoreWebView2HostResourceAccessKind.Allow);
        _view.CoreWebView2.Navigate("http://" + _host + "/index.html");
    }

    /// <summary>宿主页面已经能操作了：露出「跳过」，这一遍播完或用户点跳过才撤。</summary>
    public void ReleaseToUser()
    {
        if (!_visible) return;
        if (_view.CoreWebView2 == null) { Hide(); return; }
        try
        {
            _view.CoreWebView2.ExecuteScriptAsync("window.__splashReady&&window.__splashReady()");
        }
        catch
        {
            Hide();
        }
    }

    public void Hide()
    {
        if (!_visible) return;
        _visible = false;
        try { _view.Visible = false; } catch { }
        EventHandler h = Released;
        if (h != null) h(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        try { _view.Dispose(); } catch { }
    }

    private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.WebMessageAsJson; } catch { return; }
        if (json == null) return;
        if (json.IndexOf("splash-skip") >= 0 || json.IndexOf("splash-ended") >= 0) Hide();
    }
}
