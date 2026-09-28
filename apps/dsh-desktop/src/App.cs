// DeepSeek Harness Desktop App (WebView2 wrapper)
// Loads the local DSH Web GUI (http://127.0.0.1:3080) in a standalone window.
// Starts the `dsh web` server automatically when it is not running.
// Built with .NET Framework (csc) + Microsoft.Web.WebView2.
//
// Sizing is done in PHYSICAL pixels (the app is PerMonitorV2-aware). The default
// window size is a fraction of the working area so it looks right on any DPI.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshDesktop
{
    internal static class Program
    {
        private const string Host = "127.0.0.1";
        private const int Port = 3080;
        private const string Url = "http://127.0.0.1:3080/";

        // DSH 安装根：默认 %USERPROFILE%\DeepSeek_harness，可用环境变量 DSH_ROOT 覆盖。
        private static readonly string Root =
            Environment.GetEnvironmentVariable("DSH_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeepSeek_harness");
        private static readonly string DshCmd = Root + "\\node_modules\\.bin\\dsh.cmd";
        private static readonly string DshWorkDir = Root + "";
        private static readonly string IconPath = Root + "\\assets\\deepseek_harness.ico";
        // 系统托盘守护（DSH-Tray.exe）。2026-09-12：窗口一启动就在 Main 里确保它挂起来，
        // 关窗时再兜底一次。托盘在 = 3080 引擎与 3081 手机反代有守护，关窗只是一次普通关闭。
        private static readonly string TrayExe = Root + "\\dsh-tray\\DSH-Tray.exe";
        private static readonly string TrayWorkDir = Root + "\\dsh-tray";
        private static readonly string ChangliaoIconPath = Root + "\\assets\\changliao.ico";
        private const string MutexName = "DshDesktop_SingleInstance_3080";
        private const int ProxyPort = 3081;
        private static readonly string ProxyScript = Root + "\\scripts\\dsh-wifi-proxy.js";
        private const int SwRestore = 9;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        // 关闭询问窗：无边框拖动 + Win11 圆角/投影
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 2;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [STAThread]
        private static int Main()
        {
            bool createdNew;
            using (Mutex m = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    if (ActivateExistingInstance())
                    {
                        return 0;
                    }
                    for (int i = 0; i < 10; i++)
                    {
                        Thread.Sleep(300);
                        if (ActivateExistingInstance())
                        {
                            return 0;
                        }
                    }
                }
                try
                {
                    // PER_MONITOR_AWARE_II = -4: crisp rendering on high-DPI displays
                    SetProcessDpiAwarenessContext(new IntPtr(-4));
                }
                catch
                {
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // 打开窗口就把托盘守护挂起来，而不是等用户点 X 才拉起。
                // 这样「托盘在 = 服务保持挂起」从窗口打开那一刻就成立，关窗不再伴随托盘进程的突然出现。
                EnsureTrayRunning();

                Application.Run(new MainForm());
            }
            return 0;
        }

        /// <summary>Bring an already running instance to the foreground. Returns true when one was found.</summary>
        private static bool ActivateExistingInstance()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("dsh-desktop"))
                {
                    if (p.Id == Process.GetCurrentProcess().Id)
                    {
                        continue;
                    }
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindowAsync(p.MainWindowHandle, SwRestore);
                        SetForegroundWindow(p.MainWindowHandle);
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool PortOpen()
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    c.Connect(Host, Port);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolve the entry URL for the WebView: prefer the process token that the most recently
        /// started dsh printed (dsh web: http://127.0.0.1:3080/?token=...), because hitting that URL
        /// makes the host mint the 30-day session cookie. Fall back to the bare URL when no token
        /// can be read - an existing cookie still authenticates in that case.
        /// </summary>
        private static string ResolveWebUrl()
        {
            try
            {
                string[] candidates = new string[] {
                    Root + "\\logs\\dsh-web.log",
                    Root + "\\dsh-tray\\logs\\web.log"
                };
                string bestToken = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (string f in candidates)
                {
                    if (!File.Exists(f)) continue;
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (t <= bestTime) continue;
                    // The launcher keeps its stdout log open for writing, so a plain
                    // File.ReadAllText (FileShare.Read) is refused. Open with FileShare.ReadWrite.
                    string text;
                    try
                    {
                        using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (StreamReader sr = new StreamReader(fs))
                        {
                            text = sr.ReadToEnd();
                        }
                    }
                    catch
                    {
                        continue;
                    }
                    int idx = text.LastIndexOf("token=", StringComparison.Ordinal);
                    if (idx < 0) continue;
                    int end = idx + 6;
                    while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_' || text[end] == '-')) end++;
                    string tok = text.Substring(idx + 6, end - idx - 6);
                    if (tok.Length == 0) continue;
                    bestToken = tok;
                    bestTime = t;
                }
                if (!string.IsNullOrEmpty(bestToken))
                {
                    LogResolve("token resolved from launch log (" + bestToken.Substring(0, Math.Min(8, bestToken.Length)) + "...)");
                    return Url + "?token=" + bestToken;
                }
                LogResolve("no token in the launch logs; opening the bare URL (cookie only)");
            }
            catch (Exception ex)
            {
                LogResolve("resolve failed: " + ex.Message);
            }
            return Url;
        }

        /// <summary>Append one diagnostic line (never the whole token) so a future 401 stays traceable.</summary>
        private static void LogResolve(string message)
        {
            try
            {
                File.AppendAllText(Root + "\\logs\\dsh-desktop.log",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch
            {
            }
        }

        // ---- engine token traceability (fix 2026-09-11) -------------------------------
        // Symptom: the window sits on "connecting / load failed, retrying" forever and the
        // page never opens.
        // Root cause: every `dsh web` start mints a NEW random token and prints it exactly
        // once ("dsh web: http://127.0.0.1:3080/?token=..."). The old StartServer() launched
        // the engine with Process.Start and no stdout redirect, so that banner was thrown
        // away and ResolveWebUrl() could only read the PREVIOUS launch's token -> HTTP 401
        // -> endless retry loop.
        // Fix: (1) StartServer() appends the engine stdout/stderr to logs\dsh-web.log (the
        // first candidate ResolveWebUrl() reads), (2) start with --no-open so a background
        // start does not pop the default browser, (3) if the 3080 listener is younger than
        // the newest token log, its token is untraceable: take the engine over and restart.
        private static readonly string WebLogPath = Root + "\\logs\\dsh-web.log";
        private static readonly string WebErrLogPath = Root + "\\logs\\dsh-web.err.log";
        private static Process _engineProcess;

        private static void AppendEngineLog(string path, string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }

        /// <summary>PID listening on 3080; -1 when unknown. netstat keeps us off NetTCPIP.</summary>
        private static int ListenerPid()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "netstat.exe";
                psi.Arguments = "-ano -p tcp";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(4000);
                    if (!string.IsNullOrEmpty(output))
                    {
                        foreach (string raw in output.Split('\n'))
                        {
                            string line = raw.Trim();
                            if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (line.IndexOf(":3080 ", StringComparison.Ordinal) < 0) continue;
                            string[] parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            int pid;
                            if (parts.Length >= 5 && int.TryParse(parts[parts.Length - 1], out pid) && pid > 0) return pid;
                        }
                    }
                }
            }
            catch
            {
            }
            return -1;
        }

        /// <summary>Start time (UTC) of the process listening on 3080; MinValue when unknown.</summary>
        private static DateTime ListenerStartUtc()
        {
            try
            {
                int pid = ListenerPid();
                if (pid > 0)
                {
                    using (Process p = Process.GetProcessById(pid))
                    {
                        return p.StartTime.ToUniversalTime();
                    }
                }
            }
            catch
            {
            }
            return DateTime.MinValue;
        }

        /// <summary>Newest write time (UTC) among the token logs ResolveWebUrl() reads.</summary>
        private static DateTime NewestTokenLogUtc()
        {
            DateTime newest = DateTime.MinValue;
            string[] candidates = new string[] {
                Root + "\\logs\\dsh-web.log",
                Root + "\\dsh-tray\\logs\\web.log"
            };
            foreach (string f in candidates)
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (t > newest) newest = t;
                }
                catch
                {
                }
            }
            return newest;
        }

        /// <summary>
        /// True when the running 3080 engine came up after the newest token log was written,
        /// i.e. no log holds its token. Navigating then can only 401, so we take it over.
        /// </summary>
        private static bool EngineRestartedWithoutLog()
        {
            if (!PortOpen()) return false;
            DateTime start = ListenerStartUtc();
            if (start == DateTime.MinValue) return false;    // cannot tell -> leave it alone
            DateTime logged = NewestTokenLogUtc();
            if (logged == DateTime.MinValue) return true;    // never logged a token
            return logged < start.AddSeconds(-5);            // banner lands within ms of spawn
        }

        /// <summary>Replace an untraceable engine with one started (and logged) by this app.</summary>
        private static void RestartServerOwned()
        {
            try
            {
                int pid = ListenerPid();
                if (pid > 0)
                {
                    AppendEngineLog(WebLogPath, "[desktop] 3080 pid " + pid + " has no traceable token; restarting under desktop control");
                    try { Process.GetProcessById(pid).Kill(); } catch { }
                    for (int i = 0; i < 40 && PortOpen(); i++) Thread.Sleep(250);
                }
            }
            catch
            {
            }
            StartServer();
        }

        private static void StartServer()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c \"" + DshCmd + "\" web --host 127.0.0.1 --no-open";
                psi.WorkingDirectory = DshWorkDir;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                // Keep the launch banner (with this run's token) on disk where ResolveWebUrl()
                // looks for it; without this the window can never authenticate.
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                Process p = new Process();
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { AppendEngineLog(WebLogPath, e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { AppendEngineLog(WebErrLogPath, e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _engineProcess = p;   // keep a root so the async readers stay alive
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动 DeepSeek Harness 服务失败：\n" + ex.Message,
                    "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>Whether the WiFi proxy (dsh-wifi-proxy.js on 3081) is already listening.</summary>
        private static bool ProxyOpen()
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    c.Connect(Host, ProxyPort);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Start the WiFi proxy so phones on the same LAN can reach DSH.</summary>
        private static void StartProxy()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "node.exe";
                psi.Arguments = "\"" + ProxyScript + "\"";
                psi.WorkingDirectory = Root + "\\scripts";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch
            {
            }
        }

        /// <summary>Whether the system tray guard (DSH-Tray.exe) is already running.</summary>
        private static bool TrayRunning()
        {
            try
            {
                return Process.GetProcessesByName("DSH-Tray").Length > 0;
            }
            catch
            {
                return true; // 查不到就当作在运行，避免在异常环境里反复拉起
            }
        }

        /// <summary>
        /// 微信式驻留：主窗口关闭时，若系统托盘守护不在运行，静默将其拉起。
        /// 之后 DSH 引擎继续由托盘守护（引擎若停，托盘会自动拉起）。
        /// </summary>
        private static void EnsureTrayRunning()
        {
            try
            {
                if (TrayRunning()) return;
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = TrayExe;
                psi.WorkingDirectory = TrayWorkDir;
                psi.UseShellExecute = true; // GUI 子系统程序，无控制台窗口
                Process.Start(psi);
            }
            catch
            {
            }
        }

        /// <summary>退出系统托盘守护进程，防止它在彻底关闭后把服务再拉起来。</summary>
        private static void KillTray()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("DSH-Tray"))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                }
            }
            catch { }
        }

        /// <summary>按监听端口杀进程：3080=DSH 引擎，3081=WiFi 反代（兜底脚本独立进程时也要停）。</summary>
        private static void KillPortListeners()
        {
            try
            {
                int[] ports = { 3080, 3081 };
                HashSet<int> pids = new HashSet<int>();
                Process np = new Process();
                np.StartInfo.FileName = "netstat.exe";
                np.StartInfo.Arguments = "-ano -p tcp";
                np.StartInfo.UseShellExecute = false;
                np.StartInfo.CreateNoWindow = true;
                np.StartInfo.RedirectStandardOutput = true;
                np.Start();
                string outp = np.StandardOutput.ReadToEnd();
                np.WaitForExit();
                foreach (string line in outp.Split('\n'))
                {
                    if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5) continue;
                    string local = parts[1];
                    bool hit = false;
                    foreach (int p in ports)
                    {
                        if (local.EndsWith(":" + p.ToString())) { hit = true; break; }
                    }
                    if (!hit) continue;
                    int pid;
                    if (int.TryParse(parts[parts.Length - 1], out pid)
                        && pid != Process.GetCurrentProcess().Id)
                    {
                        pids.Add(pid);
                    }
                }
                foreach (int pid in pids)
                {
                    try { using (Process tp = Process.GetProcessById(pid)) { tp.Kill(); } } catch { }
                }
            }
            catch { }
        }

        /// <summary>彻底关闭：页面退出前，先停托盘守护与后台引擎(3080)/WiFi 反代(3081)。</summary>
        private static void FullShutdown()
        {
            KillTray();
            KillPortListeners();
        }

        /// <summary>Open a URL in a new in-app WebView2 window (keeps DeepSeek platform pages inside DSH).</summary>
        private static ChildForm openChild;

        private static void OpenChildWindow(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return;
            // 单例：已有打开的内嵌窗口则复用并聚焦，不重复开窗
            if (openChild != null && !openChild.IsDisposed)
            {
                openChild.NavigateTo(uri);
                openChild.Activate();
                return;
            }
            var form = new ChildForm(uri);
            form.FormClosed += (s, e) => { openChild = null; };
            openChild = form;
            form.Show();
        }

        private sealed class ChildForm : Form
        {
            private readonly WebView2 web;
            private readonly bool persistent;
            private string targetUri;
            private bool ready;
            private bool reused;

            public ChildForm(string uri)
            {
                this.targetUri = uri;
                this.persistent = uri != null && uri.Contains("?pm="); // 畅聊独立窗口：失焦不自动关闭
                AutoScaleMode = AutoScaleMode.None;
                StartPosition = FormStartPosition.CenterScreen;
                if (persistent)
                {
                    // 畅聊独立窗口：可拖动、可调整大小（独立窗口形式）
                    Size = new Size(1920, 1080);   // 16:9，大尺寸
                    MinimumSize = new Size(960, 540);   // 16:9
                    FormBorderStyle = FormBorderStyle.Sizable;
                    ShowInTaskbar = true;
                    Text = "畅聊";
                    try { Icon = new Icon(ChangliaoIconPath); } catch { }
                }
                else
                {
                    // 其它内化页面（充值/用量/API Key）：无边框弹层，失焦自动关闭
                    Size = new Size(1620, 911);
                    MinimumSize = new Size(960, 540);
                    FormBorderStyle = FormBorderStyle.None;
                    ShowInTaskbar = false;
                }

                web = new WebView2();
                web.Dock = DockStyle.Fill;
                Controls.Add(web);

                Shown += async (s, e) =>
                {
                    ready = true;
                    try
                    {
                        await web.EnsureCoreWebView2Async(null);
                        // 在 document 创建时（React 渲染前）注入 CSS，隐藏导航与侧边栏，避免两栏→一栏闪烁
                        await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
(function(){
  function inject(){
    try{
      var s = document.getElementById('dsh-hide-chrome');
      if(!s){
        s = document.createElement('style');
        s.id = 'dsh-hide-chrome';
        s.textContent = 'header,nav,aside,footer{display:none!important}[class*=Sidebar],[class*=sidebar],[class*=Sider],[class*=sider],[class*=Navbar],[class*=navbar],[class*=TopNav],[class*=topnav]{display:none!important}';
        (document.head || document.documentElement).appendChild(s);
      }
    }catch(e){}
  }
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', inject);
  } else {
    inject();
  }
})();
");
                        web.CoreWebView2.NewWindowRequested += (sender2, args2) =>
                        {
                            args2.Handled = true;
                            OpenChildWindow(args2.Uri);
                        };
                        web.CoreWebView2.WindowCloseRequested += (sender2, args2) =>
                        {
                            Close();
                        };
                        web.CoreWebView2.Navigate(targetUri);
                    }
                    catch { }
                };
                // 点击外部（窗口失焦）延迟自动关闭，给复用切换留出时间
                Deactivate += (s, e) =>
                {
                    if (!ready || persistent) return;
                    reused = false;
                    var closeTimer = new System.Windows.Forms.Timer { Interval = 250 };
                    closeTimer.Tick += (s2, e2) =>
                    {
                        closeTimer.Stop();
                        closeTimer.Dispose();
                        if (!reused && !IsDisposed) Close();
                    };
                    closeTimer.Start();
                };
            }

            public void NavigateTo(string url)
            {
                reused = true;
                targetUri = url;
                try
                {
                    if (web.CoreWebView2 != null) web.CoreWebView2.Navigate(url);
                }
                catch { }
            }
        }

        private sealed class MainForm : Form        {
            private readonly WebView2 web;
            private bool _closeResolved; // 用户已选定关闭方式，防止重复弹窗
            // —— 加载失败自动重试（2026-09-10 黑屏修复）——
            // 现象：窗口只剩标题栏，内容全黑，刷新一下才好；后端重启/启动竞态时最容易出现。
            // 原因：这里原来只有一句 web.Source = ...，整份程序没有任何失败重试或崩溃恢复。
            private int _attempt;          // 已失败次数
            private int _timeoutRetries;   // 被看门狗判定"超时未完成"的次数
            private System.Windows.Forms.Timer _navTimer;  // 导航看门狗
            private System.Windows.Forms.Timer _retryTimer; // 失败后退避重试
            private Label _overlay;
            // —— 开机片头（2026-09-28）：铺满窗口，盖住"WebView2 还没渲染出 DSH 界面"的那段空白 ——
            private static readonly string SplashTemplate = Root + @"\\assets\boot-splash";
            private const string SplashHost = "splash.local";
            private WebView2 _splash;
            // —— 右栏内嵌浏览器：主窗体里的一块 WebView2 子控件（不是独立窗口、没有坐标同步）——
            // 页面（主 WebView2 里的 DSH 右栏面板）用 chrome.webview.postMessage 把面板矩形的
            // getBoundingClientRect() + dpr 报过来，这里按矩形摆它；面板关掉/切走就隐藏。
            private WebView2 _embed;
            private CoreWebView2Environment _embedEnv;
            private bool _embedBusy;
            private bool _embedWanted;
            private string _embedUrl = "";
            /// <summary>内嵌视图当前是否在加载（导航栏的"重载/停止"靠它切换）。</summary>
            private bool _embedLoading;
            /// <summary>内嵌视图当前页标题。</summary>
            private string _embedTitle = "";
            /// <summary>多标签：一块 WebView2 子控件 = 一个标签页（共用同一个浏览器进程与 9223 调试口）。</summary>
            private sealed class EmbedTab
            {
                public string Id;
                public WebView2 View;
                public string Url = "";
                public string Title = "";
                public bool Loading;
            }
            /// <summary>加载遮罩：导航期间盖住旧页面（WebView2 默认会一直显示旧页直到新页首帧，
            /// 面板那边看不到任何动静，观感就是"点了没反应"）。</summary>
            private sealed class EmbedMask : Control
            {
                public double Angle;

                public EmbedMask()
                {
                    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                        | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                    Visible = false;
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    Graphics g = e.Graphics;
                    using (SolidBrush back = new SolidBrush(Color.FromArgb(0x17, 0x18, 0x1C)))
                    {
                        g.FillRectangle(back, ClientRectangle);
                    }
                    int size = 30;
                    int cx = Width / 2;
                    int cy = Height / 2;
                    if (cx < size || cy < size) return;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    Rectangle box = new Rectangle(cx - size / 2, cy - size / 2, size, size);
                    using (Pen track = new Pen(Color.FromArgb(0x2A, 0x2E, 0x36), 2.6f))
                    {
                        g.DrawEllipse(track, box);
                    }
                    // 渐隐尾巴：16 小段拼出约 100° 的弧，尾端渐淡，转起来就是常见的加载环
                    const int segments = 16;
                    for (int i = 0; i < segments; i++)
                    {
                        int alpha = 10 + (int)(245.0 * (i + 1) / segments);
                        using (Pen arc = new Pen(Color.FromArgb(alpha, 0x6F, 0xA8, 0xFF), 3f))
                        {
                            arc.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                            arc.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                            g.DrawArc(arc, box, (float)(Angle + i * 6.0), 8f);
                        }
                    }
                }
            }

            private EmbedMask _embedMask;
            /// <summary>延迟露面（180ms）：几百毫秒内就完成的导航不闪遮罩。</summary>
            private System.Windows.Forms.Timer _embedMaskDelay;
            /// <summary>延迟撤除（140ms）：JS 重定向紧接着又开一次导航时不闪回旧页。</summary>
            private System.Windows.Forms.Timer _embedMaskClear;
            private System.Windows.Forms.Timer _embedMaskSpin;
            private bool _embedMaskOn;
            private double _embedMaskAngle;
            private const int EmbedMaskDelayMs = 180;
            private const int EmbedMaskClearMs = 140;
            private readonly List<EmbedTab> _embedTabs = new List<EmbedTab>();
            private string _embedActiveId = "";
            private int _embedSerial = 0;
            /// <summary>当前面板矩形（新标签挂上来就照它摆位）。</summary>
            private Rectangle _embedBounds = Rectangle.Empty;
            /// <summary>标签数上限，到顶就不再开新的。</summary>
            private const int EmbedTabMax = 8;
            /// <summary>内嵌浏览器自己的 CDP 调试端口（只绑 127.0.0.1）：agent 驱动的是同一块视图，动作直接显示在面板里。</summary>
            private const string EmbedCdpPort = "9223";
            /// <summary>主视图（DSH 界面本身）的 CDP 端口（只绑 127.0.0.1）：界面问题直接量 DOM 尺寸，不靠截图猜。</summary>
            private const string MainCdpPort = "9222";
            /// <summary>内嵌视图缩放：右栏窄，缩一点才放得下必应那种 768 死版心的首页，观感也更像桌面浏览器。</summary>
            private const double EmbedZoom = 0.8;
            /// <summary>导航栏「首页」按钮的去处（与前端起始页一致）。</summary>
            private const string EmbedHome = "https://limestart.cn/";
            /// <summary>缩放 ± 的步长。</summary>
            private const double EmbedZoomStep = 0.1;
            // —— 收藏夹浮层（2026-09-28）：一块独立的小 WebView2，叠在内嵌视图之上 ——
            // 内嵌视图是原生子控件，永远盖在页面 DOM 之上，所以收藏夹只有两条路：让画面让位（整页感），
            // 或者自己也是一块原生控件压在画面上。这里走后者：浮层自带深色页面，画面不再隐藏。
            private WebView2 _shelf;
            private bool _shelfBusy;
            private bool _shelfReady;
            private bool _shelfWanted;
            private Rectangle _shelfBounds = Rectangle.Empty;
            private bool _shelfFlushBusy;
            private string _shelfItemsJson;
            private DateTime _shelfShownAt = DateTime.MinValue;
            private const int ShelfWidth = 300;
            /// <summary>默认行高/可视行数；面板每次都会带 panelH，这里只是它没带时的兜底。</summary>
            private const int ShelfRowHeight = 32;
            private const int ShelfVisibleRows = 10;
            private const int ShelfMaxHeight = 560;
            /// <summary>浮层页面：静态骨架，列表由面板数据经 ExecuteScriptAsync 注入。</summary>
            private const string ShelfHtml = @"<!doctype html><html><head><meta charset='utf-8'><style>
html,body{margin:0;padding:0;height:100%;overflow:hidden;background:transparent;font:13px/1.5 'Microsoft YaHei UI','Segoe UI',sans-serif;-webkit-user-select:none}
body{display:flex;box-sizing:border-box}
/* 子控件的透明只能透到父窗体背景、透不到下面的网页，所以卡片直接铺满控件，只让四个角露出一点点深色 */
#card{flex:1 1 auto;min-width:0;min-height:0;display:flex;flex-direction:column;background:#1e2024;border:1px solid rgba(255,255,255,.09);border-radius:10px;overflow:hidden;box-shadow:inset 0 1px 0 rgba(255,255,255,.05);visibility:hidden}
#list{flex:1 1 auto;min-height:0;overflow:auto;padding:6px}
.row{display:flex;align-items:center;gap:10px;height:32px;padding:0 10px;box-sizing:border-box;border-radius:8px;color:#c6cad3;cursor:default;transition:background .12s ease,color .12s ease}
.row:hover{background:rgba(255,255,255,.09);color:#fff}
/* 图标用 div 打底：img 加载失败会画出破图占位，div 的 background-image 失败只会剩底色 */
.ico{width:18px;height:18px;border-radius:4px;flex:0 0 auto;background-color:rgba(255,255,255,.09);background-size:contain;background-position:center;background-repeat:no-repeat}
.t{flex:1 1 auto;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.empty{padding:12px;color:#7d838d;font-size:12px}
#list::-webkit-scrollbar{width:10px}
#list::-webkit-scrollbar-thumb{background:rgba(255,255,255,.14);border-radius:5px}
#list::-webkit-scrollbar-thumb:hover{background:rgba(255,255,255,.24)}
#list::-webkit-scrollbar-track{background:transparent}
</style></head><body><div id='card'><div id='list'></div></div><script>
var box=document.getElementById('list');
var card=document.getElementById('card');
function hostOf(u){try{return new URL(u).hostname.replace(/^www\./,'');}catch(e){return u;}}
function renderShelf(items){
  card.style.visibility='visible';
  box.innerHTML='';
  if(!items||!items.length){var e=document.createElement('div');e.className='empty';e.textContent='-';box.appendChild(e);return;}
  for(var i=0;i<items.length;i++){(function(it){
    var r=document.createElement('div');r.className='row';r.title=it.url||'';
    var im=document.createElement('div');im.className='ico';
    if(it.icon){im.style.backgroundImage='url(""' + it.icon + '"")';im.style.backgroundColor='transparent';}
    var t=document.createElement('span');t.className='t';t.textContent=it.title||hostOf(it.url||'')||'';
    r.appendChild(im);r.appendChild(t);
    r.onclick=function(){chrome.webview.postMessage({pick:it.url});};
    box.appendChild(r);
  })(items[i]);}
}
document.addEventListener('keydown',function(e){if(e.key==='Escape')chrome.webview.postMessage({close:true});});
</script></body></html>";

            public MainForm()
            {
                Text = "DeepSeek Harness";
                AutoScaleMode = AutoScaleMode.None;
                // 浮层控件透明区透出来的就是这个颜色：深色才像卡片阴影，系统默认浅灰会是一圈发灰的边
                BackColor = Color.FromArgb(0x18, 0x19, 0x1D);
                // Default window: 75% of the working-area width, 16:9 aspect ratio,
                // centered on the primary screen (physical pixels, PMv2-aware).
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int w = (int)(wa.Width * 0.75);
                int h = (int)(w * 9.0 / 16.0);
                if (h > (int)(wa.Height * 0.90))
                {
                    h = (int)(wa.Height * 0.90);
                    w = (int)(h * 16.0 / 9.0);
                }
                if (w < 800)
                {
                    w = 800;
                }
                if (h < 500)
                {
                    h = 500;
                }
                Size = new Size(w, h);
                MinimumSize = new Size(720, 520);
                StartPosition = FormStartPosition.CenterScreen;
                try
                {
                    Icon = new Icon(IconPath);
                }
                catch
                {
                }
                web = new WebView2();
                web.Dock = DockStyle.Fill;
                Controls.Add(web);
                // 加载遮罩：盖在 WebView2 之上；加载成功即隐藏，因此不会挡住页面。
                // （黑屏那次就是这个状态一直挂着不消失——因为没有任何重试逻辑）
                _overlay = new Label();
                _overlay.Dock = DockStyle.Fill;
                _overlay.BackColor = Color.FromArgb(24, 24, 28);
                _overlay.ForeColor = Color.FromArgb(214, 218, 226);
                _overlay.TextAlign = ContentAlignment.MiddleCenter;
                _overlay.Font = new Font("Microsoft YaHei UI", 10f);
                _overlay.Text = "正在连接 DSH 服务…";
                _overlay.Visible = false;
                Controls.Add(_overlay);
                // 开机片头盖在最上层：窗口一出现就有画面，直到 DSH 界面自己渲染出来
                EnsureSplashRoot();
                _splash = new WebView2();
                _splash.Dock = DockStyle.Fill;
                _splash.Visible = true;
                try { _splash.DefaultBackgroundColor = Color.Black; } catch { }
                Controls.Add(_splash);
                _splash.BringToFront();
                Shown += OnShown;
            }

            /// <summary>开始一次导航：先武装看门狗，再导航（顺序不能反，否则可能漏掉即时的 NavigationCompleted）。</summary>
            private void NavigateWithRetry(String reason)
            {
                try
                {
                    if (_navTimer == null)
                    {
                        _navTimer = new System.Windows.Forms.Timer { Interval = 20000 };
                        _navTimer.Tick += delegate(object s, EventArgs e) { OnNavTimeout(); };
                    }
                    _navTimer.Stop();
                    _navTimer.Start();
                    // 首次导航由开机片头盖着，不再叠一块纯色遮罩；一旦要重试就让位给可读的文字提示
                    if (_attempt == 0)
                    {
                        if (_overlay != null) _overlay.Visible = false;
                        if (_splash != null) _splash.Visible = true;
                    }
                    else
                    {
                        HideSplash();
                        if (_overlay != null)
                        {
                            _overlay.Visible = true;
                            _overlay.Text = "正在连接 DSH 服务…" + Environment.NewLine + reason;
                        }
                    }
                    // 主页面要重载了：先把内嵌浏览器收掉，别让它盖住加载遮罩（页面回来后客户端会重新报矩形）
                    HideEmbed();
                    web.Source = new Uri(ResolveWebUrl());
                }
                catch (Exception ex)
                {
                    // 连导航都发起不了：走同一条退避重试，绝不留黑屏
                    System.Diagnostics.Debug.WriteLine("navigate failed: " + ex.Message);
                    ScheduleRetry();
                }
            }

            /// <summary>导航看门狗：超过 20 秒仍未完成一次导航就再来一次（首次启动多等几次，给服务启动留时间）。</summary>
            private void OnNavTimeout()
            {
                if (_navTimer != null) _navTimer.Stop();
                _timeoutRetries++;
                if (!Program.PortOpen()) Program.StartServer();
                HideSplash();
                if (_overlay != null)
                {
                    _overlay.Visible = true;
                    _overlay.Text = "后端还没就绪，正在重试…";
                }
                ScheduleRetry();
            }

            private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (_navTimer != null) _navTimer.Stop();
                if (e.IsSuccess)
                {
                    if (_overlay != null) _overlay.Visible = false;
                    ReleaseSplashToUser();
                    return;
                }
                // 失败：短暂等一次再重试；若引擎已不在，顺手把它拉起来
                if (!Program.PortOpen()) Program.StartServer();
                HideSplash();
                if (_overlay != null)
                {
                    _overlay.Visible = true;
                    _overlay.Text = "页面加载失败，正在重试…";
                }
                ScheduleRetry();
            }

            /// <summary>退避重试：1.5s → 3s → 6s … 上限 10s，永不放弃（这也修掉"必须手动刷新"）。</summary>
            private void ScheduleRetry()
            {
                _attempt++;
                if (_retryTimer == null)
                {
                    _retryTimer = new System.Windows.Forms.Timer { Interval = 1500 };
                    _retryTimer.Tick += delegate(object s, EventArgs e)
                    {
                        _retryTimer.Stop();
                        NavigateWithRetry("第 " + _attempt + " 次重试");
                    };
                }
                int delay = 1500;
                for (int k = 1; k < _attempt && delay < 10000; k++) delay *= 2;
                if (delay > 10000) delay = 10000;
                _retryTimer.Stop();
                _retryTimer.Interval = delay;
                _retryTimer.Start();
            }

            /// <summary>
            /// DSH 界面已经能操作了：片头不再由"DSH 是否就绪"决定去留 —— 露出右上角的「跳过」，
            /// 这一遍播完（或用户点跳过）才撤。片头当时若还在间隔/加载中，页面会直接回报已结束。
            /// </summary>
            private void ReleaseSplashToUser()
            {
                if (_splash == null || !_splash.Visible) return;
                if (_splash.CoreWebView2 == null)
                {
                    HideSplash();
                    return;
                }
                try
                {
                    _splash.CoreWebView2.ExecuteScriptAsync("window.__splashReady&&window.__splashReady()");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("splash ready failed: " + ex.Message);
                    HideSplash();
                }
            }

            private void HideSplash()
            {
                try
                {
                    if (_splash != null) _splash.Visible = false;
                }
                catch
                {
                }
            }

            /// <summary>片头资产摊到 ~/.dsh/boot-splash：页面每次用模板覆盖，视频与配置归用户。</summary>
            private static void EnsureSplashRoot()
            {
                try
                {
                    string root = SplashRoot();
                    Directory.CreateDirectory(root);
                    Directory.CreateDirectory(Path.Combine(root, "videos"));
                    File.Copy(Path.Combine(SplashTemplate, "index.html"), Path.Combine(root, "index.html"), true);
                    string cfg = Path.Combine(root, "config.json");
                    if (!File.Exists(cfg))
                    {
                        File.WriteAllText(cfg,
                            "{\r\n  \"video\": \"cyberpunk-intro.mp4\",\r\n  \"gapMs\": 3000\r\n}\r\n",
                            new System.Text.UTF8Encoding(false));
                    }
                    string def = Path.Combine(root, "videos", "cyberpunk-intro.mp4");
                    if (!File.Exists(def))
                    {
                        File.Copy(Path.Combine(SplashTemplate, "cyberpunk-intro.mp4"), def, true);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("splash root failed: " + ex.Message);
                }
            }

            private static string SplashRoot()
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".dsh", "boot-splash");
            }

            /// <summary>页面侧的两条消息：splash-skip（用户点跳过）、splash-ended（这一遍播完了）。</summary>
            private void OnSplashMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
            {
                string json;
                try
                {
                    json = e.WebMessageAsJson;
                }
                catch
                {
                    return;
                }
                if (json == null) return;
                if (json.IndexOf("splash-skip") >= 0 || json.IndexOf("splash-ended") >= 0) HideSplash();
            }

            /// <summary>
            /// WebView2 渲染/GPU 子进程崩溃后，控件会变成一块空白（看起来就是"白屏/黑屏"）。
            /// 这里记日志并自动 Reload 一次，让用户不必关窗口重开。
            /// </summary>
            private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
            {
                System.Diagnostics.Debug.WriteLine("WebView2 process failed: " + e.ProcessFailedKind);
                try
                {
                    if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited
                        && web.CoreWebView2 != null)
                    {
                        web.CoreWebView2.Reload();
                    }
                }
                catch
                {
                }
            }

            /// <summary>页面报来的 CSS 矩形 → 本窗体客户区的物理矩形；倍率优先用「主 WebView2 物理宽 ÷ 页面视口 CSS 宽」。</summary>
            private Rectangle EmbedRect(Dictionary<string, object> msg)
            {
                double vw = AsDouble(msg.ContainsKey("vw") ? msg["vw"] : null, 0);
                double dpr = AsDouble(msg.ContainsKey("dpr") ? msg["dpr"] : null, 1);
                double scale = dpr > 0.5 ? dpr : 1.0;
                int viewW = web.ClientSize.Width > 0 ? web.ClientSize.Width : ClientRectangle.Width;
                if (vw > 0 && viewW > 0)
                {
                    double measured = viewW / vw;
                    if (measured > 0.5 && measured < 8) scale = measured;
                }
                int cx = (int)Math.Round(AsDouble(msg.ContainsKey("x") ? msg["x"] : null, 0) * scale);
                int cy = (int)Math.Round(AsDouble(msg.ContainsKey("y") ? msg["y"] : null, 0) * scale);
                int cw = (int)Math.Round(AsDouble(msg.ContainsKey("w") ? msg["w"] : null, 0) * scale);
                int ch = (int)Math.Round(AsDouble(msg.ContainsKey("h") ? msg["h"] : null, 0) * scale);
                if (cw < 40 || ch < 40)
                {
                    return Rectangle.Empty;
                }
                Rectangle client = ClientRectangle;
                if (cx < 0) cx = 0;
                if (cy < 0) cy = 0;
                if (cx + cw > client.Width) cw = client.Width - cx;
                if (cy + ch > client.Height) ch = client.Height - cy;
                if (cw < 40 || ch < 40)
                {
                    return Rectangle.Empty;
                }
                return new Rectangle(cx, cy, cw, ch);
            }

            private static double AsDouble(object value, double fallback)
            {
                if (value == null) return fallback;
                try
                {
                    return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch
                {
                    return fallback;
                }
            }

            /// <summary>页面 → 外壳的唯一通道：只处理 kind = dsh-embed 的消息。</summary>
            private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
            {
                string json;
                try { json = e.WebMessageAsJson; }
                catch { return; }
                if (string.IsNullOrEmpty(json) || json.IndexOf("dsh-embed", StringComparison.Ordinal) < 0) return;
                Dictionary<string, object> msg;
                try { msg = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); }
                catch { return; }
                if (msg == null || !msg.ContainsKey("kind") || Convert.ToString(msg["kind"]) != "dsh-embed") return;
                string cmd = msg.ContainsKey("cmd") ? Convert.ToString(msg["cmd"]) : "";
                if (cmd == "hide")
                {
                    _embedWanted = false;
                    HideEmbed();
                    return;
                }
                Rectangle rect = EmbedRect(msg);
                // 收藏夹浮层与内嵌视图的显示状态无关：先处理，别碰 _embedWanted
                if (cmd == "shelf")
                {
                    ShelfCommand(msg, rect);
                    return;
                }
                // 面板收藏当前页之前来问一句：这页的图标在哪。这个只能有页面上下文的这边答
                if (cmd == "icon")
                {
                    ReadEmbedIcon();
                    return;
                }
                _embedWanted = true;
                if (cmd == "open")
                {
                    ShowEmbed(msg.ContainsKey("url") ? Convert.ToString(msg["url"]) : "", rect);
                    return;
                }
                if (cmd == "nav")
                {
                    EmbedNav(msg.ContainsKey("action") ? Convert.ToString(msg["action"]) : "");
                    return;
                }
                if (cmd == "newTab")
                {
                    _embedWanted = true;
                    if (!rect.IsEmpty) _embedBounds = rect;
                    NewEmbedTab(msg.ContainsKey("url") ? Convert.ToString(msg["url"]) : "", true);
                    return;
                }
                if (cmd == "closeTab")
                {
                    CloseEmbedTab(msg.ContainsKey("id") ? Convert.ToString(msg["id"]) : "");
                    return;
                }
                if (cmd == "selectTab")
                {
                    SelectEmbedTab(msg.ContainsKey("id") ? Convert.ToString(msg["id"]) : "");
                    return;
                }
                if (cmd == "rect" && _embed != null && !rect.IsEmpty)
                {
                    _embedBounds = rect;
                    _embed.Bounds = rect;
                    if (!_embed.Visible && !_embedMaskOn) _embed.Visible = true;
                    _embed.BringToFront();
                    SyncEmbedMask();
                    BringShelfFront();
                }
            }

            /// <summary>内嵌视图的状态推回面板：地址、标题、可否前进后退、加载中、缩放。</summary>
            private void PushEmbedState()
            {
                try
                {
                    if (web == null || web.CoreWebView2 == null) return;
                    if (!_embedWanted) return;
                    CoreWebView2 core = _embed == null ? null : _embed.CoreWebView2;
                    Dictionary<string, object> payload = new Dictionary<string, object>();
                    payload["kind"] = "dsh-embed-state";
                    payload["url"] = core == null ? "" : (core.Source ?? "");
                    EmbedTab shown = ActiveTab();
                    payload["title"] = shown == null ? (_embedTitle ?? "") : (shown.Title ?? "");
                    payload["canGoBack"] = core != null && core.CanGoBack;
                    payload["canGoForward"] = core != null && core.CanGoForward;
                    payload["loading"] = _embedLoading;
                    payload["zoom"] = _embed == null ? EmbedZoom : Math.Round(_embed.ZoomFactor, 3);
                    List<Dictionary<string, object>> tabs = new List<Dictionary<string, object>>();
                    for (int i = 0; i < _embedTabs.Count; i++)
                    {
                        EmbedTab item = _embedTabs[i];
                        Dictionary<string, object> row = new Dictionary<string, object>();
                        row["id"] = item.Id;
                        row["title"] = string.IsNullOrEmpty(item.Title) ? (string.IsNullOrEmpty(item.Url) ? "新标签" : item.Url) : item.Title;
                        row["url"] = item.Url ?? "";
                        row["active"] = item.Id == _embedActiveId;
                        row["loading"] = item.Loading;
                        tabs.Add(row);
                    }
                    payload["tabs"] = tabs;
                    web.CoreWebView2.PostWebMessageAsJson(new JavaScriptSerializer().Serialize(payload));
                }
                catch
                {
                }
            }

            /// <summary>导航栏来的动作：全部由外壳这边的同一块 WebView2 执行。</summary>
            private void EmbedNav(string action)
            {
                if (_embed == null || _embed.CoreWebView2 == null) return;
                CoreWebView2 core = _embed.CoreWebView2;
                try
                {
                    if (action == "back")
                    {
                        if (core.CanGoBack) core.GoBack();
                    }
                    else if (action == "forward")
                    {
                        if (core.CanGoForward) core.GoForward();
                    }
                    else if (action == "reload")
                    {
                        core.Reload();
                    }
                    else if (action == "stop")
                    {
                        core.Stop();
                    }
                    else if (action == "home")
                    {
                        core.Navigate(EmbedHome);
                    }
                    else if (action == "zoomIn")
                    {
                        SetEmbedZoom(_embed.ZoomFactor + EmbedZoomStep);
                    }
                    else if (action == "zoomOut")
                    {
                        SetEmbedZoom(_embed.ZoomFactor - EmbedZoomStep);
                    }
                }
                catch
                {
                }
                PushEmbedState();
            }

            /// <summary>缩放钳到 0.25-3，改完立刻回传（按钮 title 上显示百分比）。</summary>
            private void SetEmbedZoom(double value)
            {
                if (_embed == null) return;
                double zoom = Math.Round(value, 2);
                if (zoom < 0.25) zoom = 0.25;
                if (zoom > 3.0) zoom = 3.0;
                try { _embed.ZoomFactor = zoom; }
                catch { }
            }

            private void HideEmbed()
            {
                for (int i = 0; i < _embedTabs.Count; i++)
                {
                    try { _embedTabs[i].View.Visible = false; }
                    catch { }
                }
                try { if (_embed != null) _embed.Visible = false; }
                catch { }
                // 面板整块让位（收藏夹小卡片浮层/切走）：加载遮罩也一起收掉，且别把画面放回来
                HideEmbedMask(false);
                HideShelf(true);
            }

            /// <summary>该不该有遮罩：面板在要画面、当前标签在加载、矩形有效。</summary>
            private bool EmbedMaskWanted()
            {
                return _embedWanted && _embedLoading && _embed != null && !_embedBounds.IsEmpty;
            }

            /// <summary>导航一开始就调：先等 180ms，慢加载才真的露面。</summary>
            private void ArmEmbedMask()
            {
                if (_embedMaskClear != null) _embedMaskClear.Stop();
                if (!EmbedMaskWanted())
                {
                    HideEmbedMask();
                    return;
                }
                if (_embedMaskOn)
                {
                    SyncEmbedMask();
                    return;
                }
                if (_embedMaskDelay == null)
                {
                    _embedMaskDelay = new System.Windows.Forms.Timer { Interval = EmbedMaskDelayMs };
                    _embedMaskDelay.Tick += delegate
                    {
                        _embedMaskDelay.Stop();
                        if (EmbedMaskWanted()) ShowEmbedMask();
                    };
                }
                _embedMaskDelay.Stop();
                _embedMaskDelay.Start();
            }

            /// <summary>遮罩在显示时又报了新矩形（拖右栏）：跟着走。</summary>
            private void SyncEmbedMask()
            {
                if (!_embedMaskOn || _embedMask == null) return;
                try { _embedMask.Bounds = _embedBounds; } catch { }
                try { if (_embed.Visible) _embed.Visible = false; } catch { }
                try { _embedMask.BringToFront(); } catch { }
                BringShelfFront();
            }

            private void ShowEmbedMask()
            {
                if (_embedMask == null)
                {
                    _embedMask = new EmbedMask();
                    Controls.Add(_embedMask);
                }
                if (!EmbedMaskWanted()) return;
                _embedMaskAngle = 0;
                _embedMask.Angle = 0;
                try { _embedMask.Bounds = _embedBounds; } catch { }
                _embedMaskOn = true;
                // 遮罩是普通 GDI 控件，WebView2 是原生子控件：藏掉画面才保证遮罩一定在它上面
                try { if (_embed != null) _embed.Visible = false; } catch { }
                try { _embedMask.Visible = true; _embedMask.BringToFront(); } catch { }
                BringShelfFront();
                if (_embedMaskSpin == null)
                {
                    _embedMaskSpin = new System.Windows.Forms.Timer { Interval = 33 };
                    _embedMaskSpin.Tick += delegate
                    {
                        if (!_embedMaskOn || _embedMask == null) return;
                        _embedMaskAngle = (_embedMaskAngle + 24.0) % 360.0;
                        _embedMask.Angle = _embedMaskAngle;
                        _embedMask.Invalidate();
                    };
                }
                _embedMaskSpin.Start();
            }

            /// <summary>一次导航完成后不马上撤：等 140ms，紧接着又来一次导航（JS 重定向）就继续盖着。</summary>
            private void ScheduleEmbedMaskClear()
            {
                // 导航已经结束了：还没到 180ms 的延迟露面直接取消，否则快页面会闪一下遮罩
                if (_embedMaskDelay != null) _embedMaskDelay.Stop();
                if (_embedMaskClear == null)
                {
                    _embedMaskClear = new System.Windows.Forms.Timer { Interval = EmbedMaskClearMs };
                    _embedMaskClear.Tick += delegate
                    {
                        _embedMaskClear.Stop();
                        HideEmbedMask();
                    };
                }
                _embedMaskClear.Stop();
                _embedMaskClear.Start();
            }

            /// <summary>导航结束（成功或失败都算）：撤遮罩，把画面放回来。</summary>
            private void HideEmbedMask()
            {
                HideEmbedMask(true);
            }

            /// <summary>restore=false 用于面板整块让位：只撤遮罩，别把画面又放出来。</summary>
            private void HideEmbedMask(bool restore)
            {
                if (_embedMaskDelay != null) _embedMaskDelay.Stop();
                if (_embedMaskClear != null) _embedMaskClear.Stop();
                if (_embedMaskSpin != null) _embedMaskSpin.Stop();
                if (!_embedMaskOn) return;
                _embedMaskOn = false;
                try { if (_embedMask != null) _embedMask.Visible = false; } catch { }
                if (!restore) return;
                try
                {
                    if (_embed != null && _embedWanted && !_embedBounds.IsEmpty)
                    {
                        _embed.Bounds = _embedBounds;
                        _embed.Visible = true;
                        _embed.BringToFront();
                    }
                }
                catch { }
                BringShelfFront();
            }

            /// <summary>建一块内嵌 WebView2 子控件（共用一份 CoreWebView2Environment：同一个浏览器进程、同一个 9223 调试口）。</summary>
            private async Task<WebView2> CreateEmbedView()
            {
                if (_embedBusy) return null;
                _embedBusy = true;
                WebView2 view = new WebView2();
                view.Visible = false;
                view.Name = "embedBrowser" + (_embedSerial + 1).ToString();
                // 点画面 = 把焦点从收藏夹浮层拿走：浮层跟着收起（浏览器里点别处收起收藏夹的同一种手感）
                view.GotFocus += delegate { OnEmbedFocus(); };
                Controls.Add(view);
                try
                {
                    string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "embed-profile");
                    Directory.CreateDirectory(dir);
                    if (_embedEnv == null)
                    {
                        CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions
                        {
                            AdditionalBrowserArguments = "--remote-debugging-port=" + EmbedCdpPort + " --remote-allow-origins=*"
                        };
                        _embedEnv = await CoreWebView2Environment.CreateAsync(null, dir, options);
                    }
                    await view.EnsureCoreWebView2Async(_embedEnv);
                }
                catch (Exception ex)
                {
                    _embedBusy = false;
                    try { Controls.Remove(view); view.Dispose(); }
                    catch { }
                    System.Diagnostics.Debug.WriteLine("embed init failed: " + ex.Message);
                    return null;
                }
                _embedBusy = false;
                try { view.CoreWebView2.Settings.IsStatusBarEnabled = false; }
                catch { }
                try { view.ZoomFactor = EmbedZoom; }
                catch { }
                return view;
            }

            /// <summary>开一个标签页（一块新的 WebView2 子控件）；activate=true 就切过去。</summary>
            private async void NewEmbedTab(string url, bool activate)
            {
                if (_embedTabs.Count >= EmbedTabMax)
                {
                    Program.LogResolve("embed tab limit reached (" + EmbedTabMax + ")");
                    return;
                }
                WebView2 view = await CreateEmbedView();
                if (view == null) return;
                EmbedTab tab = new EmbedTab();
                tab.Id = "t" + (++_embedSerial).ToString();
                tab.View = view;
                tab.Url = url == null ? "" : url;
                // target=_blank / window.open：不另开顶层窗口，直接在标签条里多一个标签
                view.CoreWebView2.NewWindowRequested += delegate(object s2, CoreWebView2NewWindowRequestedEventArgs a2)
                {
                    a2.Handled = true;
                    NewEmbedTab(a2.Uri, true);
                };
                view.CoreWebView2.NavigationStarting += delegate(object s3, CoreWebView2NavigationStartingEventArgs a3)
                {
                    tab.Loading = true;
                    // 只有当前可见标签的导航才动画面：后台标签在加载不该把画面遮住
                    if (tab.Id == _embedActiveId)
                    {
                        _embedLoading = true;
                        ArmEmbedMask();
                    }
                    PushEmbedState();
                };
                view.CoreWebView2.NavigationCompleted += delegate(object s3, CoreWebView2NavigationCompletedEventArgs a3)
                {
                    tab.Loading = false;
                    if (tab.Id == _embedActiveId)
                    {
                        _embedLoading = false;
                        ScheduleEmbedMaskClear();
                    }
                    PushEmbedState();
                };
                view.CoreWebView2.SourceChanged += delegate(object s3, CoreWebView2SourceChangedEventArgs a3)
                {
                    try { tab.Url = tab.View.CoreWebView2.Source ?? tab.Url; }
                    catch { }
                    PushEmbedState();
                };
                view.CoreWebView2.HistoryChanged += delegate(object s3, object a3) { PushEmbedState(); };
                view.CoreWebView2.DocumentTitleChanged += delegate(object s3, object a3)
                {
                    try { tab.Title = tab.View.CoreWebView2.DocumentTitle ?? ""; }
                    catch { }
                    PushEmbedState();
                };
                _embedTabs.Add(tab);
                if (activate) _embedActiveId = tab.Id;
                SyncActiveTab();
                if (tab.Url.Length > 0)
                {
                    Program.LogResolve("embed tab open " + tab.Url);
                    try { view.CoreWebView2.Navigate(tab.Url); }
                    catch { }
                }
                PushEmbedState();
            }

            /// <summary>关一个标签（控件一起释放）；关的是当前标签就切到左边那个。</summary>
            private void CloseEmbedTab(string id)
            {
                for (int i = 0; i < _embedTabs.Count; i++)
                {
                    if (_embedTabs[i].Id != id) continue;
                    EmbedTab tab = _embedTabs[i];
                    _embedTabs.RemoveAt(i);
                    try { Controls.Remove(tab.View); tab.View.Dispose(); }
                    catch { }
                    if (_embedActiveId == id) _embedActiveId = _embedTabs.Count > 0 ? _embedTabs[Math.Max(0, i - 1)].Id : "";
                    SyncActiveTab();
                    PushEmbedState();
                    return;
                }
            }

            /// <summary>切标签：只有当前那块控件可见。</summary>
            private void SelectEmbedTab(string id)
            {
                _embedActiveId = id;
                SyncActiveTab();
                try { if (_embed != null) _embed.BringToFront(); }
                catch { }
                BringShelfFront();
                PushEmbedState();
            }

            private EmbedTab ActiveTab()
            {
                for (int i = 0; i < _embedTabs.Count; i++)
                {
                    if (_embedTabs[i].Id == _embedActiveId) return _embedTabs[i];
                }
                return _embedTabs.Count > 0 ? _embedTabs[0] : null;
            }

            /// <summary>当前标签的字段同步到 _embed* 与可见性（只有当前那块控件可见，且照面板矩形摆好）。</summary>
            private void SyncActiveTab()
            {
                EmbedTab tab = ActiveTab();
                if (tab == null)
                {
                    _embed = null;
                    _embedActiveId = "";
                    _embedUrl = "";
                    _embedTitle = "";
                    _embedLoading = false;
                    HideEmbedMask(false);
                    return;
                }
                _embedActiveId = tab.Id;
                _embed = tab.View;
                _embedUrl = tab.Url ?? "";
                _embedTitle = tab.Title ?? "";
                _embedLoading = tab.Loading;
                for (int i = 0; i < _embedTabs.Count; i++)
                {
                    try { _embedTabs[i].View.Visible = _embedWanted && _embedTabs[i].Id == _embedActiveId && !_embedBounds.IsEmpty; }
                    catch { }
                }
                try { if (_embed != null && _embed.Visible) _embed.Bounds = _embedBounds; }
                catch { }
                // 关键：WinForms 里后 Add 的控件在 z 序最底，不抬上来就被主视图盖住（页面在跑却什么都看不到）
                try { if (_embed != null && _embed.Visible) _embed.BringToFront(); }
                catch { }
                // 切到一个还在加载的标签：遮罩该在就在；切到已加载完的标签：撤掉并把画面放回来
                if (_embedLoading) ArmEmbedMask(); else HideEmbedMask();
                BringShelfFront();
            }

            /// <summary>把当前标签摆到面板矩形上并导航（面板每次上报矩形都走这里）。</summary>
            private void ShowEmbed(string url, Rectangle rect)
            {
                try
                {
                    _embedWanted = true;
                    if (!rect.IsEmpty) _embedBounds = rect;
                    // 控件还在、但它的浏览器进程已经没了（调试口被清 / 进程崩过）：把这块标签整块丢掉重建
                    for (int i = _embedTabs.Count - 1; i >= 0; i--)
                    {
                        bool dead = false;
                        try { dead = _embedTabs[i].View.CoreWebView2 == null || _embedTabs[i].View.IsDisposed; }
                        catch { dead = true; }
                        if (dead)
                        {
                            try { Controls.Remove(_embedTabs[i].View); _embedTabs[i].View.Dispose(); }
                            catch { }
                            _embedTabs.RemoveAt(i);
                            Program.LogResolve("embed control dead, dropped");
                        }
                    }
                    EmbedTab tab = ActiveTab();
                    if (tab == null)
                    {
                        NewEmbedTab(string.IsNullOrEmpty(url) ? EmbedHome : url, true);
                        return;
                    }
                    SyncActiveTab();
                    if (!string.IsNullOrEmpty(url) && url != tab.Url)
                    {
                        tab.Url = url;
                        Program.LogResolve("embed open " + url);
                        try { tab.View.CoreWebView2.Navigate(url); }
                        catch { }
                    }
                    if (!_embedBounds.IsEmpty)
                    {
                        try { tab.View.Bounds = _embedBounds; }
                        catch { }
                    }
                    try { tab.View.Visible = !_embedMaskOn; tab.View.BringToFront(); }
                    catch { }
                    SyncEmbedMask();
                    BringShelfFront();
                    PushEmbedState();
                }
                catch (Exception ex)
                {
                    _embedBusy = false;
                    System.Diagnostics.Debug.WriteLine("embed show failed: " + ex.Message);
                }
            }

            /// <summary>收藏夹浮层：显示 / 更新位置 / 收起。面板打开时带 items，之后每次上报矩形只带位置。</summary>
            private void ShelfCommand(Dictionary<string, object> msg, Rectangle rect)
            {
                // 面板挂载时探一次：认这条命令的外壳走浮层，旧外壳继续用面板里的小卡片，收藏夹不会变成"点了没反应"
                if (AsBool(msg.ContainsKey("probe") ? msg["probe"] : null, false))
                {
                    PostToPanel("{\"kind\":\"dsh-embed-shelf\",\"ack\":true}");
                    return;
                }
                bool show = AsBool(msg.ContainsKey("show") ? msg["show"] : null, false);
                if (!show || rect.IsEmpty)
                {
                    HideShelf(true);
                    return;
                }
                // items 的运行时类型随序列化路径变（object[] / List<object>／嵌套字典），按 IEnumerable 收，别用 as object[]
                object raw = msg.ContainsKey("items") ? msg["items"] : null;
                int wanted = (int)AsDouble(msg.ContainsKey("panelH") ? msg["panelH"] : null, 0);
                Rectangle box = ShelfBounds(rect, wanted);
                if (box.IsEmpty)
                {
                    HideShelf(true);
                    return;
                }
                // 先算好尺寸再建控件：让 WebView2 一出生就是这个视口。否则页面先按默认小尺寸排一遍、
                // 拿到真尺寸再重排，打开时就会看到文字先挤在中间再弹回左边
                EnsureShelf(box);
                if (_shelf == null) return;
                _shelfBounds = box;
                _shelfWanted = true;
                if (raw != null)
                {
                    List<object> items = new List<object>();
                    System.Collections.IEnumerable seq = raw as System.Collections.IEnumerable;
                    if (seq != null && !(raw is string))
                    {
                        foreach (object one in seq) items.Add(one);
                    }
                    Program.LogResolve("shelf rows " + items.Count + " (" + raw.GetType().Name + ")");
                    _shelfItemsJson = new JavaScriptSerializer().Serialize(items);
                }
                _shelfShownAt = DateTime.Now;
                FlushShelf();
            }

            /// <summary>浮层矩形：贴面板右上角，宽度固定，高度按条目数自适应（超上限就内部滚动）。</summary>
            private Rectangle ShelfBounds(Rectangle stage, int wanted)
            {
                int maxH = Math.Min(ShelfMaxHeight, Math.Max(60, stage.Height - 12));
                // 高度由面板给（panelH）；没给就按默认可视行数算
                int h = wanted > 0 ? wanted : (8 + ShelfVisibleRows * ShelfRowHeight);
                if (h > maxH) h = maxH;
                int x = stage.Right - ShelfWidth - 6;
                int y = stage.Y + 6;
                Rectangle client = ClientRectangle;
                if (x < 0) x = 0;
                if (y < 0) y = 0;
                int w = Math.Min(ShelfWidth, client.Width - x);
                if (y + h > client.Height) h = client.Height - y;
                if (w < 80 || h < 40) return Rectangle.Empty;
                return new Rectangle(x, y, w, h);
            }

            /// <summary>按需建浮层控件（复用内嵌浏览器的环境：同一个浏览器进程、同一个 9223 调试口）。</summary>
            private async void EnsureShelf(Rectangle initBounds)
            {
                if (_shelf != null || _shelfBusy) return;
                _shelfBusy = true;
                WebView2 view = new WebView2();
                view.Visible = false;
                // 控件一出生就是最终尺寸：WebView2 拿它当初始视口，页面只排一次版
                if (!initBounds.IsEmpty) view.Bounds = initBounds;
                view.Name = "embedShelf";
                Controls.Add(view);
                _shelf = view;
                try
                {
                    string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "embed-profile");
                    Directory.CreateDirectory(dir);
                    if (_embedEnv == null)
                    {
                        CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions
                        {
                            AdditionalBrowserArguments = "--remote-debugging-port=" + EmbedCdpPort + " --remote-allow-origins=*"
                        };
                        _embedEnv = await CoreWebView2Environment.CreateAsync(null, dir, options);
                    }
                    await view.EnsureCoreWebView2Async(_embedEnv);
                }
                catch (Exception ex)
                {
                    _shelfBusy = false;
                    try { Controls.Remove(view); view.Dispose(); }
                    catch { }
                    _shelf = null;
                    Program.LogResolve("shelf init failed: " + ex.Message);
                    return;
                }
                _shelfBusy = false;
                // 圆角与投影靠 CSS：控件本体透明，四角露出下面的网页
                try { view.DefaultBackgroundColor = Color.Transparent; }
                catch { }
                try { view.CoreWebView2.Settings.IsStatusBarEnabled = false; }
                catch { }
                view.CoreWebView2.WebMessageReceived += OnShelfMessage;
                view.CoreWebView2.NavigationCompleted += delegate(object s2, CoreWebView2NavigationCompletedEventArgs a2)
                {
                    _shelfReady = true;
                    FlushShelf();
                };
                view.CoreWebView2.NavigateToString(ShelfHtml);
            }

            /// <summary>把当前矩形与条目推给浮层；页面还没加载完就等 NavigationCompleted 再来一次。</summary>
            private async void FlushShelf()
            {
                if (!_shelfReady || _shelf == null || !_shelfWanted) return;
                string json = _shelfItemsJson;
                if (json != null && !_shelfFlushBusy)
                {
                    _shelfFlushBusy = true;
                    _shelfItemsJson = null;
                    try { _shelf.Bounds = _shelfBounds; } catch { }
                    // 先把列表画好、留一帧给它合成，再露面：直接显示的话先闪一个空卡片，看着就像"卡一下"
                    try { await _shelf.CoreWebView2.ExecuteScriptAsync("renderShelf(" + json + ")"); } catch { }
                    await Task.Delay(20);
                    _shelfFlushBusy = false;
                    if (_shelf == null || !_shelfWanted) return;
                }
                try
                {
                    _shelf.Bounds = _shelfBounds;
                    if (!_shelf.Visible)
                    {
                        _shelf.Visible = true;
                        _shelf.BringToFront();
                        _shelf.Focus();
                        _shelfShownAt = DateTime.Now;
                    }
                }
                catch { }
            }

            /// <summary>收起浮层。notify=true 时告诉面板「是外壳这边关的」，让收藏夹按钮复位。</summary>
            private void HideShelf(bool notify)
            {
                bool wasOpen = _shelfWanted || (_shelf != null && _shelf.Visible);
                _shelfWanted = false;
                _shelfItemsJson = null;
                try { if (_shelf != null) _shelf.Visible = false; }
                catch { }
                if (notify && wasOpen) PostToPanel("{\"kind\":\"dsh-embed-shelf\",\"closed\":true}");
            }

            /// <summary>内嵌视图刚抬到最前，浮层要跟着再抬一次，否则被压在下面。</summary>
            private void BringShelfFront()
            {
                try { if (_shelf != null && _shelf.Visible) _shelf.BringToFront(); }
                catch { }
            }

            /// <summary>焦点离开浮层（用户点了画面或主界面）就收起；刚打开的 250ms 内不响应，免得被自己的 Focus 误判。</summary>
            private void OnEmbedFocus()
            {
                if (!_shelfWanted) return;
                if ((DateTime.Now - _shelfShownAt).TotalMilliseconds < 250) return;
                HideShelf(true);
            }

            /// <summary>浮层里的动作：点某条 → 交给面板导航；Esc → 收起。</summary>
            private void OnShelfMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
            {
                string json;
                try { json = e.WebMessageAsJson; }
                catch { return; }
                string pick = "";
                bool close = false;
                try
                {
                    Dictionary<string, object> msg = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                    if (msg != null)
                    {
                        if (msg.ContainsKey("pick")) pick = Convert.ToString(msg["pick"]);
                        if (msg.ContainsKey("close")) close = AsBool(msg["close"], false);
                    }
                }
                catch { return; }
                if (pick != null && pick.Length > 0)
                {
                    HideShelf(false);
                    PostToPanel("{\"kind\":\"dsh-embed-shelf\",\"url\":" + new JavaScriptSerializer().Serialize(pick) + "}");
                    return;
                }
                if (close) HideShelf(true);
            }

            /// <summary>把当前标签页的真实图标地址报给面板：HTML 里的 link[rel*=icon] 才准，
            /// 直接猜 /favicon.ico 经常 404/403（站点把图标放在 CDN 上，青柠就是这种）。</summary>
            private async void ReadEmbedIcon()
            {
                string icon = "";
                string pageUrl = "";
                try
                {
                    if (_embed != null && _embed.CoreWebView2 != null)
                    {
                        pageUrl = _embed.CoreWebView2.Source ?? "";
                        string raw = await _embed.CoreWebView2.ExecuteScriptAsync(
                            "(function(){var l=document.querySelector('link[rel*=icon]');" +
                            "if(l&&l.href)return l.href;return location.origin+'/favicon.ico';})()");
                        icon = UnquoteJson(raw);
                    }
                }
                catch { }
                // CDN 普遍有防盗链（青柠那张不带 Referer 就是 403），而浮层页面是 about:blank，
                // 自己发请求既没有 Referer 也过不了 CORS —— 所以由这边按页面 Referer 把图抓下来，
                // 缩到 32×32 转成内嵌 data URL 交给面板，浮层从此不联网取图。
                string inlined = await Task.Run(() => FetchIconData(icon, pageUrl));
                string payload = inlined.Length > 0 ? inlined : icon;
                Program.LogResolve("embed icon " + (icon.Length > 0 ? icon : "(none)")
                    + (inlined.Length > 0 ? " [inlined " + inlined.Length + "]" : " [inline failed]"));
                PostToPanel("{\"kind\":\"dsh-embed-icon\",\"icon\":" + new JavaScriptSerializer().Serialize(payload == null ? "" : payload) + "}");
            }

            /// <summary>按页面 Referer 下载图标并缩到 32×32 的 PNG data URL；失败返回空串（调用方退回原始 URL）。</summary>
            private static string FetchIconData(string iconUrl, string pageUrl)
            {
                try
                {
                    if (string.IsNullOrEmpty(iconUrl)) return "";
                    string referer = "";
                    try
                    {
                        if (!string.IsNullOrEmpty(pageUrl))
                        {
                            Uri page = new Uri(pageUrl);
                            referer = page.Scheme + "://" + page.Authority + "/";
                        }
                    }
                    catch { }
                    Program.LogResolve("icon fetch page=" + (string.IsNullOrEmpty(pageUrl) ? "(none)" : pageUrl)
                        + " referer=" + (referer.Length > 0 ? referer : "(none)"));
                    byte[] data = DownloadIcon(iconUrl, referer);
                    // 少数站点反过来讨厌 Referer：带上失败就再裸试一次
                    if (data == null || data.Length == 0) data = DownloadIcon(iconUrl, "");
                    if (data == null || data.Length == 0)
                    {
                        Program.LogResolve("icon download empty");
                        return "";
                    }
                    if (data.Length > 2 * 1024 * 1024)
                    {
                        Program.LogResolve("icon too big " + data.Length);
                        return "";
                    }
                    using (MemoryStream source = new MemoryStream(data))
                    using (Image image = Image.FromStream(source))
                    using (Bitmap scaled = new Bitmap(32, 32))
                    {
                        using (Graphics g = Graphics.FromImage(scaled))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(image, 0, 0, 32, 32);
                        }
                        using (MemoryStream output = new MemoryStream())
                        {
                            scaled.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                            Program.LogResolve("icon got " + data.Length + " bytes -> png " + output.Length);
                            return "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.LogResolve("icon inline failed: " + ex.GetType().Name + " " + ex.Message);
                    return "";
                }
            }

            private static byte[] DownloadIcon(string iconUrl, string referer)
            {
                try
                {
                    // .NET Framework 默认可能只协商 TLS 1.0/1.1，现代 CDN 会握手失败（Tls11=768, Tls12=3072）
                    try { System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)(768 | 3072 | 192); }
                    catch { }
                    using (System.Net.WebClient client = new System.Net.WebClient())
                    {
                        client.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                        if (!string.IsNullOrEmpty(referer)) client.Headers.Add("Referer", referer);
                        return client.DownloadData(iconUrl);
                    }
                }
                catch (Exception ex)
                {
                    Program.LogResolve("icon download failed (referer=" + (string.IsNullOrEmpty(referer) ? "none" : "yes") + "): "
                        + ex.GetType().Name + " " + ex.Message);
                    return null;
                }
            }

            /// <summary>ExecuteScriptAsync 的结果是 JSON 编码的，字符串值要脱掉外层引号。</summary>
            private static string UnquoteJson(string raw)
            {
                if (string.IsNullOrEmpty(raw)) return "";
                try
                {
                    object value = new JavaScriptSerializer().DeserializeObject(raw);
                    return value == null ? "" : Convert.ToString(value);
                }
                catch { return ""; }
            }

            private void PostToPanel(string json)
            {
                try { if (web != null && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json); }
                catch { }
            }

            private static bool AsBool(object value, bool fallback)
            {
                if (value == null) return fallback;
                if (value is bool) return (bool)value;
                try { return Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture); }
                catch { return fallback; }
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                // 窗口关掉后遮罩的定时器还会 tick 到已释放的控件，先停掉
                if (_embedMaskDelay != null) _embedMaskDelay.Stop();
                if (_embedMaskClear != null) _embedMaskClear.Stop();
                if (_embedMaskSpin != null) _embedMaskSpin.Stop();
                // 2026-09-11：取消关闭询问弹窗，点 X 一律静默驻留托盘（引擎 3080 与 WiFi 反代 3081 继续跑），
                // 双击托盘图标随时唤回；系统注销/关机（CloseReason 非 UserClosing）仍直接放行。
                if (_closeResolved || e.CloseReason != CloseReason.UserClosing)
                {
                    base.OnFormClosing(e);
                    return;
                }
                e.Cancel = true;
                Program.EnsureTrayRunning();
                _closeResolved = true;
                Close();
            }

            private async void OnShown(object sender, EventArgs e)
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    WindowState = FormWindowState.Normal;
                }
                Activate();
                BringToFront();

                try
                {
                    // 主视图也开一个只绑本机的调试口（9222）：量 DSH 自己的界面尺寸用
                    CoreWebView2EnvironmentOptions mainOptions = new CoreWebView2EnvironmentOptions
                    {
                        AdditionalBrowserArguments = "--remote-debugging-port=" + MainCdpPort
                    };
                    string mainData = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dsh-desktop.exe.WebView2");
                    CoreWebView2Environment mainEnv = await CoreWebView2Environment.CreateAsync(null, mainData, mainOptions);
                    try
                    {
                        await _splash.EnsureCoreWebView2Async(mainEnv);
                        _splash.CoreWebView2.Settings.IsStatusBarEnabled = false;
                        _splash.CoreWebView2.WebMessageReceived += OnSplashMessage;
                        _splash.CoreWebView2.SetVirtualHostNameToFolderMapping(SplashHost, SplashRoot(), CoreWebView2HostResourceAccessKind.Allow);
                        _splash.CoreWebView2.Navigate("http://" + SplashHost + "/index.html");
                    }
                    catch (Exception exSplash)
                    {
                        System.Diagnostics.Debug.WriteLine("splash failed: " + exSplash.Message);
                        HideSplash();
                    }
                    await web.EnsureCoreWebView2Async(mainEnv);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("WebView2 初始化失败：\n" + ex.Message,
                        "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                try
                {
                    web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                }
                catch
                {
                }
                // 拦截 window.open / target=_blank：在 DSH 窗口内新开 WebView2 窗口打开，
                // 而不是唤起系统浏览器，实现官方平台页面（充值/用量/API Key）的"内化"。
                web.CoreWebView2.NewWindowRequested += (wvSender, wvArgs) =>
                {
                    wvArgs.Handled = true;
                    OpenChildWindow(wvArgs.Uri);
                };
                // 加载失败 / 超时 / 渲染进程崩溃的恢复钩子（黑屏修复的核心）
                if (web.CoreWebView2 != null)
                {
                    web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                    web.CoreWebView2.ProcessFailed += OnProcessFailed;
                }
                // 放行浏览器通知权限（配合 dsh-wallet 的低余额 / 超上限系统通知）
                web.CoreWebView2.PermissionRequested += (wvSender, wvArgs) =>
                {
                    if (wvArgs.PermissionKind == CoreWebView2PermissionKind.Notifications)
                        wvArgs.State = CoreWebView2PermissionState.Allow;
                };
                // 右栏内嵌浏览器：页面把面板矩形报过来（唯一通道，kind=dsh-embed 的消息才处理）
                web.CoreWebView2.WebMessageReceived += OnWebMessage;
                // 点主界面（离开内嵌视图或浮层）也把收藏夹浮层收起来
                web.GotFocus += delegate { OnEmbedFocus(); };

                bool broughtUpHere = false;
                if (!Program.PortOpen())
                {
                    Text = "DeepSeek Harness - 正在启动服务...";
                    Program.StartServer();
                    broughtUpHere = true;
                }
                else if (Program.EngineRestartedWithoutLog())
                {
                    // 3080 在监听，但它比最后一份 token 日志还新：那份 token 一定不是它的，
                    // 直接导航只会 401 然后无限重试。把引擎接管过来重启，token 才可追溯。
                    Text = "DeepSeek Harness - 正在重启服务...";
                    Program.RestartServerOwned();
                    broughtUpHere = true;
                }
                if (broughtUpHere)
                {
                    await Task.Run(delegate
                    {
                        for (int i = 0; i < 180 && !Program.PortOpen(); i++)
                        {
                            Thread.Sleep(500);
                        }
                    });
                }

                // 确保 WiFi 代理在跑（供手机局域网访问），不管 DSH 是否本次启动
                if (!Program.ProxyOpen())
                {
                    Program.StartProxy();
                }

                if (Program.PortOpen())
                {
                    Text = "DeepSeek Harness";
                    // 首次给后端启动留更长时间（60s 看门狗），之后按 1.5s→10s 退避重试；
                    // 只要有一次没加载出来就自动重来，不再出现"只剩标题栏的黑窗口"。
                    if (_navTimer != null) _navTimer.Interval = 60000;
                    NavigateWithRetry("");
                }
                else
                {
                    Text = "DeepSeek Harness";
                    MessageBox.Show("DeepSeek Harness 服务未能启动，请稍后重试。",
                        "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                Activate();
                BringToFront();
            }
        }

        /// <summary>关闭询问窗的绘制基件：无锯齿圆角矩形。</summary>
        internal static class DialogUi
        {
            // 配色（浅色卡片）
            internal static readonly Color CBg = Color.FromArgb(0xF5, 0xF6, 0xF8);
            internal static readonly Color CText = Color.FromArgb(0x1A, 0x1A, 0x1A);
            internal static readonly Color CSub = Color.FromArgb(0x6B, 0x6F, 0x76);
            internal static readonly Color CLine = Color.FromArgb(0xE2, 0xE5, 0xEA);
            internal static readonly Color CAccent = Color.FromArgb(0x4D, 0x6B, 0xFE);
            internal static readonly Color CAccentSoft = Color.FromArgb(0xF2, 0xF5, 0xFF);
            internal static readonly Color CAccentBorder = Color.FromArgb(0x4D, 0x6B, 0xFE);
            internal static readonly Color CGrayIcon = Color.FromArgb(0x8A, 0x90, 0x99);
            internal static readonly Color CDanger = Color.FromArgb(0xD9, 0x4A, 0x4A);

            internal static GraphicsPath Round(Rectangle r, int radius)
            {
                int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
                GraphicsPath p = new GraphicsPath();
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }

            internal static void Fill(Graphics g, Rectangle r, int radius, Color c)
            {
                using (GraphicsPath p = Round(r, radius))
                using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
            }

            internal static void Stroke(Graphics g, Rectangle r, int radius, Color c, float w)
            {
                using (GraphicsPath p = Round(r, radius))
                using (Pen pen = new Pen(c, w)) g.DrawPath(pen, p);
            }

            internal static void Circle(Graphics g, Rectangle r, Color fill, Color stroke, float sw)
            {
                if (fill.A > 0)
                {
                    using (SolidBrush b = new SolidBrush(fill)) g.FillEllipse(b, r);
                }
                if (stroke.A > 0 && sw > 0)
                {
                    using (Pen p = new Pen(stroke, sw)) g.DrawEllipse(p, r);
                }
            }
        }

        /// <summary>外层假透明面板：画一圈柔和的投影，再交回自定义绘制。</summary>
        private sealed class DialogRoundPanel : Panel
        {
            private readonly int _radius = 12;
            private readonly Color _fill = Color.Transparent;
            private readonly Color _line = Color.Empty;

            internal DialogRoundPanel(Color fill, Color line, int radius)
            {
                _fill = fill;
                _line = line;
                _radius = Math.Max(2, radius);
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                if (_fill == Color.Transparent) return; // 假透明：不擦底，圆角外由父层负责
                Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
                if (_line != Color.Empty) DialogUi.Fill(e.Graphics, r, _radius, _line);
                Rectangle inner = new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
                DialogUi.Fill(e.Graphics, inner, Math.Max(2, _radius - 1), _fill);
            }
        }

        /// <summary>可点击的选项卡片：圆角、图标、标题、说明、可选角标。</summary>
        private sealed class DialogCard : Control
        {
            private readonly string _title, _desc, _tag;
            private readonly bool _primary, _danger;
            private readonly Font _fT, _fD, _fTag;
            private bool _hover;

            internal DialogCard(bool primary, string title, string desc, string tag,
                Font fT, Font fD, Font fTag, bool danger)
            {
                _primary = primary;
                _title = title;
                _desc = desc;
                _tag = tag;
                _fT = fT;
                _fD = fD;
                _fTag = fTag;
                _danger = danger;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Cursor = Cursors.Hand;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // 由 OnPaint 统一绘制
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                // 局部坐标：矩形内缩 1px，让描边完整可见
                Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
                int rad = Math.Max(6, (int)Math.Round(11 * (Width / 442f)));

                Color accent = _danger ? DialogUi.CDanger : DialogUi.CAccent;
                Color bg, border;
                if (_primary)
                {
                    bg = _hover ? Color.FromArgb(0xE8, 0xEF, 0xFF) : DialogUi.CAccentSoft;
                    border = _hover ? accent : DialogUi.CAccentBorder;
                }
                else
                {
                    bg = _hover ? (_danger ? Color.FromArgb(0xFD, 0xF2, 0xF2) : Color.FromArgb(0xF7, 0xF8, 0xFA))
                                : Color.FromArgb(0xFB, 0xFC, 0xFD);
                    border = _hover ? accent : DialogUi.CLine;
                }
                DialogUi.Fill(g, r, rad, bg);
                DialogUi.Stroke(g, r, rad, border, _hover ? 2f : 1f);

                int pad = Math.Max(10, (int)Math.Round(r.Height * 0.23));
                int icon = Math.Max(28, (int)Math.Round(r.Height * 0.5));
                int iy = r.Y + (r.Height - icon) / 2;
                Rectangle ic = new Rectangle(r.X + pad, iy, icon, icon);

                Color iconBg = _primary ? (_danger ? Color.FromArgb(0xFF, 0xE9, 0xE9) : Color.FromArgb(0xE4, 0xEB, 0xFF))
                                        : (_hover && _danger ? Color.FromArgb(0xFF, 0xE4, 0xE4) : Color.FromArgb(0xF0, 0xF1, 0xF4));
                Color iconFg = _primary || _hover ? accent : DialogUi.CGrayIcon;
                DialogUi.Circle(g, ic, iconBg, Color.Empty, 0);

                float gy = icon * 0.3f;
                float gx = icon * 0.5f;
                using (Pen pen = new Pen(iconFg, Math.Max(1.6f, icon * 0.075f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    float cx = ic.X + gx;
                    if (_primary)
                    {
                        // 托盘图标：向下箭头 + 底托
                        g.DrawLine(pen, cx, ic.Y + gy, cx, ic.Y + icon - gy * 1.15f);
                        g.DrawLine(pen, cx - gx * 0.55f, ic.Y + icon - gy * 1.75f, cx, ic.Y + icon - gy * 1.15f);
                        g.DrawLine(pen, cx + gx * 0.55f, ic.Y + icon - gy * 1.75f, cx, ic.Y + icon - gy * 1.15f);
                        g.DrawLine(pen, cx - gx * 0.62f, ic.Y + icon - gy * 0.55f, cx + gx * 0.62f, ic.Y + icon - gy * 0.55f);
                    }
                    else
                    {
                        // 关闭图标：×
                        float k = gx * 0.52f;
                        float cy = ic.Y + icon * 0.5f;
                        g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k);
                        g.DrawLine(pen, cx + k, cy - k, cx - k, cy + k);
                    }
                }

                int tx = ic.Right + pad;
                int avail = r.Right - pad - tx;
                Size ts = TextRenderer.MeasureText(_title, _fT);
                int tagW = 0;
                if (!string.IsNullOrEmpty(_tag))
                {
                    Size gs = TextRenderer.MeasureText(_tag, _fTag);
                    tagW = gs.Width + Math.Max(10, (int)(pad * 0.5));
                    avail -= tagW + 8;
                }
                int dh = TextRenderer.MeasureText(_desc, _fD,
                    new Size(Math.Max(40, avail), 1000), TextFormatFlags.WordBreak).Height;
                int textH = ts.Height + 4 + dh;
                int ty = r.Y + (r.Height - textH) / 2;

                TextRenderer.DrawText(g, _title, _fT, new Point(tx, ty), DialogUi.CText,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

                if (tagW > 0)
                {
                    int th = Math.Max(16, ts.Height - 2);
                    Rectangle tr = new Rectangle(r.Right - pad - tagW, ty + (ts.Height - th) / 2, tagW, th);
                    Color tagBg = _danger ? Color.FromArgb(0xFF, 0xF0, 0xF0) : Color.FromArgb(0xE4, 0xEB, 0xFF);
                    Color tagFg = _danger ? DialogUi.CDanger : DialogUi.CAccent;
                    DialogUi.Fill(g, tr, th / 2, tagBg);
                    TextRenderer.DrawText(g, _tag, _fTag, tr, tagFg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }

                TextRenderer.DrawText(g, _desc, _fD,
                    new Rectangle(tx, ty + ts.Height + 4, Math.Max(40, avail + (tagW > 0 ? tagW + 8 : 0)), dh), DialogUi.CSub,
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

                g.SmoothingMode = SmoothingMode.None;
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                _hover = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hover = false;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                OnClick(EventArgs.Empty);
            }
        }

        /// <summary>胶囊按钮：主色 / 描边两种外观。</summary>
        private sealed class DialogPillButton : Control
        {
            private readonly string _text;
            private readonly bool _primary;
            private readonly Font _font;
            private bool _hover;

            internal DialogPillButton(string text, bool primary, Font font, bool disabled)
            {
                _text = text;
                _primary = primary;
                _font = font;
                Enabled = !disabled;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Cursor = Cursors.Hand;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
                int rad = Math.Max(6, r.Height / 2);

                Color bg, border, fg;
                if (_primary)
                {
                    bg = _hover ? Color.FromArgb(0x3F, 0x5A, 0xE0) : DialogUi.CAccent;
                    border = bg;
                    fg = Color.White;
                }
                else
                {
                    bg = _hover ? Color.FromArgb(0xEF, 0xF1, 0xF5) : Color.White;
                    border = _hover ? Color.FromArgb(0xC9, 0xCE, 0xD6) : DialogUi.CLine;
                    fg = DialogUi.CText;
                }
                if (!Enabled)
                {
                    bg = Color.FromArgb(0xF2, 0xF3, 0xF5);
                    border = DialogUi.CLine;
                    fg = Color.FromArgb(0xA8, 0xAD, 0xB5);
                }
                DialogUi.Fill(g, r, rad, bg);
                DialogUi.Stroke(g, r, rad, border, 1f);
                TextRenderer.DrawText(g, _text, _font, r, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                g.SmoothingMode = SmoothingMode.None;
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                _hover = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hover = false;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                OnClick(EventArgs.Empty);
            }
        }

        /// <summary>关闭方式询问小窗：返回 0=最小化到托盘，1=彻底关闭，-1=取消。</summary>
        private sealed class CloseDialog : Form
        {
            private int _choice = -1;

            public static int Ask(IWin32Window owner)
            {
                using (CloseDialog dlg = new CloseDialog())
                {
                    dlg.ShowDialog(owner);
                    return dlg._choice;
                }
            }

            /// <summary>仅供 --preview-close-dialog 调试：独立展示关闭询问窗。</summary>
            internal static CloseDialog Preview()
            {
                CloseDialog dlg = new CloseDialog();
                dlg.StartPosition = FormStartPosition.CenterScreen;
                return dlg;
            }

            /// <summary>自截图：用窗口内容区的实际矩形截图，避免外部坐标系被 DPI 虚拟化干扰。</summary>
            internal static void SaveShot(Form f, string path)
            {
                try
                {
                    f.Refresh();
                    // 稳定优先：只截窗口在屏幕上的位置（WinForms 自报坐标，与自身渲染同一坐标系）
                    Rectangle r = new Rectangle(
                        f.Left + 8, f.Top + 8, Math.Max(40, f.Width - 16), Math.Max(40, f.Height - 16));
                    using (Bitmap bmp = new Bitmap(r.Width, r.Height))
                    {
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height));
                        }
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    File.WriteAllText(path + ".txt",
                        "form=" + f.Left + "," + f.Top + "," + f.Width + "," + f.Height
                        + " dpi=" + f.DeviceDpi + " U=" + ((double)f.Width / 470.0).ToString("0.###"));
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(path + ".err", ex.ToString()); } catch { }
                }
            }

            // ── 配色（深色标题 / 浅色卡片）─────────────────────────────

            private readonly float U; // 统一缩放：0.75 × (当前 DPI / 96) → 96DPI 下正好是原尺寸的 1.5 倍

            private readonly Font _fTitle, _fSub, _fCardT, _fCardD, _fTag, _fBtn;
            private readonly DialogRoundPanel _shadow;
            private readonly DialogCard _card0, _card1;

            private CloseDialog()
            {
                using (Graphics g = CreateGraphics()) U = 0.75f * (g.DpiX / 96f);
                if (U < 0.5f) U = 0.5f;

                Text = "关闭 DeepSeek Harness";
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterParent;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                AutoScaleMode = AutoScaleMode.None;
                BackColor = DialogUi.CBg;
                KeyPreview = true; // ESC 取消

                _fTitle = new Font("Microsoft YaHei UI", 11.25f * U, FontStyle.Bold);
                _fSub = new Font("Microsoft YaHei UI", 9f * U, FontStyle.Regular);
                _fCardT = new Font("Microsoft YaHei UI", 10.5f * U, FontStyle.Bold);
                _fCardD = new Font("Microsoft YaHei UI", 8.25f * U, FontStyle.Regular);
                _fTag = new Font("Microsoft YaHei UI", 7.5f * U, FontStyle.Bold);
                _fBtn = new Font("Microsoft YaHei UI", 9f * U, FontStyle.Regular);

                int W = Math.Max(430, (int)Math.Round(470 * U));
                int H = Math.Max(300, (int)Math.Round(212 * U));
                int shad = (int)Math.Round(10 * U);
                int inPad = (int)Math.Round(26 * U);

                // 卡片外圈：柔和投影
                _shadow = new DialogRoundPanel(Color.Transparent, Color.Empty, (int)Math.Round(16 * U));
                _shadow.SetBounds(shad, shad, W - shad * 2, H - shad * 2);
                _shadow.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
                Controls.Add(_shadow);

                // 卡片内层：白底圆角 + 1px 描边
                DialogRoundPanel card = new DialogRoundPanel(Color.White, DialogUi.CLine, (int)Math.Round(13 * U));
                card.SetBounds(shad, shad, W - shad * 4, H - shad * 4);
                card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
                _shadow.Controls.Add(card);
                int cw = card.Width - inPad * 2;

                Label title = new Label();
                title.Text = "关闭页面后？";
                title.Font = _fTitle;
                title.ForeColor = DialogUi.CText;
                title.BackColor = Color.Transparent;
                title.AutoSize = false;
                title.SetBounds(inPad, (int)Math.Round(18 * U), cw, (int)Math.Round(32 * U));
                title.MouseDown += delegate(object s, MouseEventArgs e) { DragWindow(); };
                card.Controls.Add(title);

                int cardH = (int)Math.Round(84 * U);
                int gap = (int)Math.Round(12 * U);
                int row0 = (int)Math.Round(56 * U);

                _card0 = new DialogCard(true, "最小化到托盘",
                    "引擎与手机访问留在后台，双击托盘图标唤回",
                    "推荐", _fCardT, _fCardD, _fTag, false);
                _card0.SetBounds(inPad, row0, cw, cardH);
                _card0.Click += delegate { _choice = 0; Close(); };
                card.Controls.Add(_card0);

                _card1 = new DialogCard(false, "彻底关闭",
                    "页面与后台服务全部退出",
                    null, _fCardT, _fCardD, _fTag, true);
                _card1.SetBounds(inPad, row0 + cardH + gap, cw, cardH);
                _card1.Click += delegate { _choice = 1; Close(); };
                card.Controls.Add(_card1);

                DialogPillButton cancel = new DialogPillButton("取消", false, _fBtn, false);
                cancel.SetBounds(card.Width - inPad - (int)Math.Round(124 * U),
                    row0 + cardH * 2 + gap + (int)Math.Round(8 * U),
                    (int)Math.Round(124 * U), (int)Math.Round(40 * U));
                cancel.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
                cancel.Click += delegate { _choice = -1; Close(); };
                card.Controls.Add(cancel);

                ClientSize = new Size(W, H);

                AcceptButton = null;
                KeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.KeyCode == Keys.Escape)
                    {
                        _choice = -1;
                        Close();
                    }
                    else if (e.KeyCode == Keys.Enter)
                    {
                        _choice = 0; // Enter＝最小化到托盘（安全默认）
                        Close();
                    }
                    else if (e.KeyCode == Keys.D1 || e.KeyCode == Keys.NumPad1)
                    {
                        _choice = 0; Close();
                    }
                    else if (e.KeyCode == Keys.D2 || e.KeyCode == Keys.NumPad2)
                    {
                        _choice = 1; Close();
                    }
                };
            }

            /// <summary>按住卡片空白处拖动窗口（无边框窗体）。</summary>
            private void DragWindow()
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                // Win11：请系统给无边框窗体加圆角 + 投影
                try
                {
                    int pref = 2; // DWMWCP_ROUND
                    DwmSetWindowAttribute(Handle, 33, ref pref, 4);
                    int shadow = 2;
                    DwmSetWindowAttribute(Handle, 2, ref shadow, 4);
                }
                catch
                {
                }
            }
        }
    }
}
