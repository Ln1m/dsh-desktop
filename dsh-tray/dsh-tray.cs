// DSH Tray - system tray companion for DeepSeek Harness.
// ─────────────────────────────────────────────────────────────────────────────
// 2026-09-12 重做（用户要求）：
//   ① 关闭 dsh-desktop 窗口后不再弹任何「已驻留/已在运行」气泡或提示框（静默驻留）。
//   ② 单例冲突时静默退出，绝不弹 MessageBox。
//   ③ 只保留一个退出入口「退出 DSH」：点它 = 关窗口 → 停 3081 反代 → 停 3080 引擎 → 退出托盘。
//      删掉了原来「退出托盘（只退托盘、服务继续跑）」这种半退出入口 —— 它会留下无人守护的
//      3080/3081，与「托盘在 = 服务挂起」的设计冲突。
//   ④ 托盘右键菜单改为自绘：深色圆角卡片、品牌头 + 状态灯、图标行、危险色退出项。
// 编译（UTF-8 源，codepage 65001，.NET Framework csc，输出到 build\ 以免锁住运行中的 exe）：
//   csc /nologo /target:winexe /out:build\DSH-Tray.exe /codepage:65001
//       /reference:System.Windows.Forms.dll /reference:System.Drawing.dll dsh-tray.cs
// 调试预览（自截图菜单，不驻留）：DSH-Tray.exe --shot <输出前缀>
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DshTray
{
    // ─────────────────────────────────────────────────────────────
    // 入口
    // ─────────────────────────────────────────────────────────────
    static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }

            if (args != null && args.Length > 0 && args[0] == "--shot")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string prefix = args.Length > 1 ? args[1] : Cfg.Dir + "\\build\\tray";
                Application.Run(new ShotContext(prefix));
                return;
            }

            bool created;
            using (Mutex m = new Mutex(true, "DSH_Tray_Singleton_Mutex", out created))
            {
                // 已在运行：静默退出。这里以前会弹「DSH 常驻托盘已在运行中」的提示框，
                // 关闭 dsh-desktop 窗口时若触发二次拉起就会弹出，已按要求去掉。
                if (!created) return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 路径与端口
    // ─────────────────────────────────────────────────────────────
    static class Cfg
    {
        // DSH 安装根：默认 %USERPROFILE%\DeepSeek_harness，可用环境变量 DSH_ROOT 覆盖。
        public static readonly string Root =
            Environment.GetEnvironmentVariable("DSH_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeepSeek_harness");
        public static readonly string Dir      = Root + "\\dsh-tray";
        public static readonly string AppExe   = Root + "\\apps\\dsh-desktop\\dsh-desktop.exe";
        public static readonly string AppDir   = Root + "\\apps\\dsh-desktop";
        public static readonly string IconFile = Root + "\\assets\\deepseek_harness.ico";
        public static readonly string BrandPng = Root + "\\assets\\deepseek-icon-64.png";
        public static readonly string NodeExe  = Environment.GetEnvironmentVariable("DSH_NODE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        public static readonly string DshBin   = Root + "\\node_modules\\.bin\\..\\@deepseek-ai\\dsh\\lib\\bin.js";
        public static readonly string WorkDir  = Root + "";
        public static readonly string LogFile  = Root + "\\dsh-tray\\logs\\web.log";
        public const int EnginePort  = 3080;   // 引擎（Web UI）
        public const int LanPort     = 3081;   // 手机局域网反代（dsh-wifi-access 插件内）
        // 引擎启停的跨进程互斥名：托盘与 dsh-desktop 共用，谁先拿到谁负责拉起 3080。
        public const string EngineGate = "Local\\DSH_Engine_Start_Gate";
    }

    // ─────────────────────────────────────────────────────────────
    // 视觉主题（深色卡片风）
    // ─────────────────────────────────────────────────────────────
    static class Theme
    {
        public static float U = 1f;
        public static Font MainFont, SubFont, HeadFont;

        public static readonly Color Bg       = Color.FromArgb(26, 28, 33);
        public static readonly Color Border   = Color.FromArgb(58, 62, 71);
        public static readonly Color Hover    = Color.FromArgb(44, 48, 56);
        public static readonly Color TextMain = Color.FromArgb(234, 236, 239);
        public static readonly Color TextDim  = Color.FromArgb(148, 154, 164);
        public static readonly Color Accent   = Color.FromArgb(77, 107, 254);
        public static readonly Color Ok       = Color.FromArgb(61, 207, 133);
        public static readonly Color Off      = Color.FromArgb(118, 124, 134);
        public static readonly Color Danger   = Color.FromArgb(255, 108, 108);
        public static readonly Color DangerBg = Color.FromArgb(60, 34, 39);
        public static readonly Color Sep      = Color.FromArgb(48, 51, 58);

        public static int S(int px) { return (int)Math.Round(px * U); }
        public static float F(float px) { return Math.Max(1f, px * U); }

        public static void Init()
        {
            // 字号用 pt 且不再乘 U：GDI+ 会按 Graphics 的 DpiY 换算 pt→px，高 DPI 下已自动放大，
            // 再乘一次 U 就成了双重放大（200% 屏上字体撑爆菜单行宽，实测过）。
            MainFont = new Font("Microsoft YaHei UI", 9.5f);
            SubFont  = new Font("Microsoft YaHei UI", 8f);
            HeadFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
        }

        /// <summary>按当前系统 DPI 求缩放（进程已声明 DPI aware，绘制与字号都要自己缩放）。</summary>
        public static float DetectScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float u = g.DpiX / 96f;
                    if (u < 1f) u = 1f;
                    if (u > 3f) u = 3f;
                    return u;
                }
            }
            catch { return 1f; }
        }

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0 || r.Width <= 0 || r.Height <= 0) { p.AddRectangle(r); return p; }
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Bitmap _brand;

        /// <summary>
        /// 品牌图标位图，取不到时返回 null 由调用方退化绘制。
        /// 优先用 PNG：deepseek_harness.ico 内含 PNG 压缩帧，Icon.ToBitmap() 会解出彩色噪点。
        /// </summary>
        public static Bitmap Brand(int px)
        {
            if (_brand != null) return _brand;
            try
            {
                _brand = new Bitmap(Cfg.BrandPng);
                return _brand;
            }
            catch { }
            try
            {
                using (FileStream fs = new FileStream(Cfg.IconFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (Icon ic = new Icon(fs))
                {
                    _brand = ic.ToBitmap();
                }
            }
            catch { _brand = null; }
            return _brand;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 菜单项语义
    // ─────────────────────────────────────────────────────────────
    enum ItemKind { Header, Plain, Status, Danger }

    class MenuMeta
    {
        public ItemKind Kind;
        public string Glyph;   // window | play | power
        public string Sub;     // 副标题 / 右侧状态值
        public bool On;        // 状态灯是否点亮
        public MenuMeta(ItemKind kind) { Kind = kind; }
    }

    /// <summary>
    /// 固定宽度的托盘菜单：宽度由我们自己定（FixedWidth），不随文本长短跳动。
    /// </summary>
    class DshMenu : ContextMenuStrip
    {
        public int FixedWidth;

        public override Size GetPreferredSize(Size constrainingSize)
        {
            Size s = base.GetPreferredSize(constrainingSize);
            if (FixedWidth > 0 && s.Width < FixedWidth) s.Width = FixedWidth;
            return s;
        }
    }

    /// <summary>
    /// 固定宽度的菜单项：ToolStripMenuItem 的 PreferredSize 会按文本量算宽度，
    /// 把 AutoSize=false 下我们设定的 Size.Width 顶掉（实测菜单被压到 388 而非 500）。
    /// 这里把设定宽度并回 PreferredSize，菜单宽度才由我们说了算。
    /// </summary>
    class DshMenuItem : ToolStripMenuItem
    {
        public DshMenuItem(string text) : base(text) { }

        public override Size GetPreferredSize(Size constrainingSize)
        {
            Size s = base.GetPreferredSize(constrainingSize);
            if (!AutoSize && Size.Width > s.Width) s.Width = Size.Width;
            return s;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 自绘渲染
    // ─────────────────────────────────────────────────────────────
    class DshColorTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
        public override Color MenuItemBorder { get { return Color.Transparent; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color ToolStripDropDownBackground { get { return Theme.Bg; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Bg; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Bg; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Bg; } }
        public override Color SeparatorDark { get { return Theme.Sep; } }
        public override Color SeparatorLight { get { return Theme.Sep; } }
    }

    class DshRenderer : ToolStripProfessionalRenderer
    {
        public DshRenderer() : base(new DshColorTable()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Theme.Bg))
                e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.ToolStrip.Size));
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (GraphicsPath p = Theme.Rounded(r, Theme.S(10)))
            using (Pen pen = new Pen(Theme.Border, Theme.F(1f)))
                e.Graphics.DrawPath(pen, p);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            MenuMeta meta = e.Item.Tag as MenuMeta;
            if (meta == null || meta.Kind == ItemKind.Header) return;
            if (!e.Item.Selected && !e.Item.Pressed) return;
            Rectangle r = new Rectangle(Theme.S(5), Theme.S(2), e.Item.Width - Theme.S(10), e.Item.Height - Theme.S(4));
            if (r.Width <= 0 || r.Height <= 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg = (meta.Kind == ItemKind.Danger) ? Theme.DangerBg : Theme.Hover;
            using (GraphicsPath p = Theme.Rounded(r, Theme.S(7)))
            using (SolidBrush b = new SolidBrush(bg))
                e.Graphics.FillPath(b, p);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            Rectangle r = new Rectangle(Theme.S(15), y, Math.Max(1, e.Item.Width - Theme.S(30)), 1);
            using (SolidBrush b = new SolidBrush(Theme.Sep))
                e.Graphics.FillRectangle(b, r);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            MenuMeta meta = e.Item.Tag as MenuMeta;
            if (meta == null) { base.OnRenderItemText(e); return; }
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            switch (meta.Kind)
            {
                case ItemKind.Header: PaintHeader(g, e.Item, meta); break;
                case ItemKind.Status: PaintStatus(g, e.Item, meta); break;
                case ItemKind.Danger: PaintRow(g, e.Item, meta, Theme.Danger); break;
                default: PaintRow(g, e.Item, meta, Theme.TextMain); break;
            }
        }

        // ── 行绘制 ──────────────────────────────────────────────
        private static void PaintRow(Graphics g, ToolStripItem item, MenuMeta meta, Color color)
        {
            // 不可用的行（服务运行时的「启动 DSH 服务」）整行压暗，避免看着像可点的第三入口。
            if (!item.Enabled && meta.Kind != ItemKind.Danger) color = Theme.Off;
            int cy = item.Height / 2;
            int gs = Theme.S(18);
            int x = Theme.S(16);
            if (!string.IsNullOrEmpty(meta.Glyph))
            {
                DrawGlyph(g, meta.Glyph, new Rectangle(x, cy - gs / 2, gs, gs), color);
            }
            x += gs + Theme.S(12);
            using (SolidBrush b = new SolidBrush(color))
            using (StringFormat sf = TextFormat())
            {
                RectangleF box = new RectangleF(x, 0, item.Width - x - Theme.S(16), item.Height);
                g.DrawString(item.Text, Theme.MainFont, b, box, sf);
            }
        }

        private static void PaintStatus(Graphics g, ToolStripItem item, MenuMeta meta)
        {
            int cy = item.Height / 2;
            int dot = Theme.S(8);
            Color dotColor = meta.On ? Theme.Ok : Theme.Off;
            using (SolidBrush b = new SolidBrush(dotColor))
                g.FillEllipse(b, Theme.S(16), cy - dot / 2, dot, dot);

            int x = Theme.S(16) + dot + Theme.S(11);
            using (SolidBrush b = new SolidBrush(Theme.TextDim))
            using (StringFormat sf = TextFormat())
            {
                g.DrawString(item.Text, Theme.MainFont, b,
                    new RectangleF(x, 0, item.Width - x - Theme.S(86), item.Height), sf);
            }
            using (SolidBrush b = new SolidBrush(dotColor))
            using (StringFormat sf = TextFormat())
            {
                sf.Alignment = StringAlignment.Far;
                g.DrawString(meta.Sub, Theme.MainFont, b,
                    new RectangleF(0, 0, item.Width - Theme.S(18), item.Height), sf);
            }
        }

        private static void PaintHeader(Graphics g, ToolStripItem item, MenuMeta meta)
        {
            int pad = Theme.S(16);
            int logo = Theme.S(30);
            int top = Theme.S(11);

            Bitmap brand = Theme.Brand(logo);
            if (brand != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(brand, new Rectangle(pad, top, logo, logo));
            }
            else
            {
                Rectangle lr = new Rectangle(pad, top, logo, logo);
                using (GraphicsPath p = Theme.Rounded(lr, Theme.S(8)))
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillPath(b, p);
                using (SolidBrush b = new SolidBrush(Color.White))
                using (StringFormat sf = TextFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    g.DrawString("D", Theme.HeadFont, b, new RectangleF(lr.X, lr.Y, lr.Width, lr.Height), sf);
                }
            }

            int tx = pad + logo + Theme.S(12);
            int w = item.Width - tx - pad;
            using (SolidBrush b = new SolidBrush(Theme.TextMain))
            using (StringFormat sf = TextFormat())
            {
                sf.Alignment = StringAlignment.Near;
                g.DrawString(item.Text, Theme.HeadFont, b,
                    new RectangleF(tx, top - Theme.S(1), w, Theme.S(20)), sf);
            }

            int dot = Theme.S(7);
            int sy = top + Theme.S(30);
            Color c = meta.On ? Theme.Ok : Theme.Off;
            using (SolidBrush b = new SolidBrush(c))
                g.FillEllipse(b, tx, sy - dot / 2, dot, dot);
            using (SolidBrush b = new SolidBrush(Theme.TextDim))
            using (StringFormat sf = TextFormat())
            {
                sf.Alignment = StringAlignment.Near;
                g.DrawString(meta.Sub, Theme.SubFont, b,
                    new RectangleF(tx + dot + Theme.S(8), sy - Theme.S(9), w, Theme.S(18)), sf);
            }
        }

        private static StringFormat TextFormat()
        {
            StringFormat sf = new StringFormat();
            sf.LineAlignment = StringAlignment.Center;
            sf.Alignment = StringAlignment.Near;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            return sf;
        }

        private static void DrawGlyph(Graphics g, string glyph, Rectangle r, Color color)
        {
            switch (glyph)
            {
                case "window":
                    using (Pen p = new Pen(color, Theme.F(1.4f)))
                    {
                        Rectangle w = new Rectangle(r.X, r.Y + Theme.S(3), r.Width, r.Height - Theme.S(6));
                        using (GraphicsPath path = Theme.Rounded(w, Theme.S(3)))
                            g.DrawPath(p, path);
                        g.DrawLine(p, w.X + 1, w.Y + Theme.S(4), w.Right - 1, w.Y + Theme.S(4));
                    }
                    break;
                case "play":
                    PointF[] tri = new PointF[] {
                        new PointF(r.X + r.Width * 0.30f, r.Y + r.Height * 0.20f),
                        new PointF(r.X + r.Width * 0.30f, r.Y + r.Height * 0.80f),
                        new PointF(r.X + r.Width * 0.80f, r.Y + r.Height * 0.50f) };
                    using (SolidBrush b = new SolidBrush(color))
                        g.FillPolygon(b, tri);
                    break;
                case "power":
                    using (Pen p = new Pen(color, Theme.F(1.6f)))
                    {
                        p.StartCap = LineCap.Round;
                        p.EndCap = LineCap.Round;
                        Rectangle a = new Rectangle(r.X + Theme.S(3), r.Y + Theme.S(4), r.Width - Theme.S(6), r.Height - Theme.S(6));
                        g.DrawArc(p, a, -50f, 280f);
                        g.DrawLine(p, r.X + r.Width / 2, r.Y + Theme.S(1), r.X + r.Width / 2, r.Y + Theme.S(9));
                    }
                    break;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 托盘 UI：图标 + 自绘菜单 + 状态刷新 + 退出确认
    // ─────────────────────────────────────────────────────────────
    class TrayUi : IDisposable
    {
        public readonly NotifyIcon Notify;
        public readonly DshMenu Menu;
        public readonly ToolStripMenuItem HeaderItem, StartItem, EngineItem, LanItem, QuitItem;

        public event EventHandler OpenRequested;
        public event EventHandler StartRequested;
        public event EventHandler QuitRequested;   // 单击即触发，无二次确认

        private bool _engineUp, _lanUp, _starting;

        public TrayUi(bool withIcon)
        {
            Menu = new DshMenu();
            Menu.FixedWidth = Theme.S(250);
            Menu.Renderer = new DshRenderer();
            Menu.ShowImageMargin = false;
            Menu.DropShadowEnabled = true;
            Menu.BackColor = Theme.Bg;
            Menu.Font = Theme.MainFont;
            Menu.Padding = new Padding(0, Theme.S(7), 0, Theme.S(7));
            Menu.SizeChanged += delegate { ApplyRegion(); };

            MenuMeta head = new MenuMeta(ItemKind.Header);
            head.Sub = "检测中...";
            HeaderItem = MakeItem("DeepSeek Harness", head, 60, null);
            HeaderItem.Enabled = false;

            MenuMeta open = new MenuMeta(ItemKind.Plain);
            open.Glyph = "window";
            ToolStripMenuItem openItem = MakeItem("打开 DeepSeek Harness", open, 34,
                delegate { Fire(OpenRequested); });

            MenuMeta start = new MenuMeta(ItemKind.Plain);
            start.Glyph = "play";
            StartItem = MakeItem("启动 DSH 服务", start, 34, delegate { Fire(StartRequested); });

            MenuMeta eng = new MenuMeta(ItemKind.Status);
            eng.Sub = "未运行";
            EngineItem = MakeItem("引擎端口 3080", eng, 30, null);
            EngineItem.Enabled = false;

            MenuMeta lan = new MenuMeta(ItemKind.Status);
            lan.Sub = "未监听";
            LanItem = MakeItem("手机访问 3081", lan, 30, null);
            LanItem.Enabled = false;

            MenuMeta quit = new MenuMeta(ItemKind.Danger);
            quit.Glyph = "power";
            // 2026-09-12：取消二次确认。菜单是点一下即收起的，确认态根本来不及点第二下
            // （用户实测：点一次菜单就关了，还得重新右键唤出，比直接退更麻烦）——单击直通退出。
            QuitItem = MakeItem("退出 DSH（关闭全部服务）", quit, 38, delegate { Fire(QuitRequested); });

            Menu.Items.Add(HeaderItem);
            Menu.Items.Add(Sep());
            Menu.Items.Add(openItem);
            Menu.Items.Add(StartItem);
            Menu.Items.Add(Sep());
            Menu.Items.Add(EngineItem);
            Menu.Items.Add(LanItem);
            Menu.Items.Add(Sep());
            Menu.Items.Add(QuitItem);

            Menu.Opened += delegate { ApplyRegion(); };

            if (withIcon)
            {
                Notify = new NotifyIcon();
                Notify.Icon = LoadIcon();
                Notify.Text = "DSH 常驻托盘";
                Notify.ContextMenuStrip = Menu;
                Notify.Visible = true;
                Notify.DoubleClick += delegate { Fire(OpenRequested); };
            }
        }

        private static ToolStripMenuItem MakeItem(string text, MenuMeta meta, int height, EventHandler onClick)
        {
            ToolStripMenuItem it = new DshMenuItem(text);
            it.Tag = meta;
            it.AutoSize = false;
            it.Size = new Size(Theme.S(250), Theme.S(height));
            it.Padding = Padding.Empty;
            it.Margin = Padding.Empty;
            it.ForeColor = Theme.TextMain;
            it.BackColor = Color.Transparent;
            if (onClick != null) it.Click += onClick;
            return it;
        }

        private static ToolStripSeparator Sep()
        {
            ToolStripSeparator s = new ToolStripSeparator();
            s.Margin = new Padding(0, Theme.S(5), 0, Theme.S(5));
            return s;
        }

        private void Fire(EventHandler h) { if (h != null) h(this, EventArgs.Empty); }

        private void ApplyRegion()
        {
            if (Menu.Width <= 0 || Menu.Height <= 0) return;
            using (GraphicsPath p = Theme.Rounded(new Rectangle(0, 0, Menu.Width, Menu.Height), Theme.S(11)))
            {
                Region old = Menu.Region;
                Menu.Region = new Region(p);
                if (old != null) old.Dispose();
            }
        }

        // ── 状态刷新 ────────────────────────────────────────────
        public void SetEngineState(bool engineUp, bool lanUp, bool starting)
        {
            _engineUp = engineUp;
            _lanUp = lanUp;
            _starting = starting;

            MenuMeta head = (MenuMeta)HeaderItem.Tag;
            head.On = engineUp;
            head.Sub = engineUp
                ? (lanUp ? "运行中 · 引擎 3080 · 手机 3081" : "运行中 · 手机反代未监听")
                : (starting ? "正在启动..." : "未运行");

            MenuMeta eng = (MenuMeta)EngineItem.Tag;
            eng.On = engineUp;
            eng.Sub = engineUp ? "运行中" : (starting ? "启动中" : "未运行");

            MenuMeta lan = (MenuMeta)LanItem.Tag;
            lan.On = lanUp;
            lan.Sub = lanUp ? "运行中" : "未监听";

            StartItem.Enabled = !engineUp && !starting;

            if (Notify != null)
                Notify.Text = engineUp ? "DSH 运行中 · 双击打开" : "DSH 未运行 · 双击启动";

            Menu.Invalidate();
        }

        public void MarkQuitting(string text)
        {
            QuitItem.Text = text;
            QuitItem.Enabled = false;
            Menu.Invalidate();
        }

        // ── 退出 ────────────────────────────────────────────────
        // 不再有确认态：菜单项点击即收起，二次确认反而要求用户重新唤出菜单，已于 2026-09-12 去掉。
        // 退出入口只此一个，点击后由 TrayContext.QuitDshFully 完成「关窗口 + 停 3080/3081 + 退托盘」。

        private static Icon LoadIcon()
        {
            try
            {
                using (FileStream fs = new FileStream(Cfg.IconFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    return new Icon(fs);
            }
            catch { }
            return FallbackIcon();
        }

        private static Icon FallbackIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (GraphicsPath path = Theme.Rounded(new Rectangle(1, 1, 30, 30), 8))
                using (SolidBrush bg = new SolidBrush(Theme.Accent))
                    g.FillPath(bg, path);
                using (Font f = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush fg = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("D", f, fg, new RectangleF(0f, -1f, 32f, 32f), sf);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        public void Dispose()
        {
            try { if (Notify != null) { Notify.Visible = false; Notify.Dispose(); } } catch { }
            try { Menu.Dispose(); } catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 托盘主体：守护引擎 + 打开窗口 + 唯一退出入口
    // ─────────────────────────────────────────────────────────────
    class TrayContext : ApplicationContext
    {
        private readonly TrayUi _ui;
        private readonly System.Windows.Forms.Timer _watch;
        private readonly System.Windows.Forms.Timer _bootTimer;
        private volatile bool _starting;
        private bool _quitting;

        public TrayContext()
        {
            Theme.U = Theme.DetectScale();
            Theme.Init();
            try { Directory.CreateDirectory(Path.GetDirectoryName(Cfg.LogFile)); } catch { }

            _ui = new TrayUi(true);
            _ui.OpenRequested += delegate { OpenApp(); };
            _ui.StartRequested += delegate { StartDsh(); };
            _ui.QuitRequested += delegate { QuitDshFully(); };

            _watch = new System.Windows.Forms.Timer();
            _watch.Interval = 4000;
            _watch.Tick += delegate { RefreshStatus(); };
            _watch.Start();

            // 登录或被 dsh-desktop 拉起后延迟一次引擎自检：端口不通才拉起。
            // 是否真由本进程启动由跨进程启动门（Cfg.EngineGate）裁定 —— 这里的延迟只管时机，
            // 不再承担「躲开 dsh-desktop 的启动窗口」这种正确性职责，慢机器也能正确工作。
            _bootTimer = new System.Windows.Forms.Timer();
            _bootTimer.Interval = 8000;
            _bootTimer.Tick += delegate
            {
                _bootTimer.Stop();
                if (!PortOpen(Cfg.EnginePort)) StartDsh();
            };
            _bootTimer.Start();

            RefreshStatus();
            // 不再弹「已驻留系统托盘」气泡：关闭 dsh-desktop 窗口触发的静默驻留不应打扰用户。
        }

        private void RefreshStatus()
        {
            if (_quitting) return;
            bool up = PortOpen(Cfg.EnginePort);
            bool lan = PortOpen(Cfg.LanPort);
            _ui.SetEngineState(up, lan, _starting);
        }

        // ── 唯一退出入口：关窗口 → 停 3081 → 停 3080 → 退出托盘 ──
        private void QuitDshFully()
        {
            if (_quitting) return;
            _quitting = true;
            _ui.MarkQuitting("正在退出...");
            _watch.Stop();
            if (_bootTimer != null) _bootTimer.Stop();

            try
            {
                foreach (Process p in Process.GetProcessesByName("dsh-desktop"))
                {
                    try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); }
                    catch { }
                }
            }
            catch { }

            // 3080 与 3081 常由同一个 node 进程监听，按 PID 去重后各杀一次。
            HashSet<int> pids = FindListenerPids(new int[] { Cfg.LanPort, Cfg.EnginePort });
            foreach (int pid in pids)
            {
                try
                {
                    Process p = Process.GetProcessById(pid);
                    string name = p.ProcessName;
                    p.Kill();
                    AppendLog("[quit] 已结束监听进程 PID " + pid + "（" + name + "）");
                }
                catch (Exception ex)
                {
                    AppendLog("[quit] 结束 PID " + pid + " 失败：" + ex.Message);
                }
            }
            Thread.Sleep(300);
            AppendLog("[quit] 退出：引擎 3080 与手机反代 3081 已停，托盘退出");

            _ui.Dispose();
            ExitThread();
        }

        /// <summary>按端口找 owner PID（netstat 解析，不依赖 NetTCPIP 模块）。</summary>
        private static HashSet<int> FindListenerPids(int[] ports)
        {
            HashSet<int> pids = new HashSet<int>();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "netstat.exe";
                psi.Arguments = "-ano -p tcp";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
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
                            string[] parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length < 5) continue;
                            bool hit = false;
                            foreach (int port in ports)
                            {
                                if (parts[1].EndsWith(":" + port, StringComparison.Ordinal)) { hit = true; break; }
                            }
                            if (!hit) continue;
                            int pid;
                            if (int.TryParse(parts[parts.Length - 1], out pid) && pid > 0) pids.Add(pid);
                        }
                    }
                }
            }
            catch { }
            return pids;
        }

        private static bool PortOpen(int port)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult r = c.BeginConnect("127.0.0.1", port, null, null);
                    if (!r.AsyncWaitHandle.WaitOne(500)) return false;
                    c.EndConnect(r);
                    return c.Connected;
                }
            }
            catch { return false; }
        }

        private void OpenApp()
        {
            if (!PortOpen(Cfg.EnginePort)) StartDsh();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Cfg.AppExe;
                psi.WorkingDirectory = Cfg.AppDir;
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                _ui.Notify.ShowBalloonTip(2500, "DSH 常驻托盘",
                    "打开 DeepSeek Harness 失败：" + ex.Message, ToolTipIcon.Error);
            }
        }

        private void StartDsh()
        {
            if (_starting || _quitting) return;
            _starting = true;
            RefreshStatus();
            ThreadPool.QueueUserWorkItem(delegate { StartDshGuarded(); });
        }

        /// <summary>
        /// 跨进程启动门：托盘与 dsh-desktop 抢同一个命名 Mutex，抢到的一方负责把引擎拉起来，
        /// 并持有到 3080 就绪（或 90 秒超时）才放手；抢不到说明另一方正在启动，直接返回。
        /// 两边不会再各拉一个引擎撞同一个端口（历史 EADDRINUSE 来源），启动时机也不再依赖固定延迟。
        /// 全过程在线程池线程，托盘菜单与状态刷新不被阻塞。
        /// </summary>
        private void StartDshGuarded()
        {
            Mutex gate = null;
            bool got = false;
            try
            {
                gate = new Mutex(false, Cfg.EngineGate);
                try { got = gate.WaitOne(0, false); }
                catch (AbandonedMutexException) { got = true; }
                if (!got) return;
                if (PortOpen(Cfg.EnginePort)) return;

                Process p = new Process();
                p.StartInfo.FileName = Cfg.NodeExe;
                // 2026-09-14: 补 --no-open。dsh web 的 openBrowser 默认 true，托盘自检
                // 拉起服务时会把默认浏览器弹到 3080 页面（用户在移动端「重启」后同样中招，
                // 那条路径已在 scripts\dsh-restart-instance.ps1 里注入 --no-open）。
                // 需要看界面时走「打开 DeepSeek Harness」→ OpenApp，它起的是 dsh-desktop。
                p.StartInfo.Arguments = "\"" + Cfg.DshBin + "\" web --host 127.0.0.1 --no-open";
                p.StartInfo.WorkingDirectory = Cfg.WorkDir;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { AppendLog(e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { AppendLog(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                for (int i = 0; i < 180 && !PortOpen(Cfg.EnginePort) && !_quitting; i++) Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                AppendLog("[start] 拉起引擎失败：" + ex.Message);
                try
                {
                    _ui.Notify.ShowBalloonTip(3000, "DSH 常驻托盘",
                        "启动 DSH 服务失败：" + ex.Message, ToolTipIcon.Error);
                }
                catch { }
            }
            finally
            {
                _starting = false;
                if (got) { try { gate.ReleaseMutex(); } catch { } }
                if (gate != null) { try { gate.Close(); } catch { } }
            }
        }

        private static void AppendLog(string line)
        {
            if (line == null) return;
            try
            {
                File.AppendAllText(Cfg.LogFile,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 预览：--shot 打开菜单并自截图三个状态（不驻留、不动服务）
    // ─────────────────────────────────────────────────────────────
    class ShotContext : ApplicationContext
    {
        private readonly TrayUi _ui;
        private readonly string _prefix;
        private readonly System.Windows.Forms.Timer _timer;
        private int _step;

        public ShotContext(string prefix)
        {
            _prefix = prefix;
            Theme.U = Theme.DetectScale();
            Theme.Init();
            _ui = new TrayUi(false);
            _ui.SetEngineState(true, true, false);
            _ui.Menu.Show(new Point(260, 260));
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 900;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private void Dump()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("U=" + Theme.U + " menu=" + _ui.Menu.Size + " client=" + _ui.Menu.ClientSize
                    + " autosize=" + _ui.Menu.AutoSize);
                foreach (ToolStripItem it in _ui.Menu.Items)
                {
                    sb.AppendLine(it.GetType().Name + " auto=" + it.AutoSize + " size=" + it.Size
                        + " pref=" + it.GetPreferredSize(Size.Empty) + " text=" + it.Text);
                }
                File.WriteAllText(_prefix + "-dump.txt", sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private void OnTick(object sender, EventArgs e)
        {
            _step++;
            if (_step == 1)
            {
                Shoot("01-running");
                _ui.SetEngineState(false, false, false);
            }
            else if (_step == 2)
            {
                Shoot("02-stopped");
            }
            else
            {
                _timer.Stop();
                _ui.Dispose();
                ExitThread();
            }
        }

        private void Shoot(string tag)
        {
            try
            {
                Rectangle r = _ui.Menu.Bounds;
                if (r.Width <= 0 || r.Height <= 0) return;
                using (Bitmap bmp = new Bitmap(r.Width, r.Height))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height));
                    bmp.Save(_prefix + "-" + tag + ".png", ImageFormat.Png);
                }
            }
            catch { }
        }
    }
}
