using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopDance
{
    /// <summary>
    /// 舞台上的画面：遮罩（壁纸）+ 图标副本 + 9:16 取景框。
    /// 和窗口解耦，所以也能离屏渲染成图片做自测。
    /// 传进来的姿态是「原点左下、y 向上」的舞台坐标，这里翻成屏幕的「y 向下」。
    /// </summary>
    public sealed class StageHost : FrameworkElement
    {
        sealed class Node
        {
            public readonly ContainerVisual Root = new ContainerVisual();
            public readonly MatrixTransform Xf = new MatrixTransform();
            public DrawingVisual? Label;
            public double LastAlpha = -1;
        }

        readonly VisualCollection visuals;
        readonly DrawingVisual curtain = new DrawingVisual();
        readonly ContainerVisual iconLayer = new ContainerVisual();
        readonly DrawingVisual guide = new DrawingVisual();
        readonly List<Node> nodes = new List<Node>();
        readonly double stageW, stageH;
        int[] order = Array.Empty<int>();
        double[] zs = Array.Empty<double>();

        public int Count => nodes.Count;

        public StageHost(double width, double height, double iconDip, double labelWidth, double pixelsPerDip, IReadOnlyList<DancerItem> items)
        {
            stageW = width; stageH = height;
            Width = width; Height = height;
            visuals = new VisualCollection(this);
            visuals.Add(curtain);
            visuals.Add(iconLayer);
            visuals.Add(guide);

            // 桌面图标名字用的字体（系统设置里的「图标」字体），白字加一圈暗影
            FontFamily family = SystemFonts.IconFontFamily ?? new FontFamily("Segoe UI");
            var typeface = new Typeface(family, SystemFonts.IconFontStyle, SystemFonts.IconFontWeight, FontStretches.Normal);
            double fontSize = SystemFonts.IconFontSize;
            if (double.IsNaN(fontSize) || fontSize < 8 || fontSize > 40) fontSize = 12;
            var shadow = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
            shadow.Freeze();
            var placeholder = new SolidColorBrush(Color.FromArgb(200, 150, 150, 150));
            placeholder.Freeze();
            double s = iconDip;
            var shadowOffsets = new[] { new Vector(0, 1), new Vector(1, 1), new Vector(-1, 1), new Vector(1, 0), new Vector(-1, 0), new Vector(0, 2) };

            for (int i = 0; i < items.Count; i++)
            {
                DancerItem it = items[i];
                var node = new Node();
                node.Root.Transform = node.Xf;

                var img = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
                using (DrawingContext dc = img.RenderOpen())
                {
                    BitmapSource? icon = it.Icon;
                    if (icon != null && icon.PixelWidth > 0 && icon.PixelHeight > 0)
                    {
                        double k = Math.Min(s / icon.PixelWidth, s / icon.PixelHeight);
                        double dw = icon.PixelWidth * k, dh = icon.PixelHeight * k;
                        dc.DrawImage(icon, new Rect(-dw / 2, -dh / 2, dw, dh));
                    }
                    else
                    {
                        dc.DrawRoundedRectangle(placeholder, null, new Rect(-s / 2, -s / 2, s, s), s * 0.18, s * 0.18);
                    }
                }
                node.Root.Children.Add(img);

                if (!string.IsNullOrEmpty(it.Name))
                {
                    try
                    {
                        var ft = new FormattedText(it.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.White, pixelsPerDip)
                        {
                            MaxTextWidth = Math.Max(40, labelWidth),
                            MaxLineCount = 2,
                            Trimming = TextTrimming.CharacterEllipsis,
                            TextAlignment = TextAlignment.Center,
                        };
                        var lab = new DrawingVisual();
                        using (DrawingContext dc = lab.RenderOpen())
                        {
                            var origin = new Point(-ft.MaxTextWidth / 2, s / 2 + 3);
                            ft.SetForegroundBrush(shadow);
                            foreach (Vector o in shadowOffsets) dc.DrawText(ft, origin + o);
                            ft.SetForegroundBrush(Brushes.White);
                            dc.DrawText(ft, origin);
                        }
                        node.Root.Children.Add(lab);
                        node.Label = lab;
                    }
                    catch (Exception ex) { Log.Write("画图标名字失败 " + it.Name + "：" + ex.Message); }
                }
                node.Root.Opacity = 0;
                nodes.Add(node);
            }
            order = new int[nodes.Count];
            zs = new double[nodes.Count];
            for (int i = 0; i < nodes.Count; i++) { order[i] = i; iconLayer.Children.Add(nodes[i].Root); }
        }

        protected override int VisualChildrenCount => visuals.Count;
        protected override Visual GetVisualChild(int index) => visuals[index];
        protected override Size MeasureOverride(Size availableSize) => new Size(stageW, stageH);

        /// <summary>有图片就盖壁纸；没有图片时用纯色盖住真实图标。</summary>
        public void SetCurtain(BitmapSource? image, Color background)
        {
            using (DrawingContext dc = curtain.RenderOpen())
            {
                var b = new SolidColorBrush(background);
                b.Freeze();
                dc.DrawRectangle(b, null, new Rect(0, 0, stageW, stageH));
                if (image != null) dc.DrawImage(image, new Rect(0, 0, stageW, stageH));
            }
        }

        /// <summary>画 9:16 取景框的四个角。rect 是舞台坐标（y 向上）；传 null 不画。</summary>
        public void SetGuide(RectD? rect)
        {
            using (DrawingContext dc = guide.RenderOpen())
            {
                if (rect == null) return;
                RectD r = rect.Value;
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 2);
                pen.Freeze();
                double top = stageH - r.MaxY, bottom = stageH - r.MinY;
                const double k = 26;
                var corners = new (double x, double y, double dx, double dy)[]
                {
                    (r.MinX, top, 1, 1), (r.MaxX, top, -1, 1), (r.MinX, bottom, 1, -1), (r.MaxX, bottom, -1, -1),
                };
                foreach (var (x, y, dx, dy) in corners)
                {
                    dc.DrawLine(pen, new Point(x + dx * k, y), new Point(x, y));
                    dc.DrawLine(pen, new Point(x, y), new Point(x, y + dy * k));
                }
            }
        }

        /// <summary>hiddenIndex：即使显示名称，也把这个图标的名字藏起来（当篮球的那个，不然名字跟着球一起转）。</summary>
        public void SetLabelsVisible(bool on, int hiddenIndex)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                DrawingVisual? lab = nodes[i].Label;
                if (lab != null) lab.Opacity = on && i != hiddenIndex ? 1 : 0;
            }
        }

        public void Apply(Pose[] poses)
        {
            int n = Math.Min(nodes.Count, poses.Length);
            bool resort = false;
            for (int i = 0; i < n; i++)
            {
                Pose q = poses[i];
                Node node = nodes[i];
                if (double.IsNaN(q.P.X) || double.IsNaN(q.P.Y) || double.IsNaN(q.Rot) || double.IsNaN(q.Sx) || double.IsNaN(q.Sy) ||
                    double.IsInfinity(q.P.X) || double.IsInfinity(q.P.Y))
                    continue;                                          // 坏数据：这一帧这个图标不动
                Matrix m = Matrix.Identity;
                m.Scale(q.Sx, q.Sy);
                m.Rotate(-q.Rot * 180 / Math.PI);                      // 舞台坐标 y 向上，屏幕 y 向下，所以角度取反
                m.Translate(q.P.X, stageH - q.P.Y);
                node.Xf.Matrix = m;
                double a = M.Clamp(q.Alpha, 0, 1);
                if (a != node.LastAlpha) { node.Root.Opacity = a; node.LastAlpha = a; }
                if (zs[i] != q.Z) { zs[i] = q.Z; resort = true; }
            }
            if (resort) Reorder();
        }

        /// <summary>谁压在谁上面变了：按 z 重新排一遍（平时每一帧都不变，不会走到这里）。</summary>
        void Reorder()
        {
            bool sorted = true;
            for (int k = 1; k < order.Length; k++)
            {
                if (zs[order[k - 1]] > zs[order[k]]) { sorted = false; break; }
            }
            if (sorted) return;
            var idx = new int[order.Length];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            Array.Sort(idx, (a, b) => { int c = zs[a].CompareTo(zs[b]); return c != 0 ? c : a.CompareTo(b); });
            order = idx;
            iconLayer.Children.Clear();
            foreach (int i in order) iconLayer.Children.Add(nodes[i].Root);
        }

        /// <summary>离屏渲染成图片（自测用）。</summary>
        public RenderTargetBitmap RenderToBitmap()
        {
            Measure(new Size(stageW, stageH));
            Arrange(new Rect(0, 0, stageW, stageH));
            var rtb = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(stageW)), Math.Max(1, (int)Math.Ceiling(stageH)), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(this);
            return rtb;
        }
    }

    /// <summary>
    /// 盖在桌面图标正上方的窗口，铺满主显示器。
    /// 它是一个普通窗口，但永远待在所有窗口的最底下、不抢焦点，看起来就是桌面本身。
    /// 它只活在本进程里：进程一结束（正常退出、崩溃、被任务管理器结束都一样），
    /// 系统会立刻把窗口收走，真实桌面原样露出来。程序没有对资源管理器做任何改动，所以没有东西需要「恢复」。
    /// </summary>
    public sealed class StageWindow : Window
    {
        public StageHost Host { get; }
        public event Action? Clicked;
        readonly ScreenInfo screen;
        IntPtr hwnd;
        bool pinnedAboveDesktop;      // 退路生效后，不再把自己往最底下按

        /// <summary>开窗后检查到的层级情况（写进日志和状态栏，方便排查）。</summary>
        public string LayerNote { get; private set; } = "";      // 写日志用，中文
        /// <summary>层级不正常时给用户看的话（中英文）；正常时是空的。</summary>
        public Msg LayerProblem { get; private set; } = Msg.None;

        public StageWindow(ScreenInfo sc, StageHost host)
        {
            screen = sc;
            Host = host;
            Title = "DesktopDance Stage";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = false;
            Focusable = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = sc.MonX / sc.Scale;
            Top = sc.MonY / sc.Scale;
            Width = sc.MonW / sc.Scale;
            Height = sc.MonH / sc.Scale;
            Background = Brushes.Black;
            host.HorizontalAlignment = HorizontalAlignment.Left;
            host.VerticalAlignment = VerticalAlignment.Top;
            Content = host;
            // 有遮罩时真实图标看不见，点一下桌面就停止（免得隔着遮罩误操作）
            MouseDown += (s, e) => { e.Handled = true; Clicked?.Invoke(); };
        }

        public void Open()
        {
            var helper = new WindowInteropHelper(this);
            hwnd = helper.EnsureHandle();
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
            HwndSource? src = HwndSource.FromHwnd(hwnd);
            src?.AddHook(WndProc);
            ToBottom();
            Show();
            ToBottom();
            CheckLayer();
        }

        void ToBottom()
        {
            if (hwnd == IntPtr.Zero) return;
            Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, screen.MonX, screen.MonY, screen.MonW, screen.MonH, Native.SWP_NOACTIVATE);
        }

        /// <summary>装着桌面图标的那个顶层窗口（通常是 Progman；某些情况下是一个 WorkerW）。只是找到它，不碰它。</summary>
        static IntPtr FindDesktopHost()
        {
            IntPtr progman = Native.FindWindow("Progman", null);
            if (progman != IntPtr.Zero && Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return progman;
            IntPtr w = IntPtr.Zero;
            for (int guard = 0; guard < 200; guard++)
            {
                w = Native.FindWindowEx(IntPtr.Zero, w, "WorkerW", null);
                if (w == IntPtr.Zero) break;
                if (Native.FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return w;
            }
            return IntPtr.Zero;
        }

        /// <summary>a 是不是压在 b 上面。</summary>
        static bool IsAbove(IntPtr a, IntPtr b)
        {
            IntPtr w = a;
            for (int guard = 0; guard < 5000 && w != IntPtr.Zero; guard++)
            {
                w = Native.GetWindow(w, Native.GW_HWNDNEXT);
                if (w == b) return true;
            }
            return false;
        }

        /// <summary>
        /// 确认舞台确实在桌面图标的上方。正常情况下「放到最底」就是「只比桌面高一层」；
        /// 万一这台电脑上桌面窗口反而压在了我们上面，就把自己挪到它的正上方（只挪自己的窗口）。
        /// </summary>
        void CheckLayer()
        {
            try
            {
                IntPtr host = FindDesktopHost();
                if (host == IntPtr.Zero)
                {
                    LayerNote = "没找到桌面窗口，无法确认层级";
                    LayerProblem = new Msg("舞台层级：" + LayerNote, "Stage layer: desktop window not found, so the layer could not be confirmed");
                }
                else if (!IsAbove(host, hwnd)) { LayerNote = "在桌面图标之上（正常）"; LayerProblem = Msg.None; }
                else
                {
                    IntPtr prev = Native.GetWindow(host, Native.GW_HWNDPREV);
                    if (prev != IntPtr.Zero && prev != hwnd)
                    {
                        pinnedAboveDesktop = true;
                        Native.SetWindowPos(hwnd, prev, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    }
                    bool stuck = IsAbove(host, hwnd);
                    LayerNote = stuck ? "被桌面盖住了，挪不上去（看到的会是真实桌面）" : "被桌面盖住过，已挪到桌面图标正上方";
                    LayerProblem = new Msg("舞台层级：" + LayerNote,
                                           stuck ? "Stage layer: covered by the desktop and could not be moved up (you will see the real desktop)"
                                                 : "Stage layer: was covered by the desktop; moved to just above the desktop icons");
                }
            }
            catch (Exception ex)
            {
                LayerNote = "检查层级出错：" + ex.Message;
                LayerProblem = new Msg("舞台层级：" + LayerNote, "Stage layer: the check failed: " + ex.Message);
            }
            Log.Write("舞台窗口层级：" + LayerNote);
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_WINDOWPOSCHANGING && lParam != IntPtr.Zero)
            {
                // 不管谁想把它往上提，都按回最底下
                WINDOWPOS wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                if (!pinnedAboveDesktop && (wp.flags & Native.SWP_NOZORDER) == 0 && wp.hwndInsertAfter != Native.HWND_BOTTOM)
                {
                    wp.hwndInsertAfter = Native.HWND_BOTTOM;
                    Marshal.StructureToPtr(wp, lParam, false);
                }
            }
            else if (msg == Native.WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            }
            return IntPtr.Zero;
        }
    }
}
