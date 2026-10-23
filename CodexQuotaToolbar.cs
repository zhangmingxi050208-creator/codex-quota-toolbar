using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexQuotaToolbar
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            NativeMethods.TryEnablePerMonitorDpi();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            using (var mutex = new Mutex(true, "CodexQuotaToolbar.SingleInstance", out created))
            {
                if (!created) return;
                Application.Run(new QuotaToolbarForm());
            }
        }
    }

    internal sealed class QuotaWindow
    {
        public double UsedPercent;
        public int DurationMinutes;
        public DateTime ResetAtLocal;
        public double RemainingPercent { get { return Math.Max(0, Math.Min(100, 100 - UsedPercent)); } }
    }

    internal sealed class QuotaSnapshot
    {
        public QuotaWindow Primary;
        public QuotaWindow Secondary;
        public string Error;
        public DateTime UpdatedAt;
    }

    internal sealed class QuotaToolbarForm : Form
    {
        private const int ToolbarWidth = 580;
        private const int ToolbarHeight = 62;
        private readonly System.Windows.Forms.Timer _layoutTimer;
        private readonly System.Windows.Forms.Timer _clockTimer;
        private readonly ContextMenuStrip _menu;
        private QuotaSnapshot _snapshot;
        private DateTime _nextRefresh = DateTime.MinValue;
        private bool _refreshing;
        private IntPtr _codexWindow;
        private bool _codexPresent;
        private Rectangle _lastPlacement = Rectangle.Empty;
        private int _lostForegroundTicks;
        private string _status = "正在读取额度…";

        public QuotaToolbarForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(244, 248, 251);
            ClientSize = new Size(ToolbarWidth, ToolbarHeight);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "CodexQuotaToolbar";
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Codex 额度";
            TopMost = true;

            _menu = new ContextMenuStrip();
            _menu.Items.Add("立即刷新", null, delegate { RefreshQuota(true); });
            _menu.Items.Add("打开工具目录", null, delegate
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = true
                });
            });
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("退出", null, delegate { Close(); });
            ContextMenuStrip = _menu;

            _layoutTimer = new System.Windows.Forms.Timer { Interval = 300 };
            _layoutTimer.Tick += delegate { FollowCodexWindow(); };
            _clockTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _clockTimer.Tick += delegate
            {
                if (_codexPresent && DateTime.Now >= _nextRefresh) RefreshQuota(false);
                Invalidate();
            };

            MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) RefreshQuota(true);
            };
            Shown += delegate
            {
                _layoutTimer.Start();
                _clockTimer.Start();
                FollowCodexWindow();
            };
            FormClosed += delegate
            {
                _layoutTimer.Stop();
                _clockTimer.Stop();
            };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateWindowRegion();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (IsHandleCreated) UpdateWindowRegion();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_NOACTIVATE = 0x08000000;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (var path = BottomRoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 10))
            using (var bg = new SolidBrush(Color.FromArgb(244, 248, 251)))
            using (var border = new Pen(Color.FromArgb(211, 222, 230)))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            if (_snapshot == null || _snapshot.Primary == null)
            {
                using (var font = new Font("Microsoft YaHei UI", 15f, FontStyle.Regular))
                using (var brush = new SolidBrush(Color.FromArgb(72, 84, 99)))
                    g.DrawString(_status, font, brush, new RectangleF(16, 14, Width - 32, 32));
                return;
            }

            int half = Width / 2;
            DrawQuota(g, new Rectangle(0, 0, half, Height), _snapshot.Primary, true);
            DrawQuota(g, new Rectangle(half, 0, Width - half, Height), _snapshot.Secondary, false);
            using (var separator = new Pen(Color.FromArgb(211, 222, 230)))
                g.DrawLine(separator, half, 7, half, Height - 8);
        }

        private void DrawQuota(Graphics g, Rectangle bounds, QuotaWindow window, bool primary)
        {
            if (window == null) return;
            var remaining = (int)Math.Round(window.RemainingPercent);
            var title = DurationLabel(window.DurationMinutes);
            var reset = primary ? Countdown(window.ResetAtLocal) : window.ResetAtLocal.ToString("MM.dd HH:mm");
            var accent = QuotaColor(remaining, primary);

            using (var titleFont = new Font("Microsoft YaHei UI", 15f, FontStyle.Regular))
            using (var valueFont = new Font("Microsoft YaHei UI", 16.5f, FontStyle.Bold))
            using (var resetFont = new Font("Segoe UI", 12.5f, FontStyle.Regular))
            using (var textBrush = new SolidBrush(Color.FromArgb(48, 62, 76)))
            using (var accentBrush = new SolidBrush(accent))
            using (var resetBrush = new SolidBrush(Color.FromArgb(117, 132, 147)))
            using (var trackBrush = new SolidBrush(Color.FromArgb(218, 227, 234)))
            {
                float x = bounds.Left + 16;
                g.DrawString(title, titleFont, textBrush, x, 13f);
                float titleWidth = g.MeasureString(title, titleFont).Width - 1;
                g.DrawString(remaining + "%", valueFont, accentBrush, x + titleWidth + 3, 10.5f);
                var resetSize = g.MeasureString(reset, resetFont);
                g.DrawString(reset, resetFont, resetBrush, bounds.Right - resetSize.Width - 15, 15f);

                var track = new Rectangle(bounds.Left + 9, Height - 7, bounds.Width - 18, 4);
                g.FillRectangle(trackBrush, track);
                var fillWidth = (int)Math.Round(track.Width * window.RemainingPercent / 100.0);
                if (fillWidth > 0) g.FillRectangle(accentBrush, track.Left, track.Top, fillWidth, track.Height);
            }
        }

        private static Color QuotaColor(int remaining, bool primary)
        {
            if (remaining < 20) return Color.FromArgb(255, 59, 48);
            if (remaining < 60) return Color.FromArgb(255, 176, 0);
            return Color.FromArgb(34, 197, 94);
        }

        private static string DurationLabel(int minutes)
        {
            if (minutes == 300) return "5小时 ";
            if (minutes == 10080) return "7天 ";
            if (minutes >= 1440) return (minutes / 1440) + "天 ";
            return Math.Max(1, minutes / 60) + "小时 ";
        }

        private static string Countdown(DateTime target)
        {
            var span = target - DateTime.Now;
            if (span.TotalSeconds <= 0) return "刷新中";
            if (span.TotalDays >= 1) return ((int)span.TotalDays) + "天 " + span.Hours.ToString("00") + ":" + span.Minutes.ToString("00");
            return ((int)span.TotalHours).ToString("00") + ":" + span.Minutes.ToString("00");
        }

        private void RefreshQuota(bool force)
        {
            if (_refreshing) return;
            if (!_codexPresent) return;
            if (!force && DateTime.Now < _nextRefresh) return;
            _refreshing = true;
            _status = "正在读取额度…";
            Invalidate();

            Task.Factory.StartNew<QuotaSnapshot>(() => QuotaClient.Fetch()).ContinueWith(task =>
            {
                BeginInvoke((Action)delegate
                {
                    _refreshing = false;
                    if (task.IsFaulted)
                    {
                        _status = "额度读取失败 · 单击重试";
                        _nextRefresh = DateTime.Now.AddSeconds(20);
                    }
                    else
                    {
                        var result = task.Result;
                        if (!string.IsNullOrEmpty(result.Error))
                        {
                            _status = "额度读取失败 · 单击重试";
                            _nextRefresh = DateTime.Now.AddSeconds(20);
                        }
                        else
                        {
                            _snapshot = result;
                            _nextRefresh = DateTime.Now.AddMinutes(1);
                        }
                    }
                    Invalidate();
                });
            });
        }

        private void FollowCodexWindow()
        {
            if (_codexWindow == IntPtr.Zero || !NativeMethods.IsWindow(_codexWindow))
                _codexWindow = FindCodexWindow();

            if (_codexWindow == IntPtr.Zero || NativeMethods.IsIconic(_codexWindow) || !NativeMethods.IsWindowVisible(_codexWindow))
            {
                _codexPresent = false;
                _lostForegroundTicks = 0;
                if (Visible) Hide();
                return;
            }

            if (!_codexPresent)
            {
                _codexPresent = true;
                _nextRefresh = DateTime.MinValue;
                RefreshQuota(true);
            }

            var foreground = NativeMethods.GetForegroundWindow();
            var foregroundRoot = NativeMethods.GetAncestor(foreground, 3);
            var codexRoot = NativeMethods.GetAncestor(_codexWindow, 3);
            bool belongsToCodex = foreground == _codexWindow || foregroundRoot == codexRoot || foreground == Handle;
            if (!belongsToCodex)
            {
                // Codex can briefly hand focus to a transient system/popup window.
                // Waiting for three ticks avoids a visible hide/show flash.
                _lostForegroundTicks++;
                if (_lostForegroundTicks >= 3 && Visible) Hide();
                return;
            }

            _lostForegroundTicks = 0;

            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(_codexWindow, out rect)) return;
            int windowWidth = rect.Right - rect.Left;
            int sidebar = Math.Min(280, Math.Max(0, (int)(windowWidth * 0.46)));
            int contentWidth = Math.Max(ToolbarWidth, windowWidth - sidebar);
            int x = rect.Left + sidebar + Math.Max(0, (contentWidth - ToolbarWidth) / 2);
            int y = rect.Top + 1;

            var placement = new Rectangle(x, y, ToolbarWidth, ToolbarHeight);
            if (placement != _lastPlacement)
            {
                NativeMethods.SetWindowPos(Handle, new IntPtr(-1), x, y, ToolbarWidth, ToolbarHeight, 0x0010 | 0x0200);
                _lastPlacement = placement;
            }
            if (!Visible) NativeMethods.ShowWindow(Handle, 4);
        }

        private static IntPtr FindCodexWindow()
        {
            foreach (var name in new[] { "Codex", "ChatGPT" })
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(process.MainWindowTitle))
                            return process.MainWindowHandle;
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
            return IntPtr.Zero;
        }

        private void UpdateWindowRegion()
        {
            var oldRegion = Region;
            using (var path = BottomRoundedRect(new Rectangle(0, 0, Width, Height), 10))
                Region = new Region(path);
            if (oldRegion != null) oldRegion.Dispose();
        }

        private static GraphicsPath BottomRoundedRect(Rectangle rect, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.StartFigure();
            path.AddLine(rect.Left, rect.Top, rect.Right, rect.Top);
            path.AddLine(rect.Right, rect.Top, rect.Right, rect.Bottom - radius);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.AddLine(rect.Left, rect.Bottom - radius, rect.Left, rect.Top);
            path.CloseFigure();
            return path;
        }
    }

    internal static class QuotaClient
    {
        public static QuotaSnapshot Fetch()
        {
            var result = new QuotaSnapshot { UpdatedAt = DateTime.Now };
            Process process = null;
            try
            {
                var codex = FindCodexBinary();
                if (string.IsNullOrEmpty(codex)) throw new FileNotFoundException("找不到 Codex runtime");

                var psi = new ProcessStartInfo
                {
                    FileName = codex,
                    Arguments = "app-server",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                process = Process.Start(psi);
                process.ErrorDataReceived += delegate { };
                process.BeginErrorReadLine();
                process.StandardInput.WriteLine("{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"codex_quota_toolbar\",\"title\":\"Codex Quota Toolbar\",\"version\":\"1.0.0\"}}}");
                process.StandardInput.WriteLine("{\"method\":\"initialized\",\"params\":{}}");
                process.StandardInput.WriteLine("{\"method\":\"account/rateLimits/read\",\"id\":2,\"params\":{}}");
                process.StandardInput.Flush();

                var readTask = Task.Factory.StartNew(() => ReadResponse(process));
                if (!readTask.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Codex app-server 响应超时");
                return readTask.Result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
            finally
            {
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    process.Dispose();
                }
            }
        }

        private static QuotaSnapshot ReadResponse(Process process)
        {
            string line;
            var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                object rootObject;
                try { rootObject = serializer.DeserializeObject(line); }
                catch { continue; }
                var root = rootObject as Dictionary<string, object>;
                if (root == null || !root.ContainsKey("id") || Convert.ToInt32(root["id"]) != 2) continue;
                if (root.ContainsKey("error")) throw new InvalidOperationException("Codex 返回额度错误");
                var response = Dict(root, "result");
                var limits = Dict(response, "rateLimits");
                if (limits == null)
                {
                    var byId = Dict(response, "rateLimitsByLimitId");
                    if (byId != null && byId.ContainsKey("codex")) limits = byId["codex"] as Dictionary<string, object>;
                }
                if (limits == null) throw new InvalidDataException("额度响应中没有 rateLimits");
                return new QuotaSnapshot
                {
                    Primary = ParseWindow(Dict(limits, "primary")),
                    Secondary = ParseWindow(Dict(limits, "secondary")),
                    UpdatedAt = DateTime.Now
                };
            }
            throw new EndOfStreamException("Codex app-server 已关闭");
        }

        private static QuotaWindow ParseWindow(Dictionary<string, object> value)
        {
            if (value == null) return null;
            return new QuotaWindow
            {
                UsedPercent = Number(value, "usedPercent"),
                DurationMinutes = (int)Number(value, "windowDurationMins"),
                ResetAtLocal = UnixToLocal((long)Number(value, "resetsAt"))
            };
        }

        private static Dictionary<string, object> Dict(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.ContainsKey(key)) return null;
            return source[key] as Dictionary<string, object>;
        }

        private static double Number(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.ContainsKey(key) || source[key] == null) return 0;
            return Convert.ToDouble(source[key], System.Globalization.CultureInfo.InvariantCulture);
        }

        private static DateTime UnixToLocal(long seconds)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();
        }

        private static string FindCodexBinary()
        {
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
                if (Directory.Exists(root))
                {
                    var candidate = Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories)
                        .Select(path => new FileInfo(path))
                        .OrderByDescending(file => file.LastWriteTimeUtc)
                        .FirstOrDefault();
                    if (candidate != null) return candidate.FullName;
                }
            }
            catch { }

            foreach (var process in Process.GetProcessesByName("codex"))
            {
                try
                {
                    var path = process.MainModule.FileName;
                    if (File.Exists(path)) return path;
                }
                catch { }
                finally { process.Dispose(); }
            }
            return null;
        }
    }

    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int command);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        internal static void TryEnablePerMonitorDpi()
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        }
    }
}

