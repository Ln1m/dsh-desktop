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

            public MainForm()
            {
                Text = "DeepSeek Harness";
                AutoScaleMode = AutoScaleMode.None;
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
                    if (_overlay != null)
                    {
                        _overlay.Visible = true;
                        _overlay.Text = "正在连接 DSH 服务…" + Environment.NewLine + reason;
                    }
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
                if (_overlay != null) _overlay.Text = "后端还没就绪，正在重试…";
                ScheduleRetry();
            }

            private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (_navTimer != null) _navTimer.Stop();
                if (e.IsSuccess)
                {
                    if (_overlay != null) _overlay.Visible = false;
                    return;
                }
                // 失败：短暂等一次再重试；若引擎已不在，顺手把它拉起来
                if (!Program.PortOpen()) Program.StartServer();
                if (_overlay != null) _overlay.Text = "页面加载失败，正在重试…";
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

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
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
                    await web.EnsureCoreWebView2Async(null);
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
