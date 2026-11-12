using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopDance
{
    /// <summary>
    /// 实跑测试（GitHub 上自动构建时会跑；自己也可以跑）：和平时一样把程序开起来，然后由脚本代替人去点。
    ///
    ///   DesktopDance.exe --livetest 输出文件夹
    ///       开始跳 → 换四个模式 → 跳舞中换语言、换数量 → 用三种方式各恢复一次（按 Esc、「恢复桌面」、点桌面），
    ///       每次恢复后核对：舞台窗口没了、每个桌面图标的位置和开始前一样。结果写进 livetest.txt，并留下截屏。
    ///   DesktopDance.exe --livetest-hold 输出文件夹
    ///       开始跳，然后一直跳着不动，等外面把这个进程强制结束（模拟任务管理器里的「结束任务」）。
    ///   DesktopDance.exe --livetest-after 输出文件夹
    ///       上面那个进程被强制结束之后运行：核对舞台窗口没了、图标位置没变。
    ///
    /// 「算失败」的只有安全相关的几条：能开始、能恢复、恢复后舞台窗口没了、图标位置没变。
    /// 其余（舞台是不是正好垫在桌面图标上面、模拟的 Esc 有没有生效、画面比对）只做记录，因为它们取决于跑测试的那台机器。
    ///
    /// 注意：只有这三个测试模式会截屏、会模拟按键和鼠标点击；平时运行的程序不做这些事。
    /// 测试期间不写设置文件（Settings.NoSave）。
    /// </summary>
    public sealed class LiveTest
    {
        const string StageTitle = "DesktopDance Stage";

        readonly Application app;
        readonly DanceController c;
        readonly PanelWindow panel;
        readonly string outDir;
        readonly bool hold;
        readonly Report rep;
        public int ExitCode { get; private set; } = 2;      // 脚本没跑完就退出 = 2

        LiveTest(Application app, DanceController c, PanelWindow panel, string outDir, bool hold)
        {
            this.app = app; this.c = c; this.panel = panel; this.outDir = outDir; this.hold = hold;
            rep = new Report(Path.Combine(outDir, hold ? "livetest-hold.txt" : "livetest.txt"));
        }

        /// <summary>程序照常启动之后调用：把测试脚本排进界面线程，脚本跑完会让程序退出。</summary>
        public static LiveTest Attach(Application app, DanceController c, PanelWindow panel, string outDir, bool hold)
        {
            try { Directory.CreateDirectory(outDir); } catch { }
            var t = new LiveTest(app, c, panel, outDir, hold);
            // 保险：脚本卡住的话，到点直接结束进程（舞台窗口属于本进程，进程没了它就没了）
            var watchdog = new Thread(() =>
            {
                Thread.Sleep(hold ? 200000 : 170000);
                t.rep.Fail("超时：测试脚本没有在规定时间内跑完，直接结束进程");
                CopyLog(outDir);
                Environment.Exit(2);
            }) { IsBackground = true, Name = "livetest-watchdog" };
            watchdog.Start();
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (hold) await t.RunHold(); else await t.RunFull();
                }
                catch (Exception ex)
                {
                    t.rep.Fail("测试脚本抛出异常：" + ex);
                    try { c.EmergencyRestore(); } catch { }
                    t.ExitCode = 1;
                }
                CopyLog(outDir);
                app.Shutdown();
            }));
            return t;
        }

        // ================= 完整的一轮 =================

        async Task RunFull()
        {
            rep.Line("桌面图标跳舞 Windows 版 · 实跑测试 · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            rep.Line("系统：" + Environment.OSVersion.VersionString + "，界面语言：" + (L.Current == Lang.Zh ? "中文" : "English"));
            RECT work = WorkArea();
            int sw = TestNative.GetSystemMetrics(0), sh = TestNative.GetSystemMetrics(1);
            rep.Line("主屏 " + sw + "x" + sh + "，工作区 " + (work.Right - work.Left) + "x" + (work.Bottom - work.Top));
            rep.Line("");

            await WaitUntil(() => c.CatalogLoaded, 40);
            rep.Note("桌面上有 " + c.Catalog.Desktop.Count + " 个图标（控制窗口里的数）");
            await Task.Delay(800);
            await PanelShots();
            RECT panelRect = PanelRect();
            Dictionary<string, POINT>? before = await ReadPositions();
            if (before == null) rep.Note("读不到桌面图标的位置：后面「位置没变」这一项没法核对");
            else rep.Note("开始前读到 " + before.Count + " 个桌面图标的位置");
            BitmapSource? shotBefore = Shot("1-before.png");
            List<Probe> probesBefore = ProbePoints(work, panelRect);
            int deskPoints = probesBefore.FindAll(p => p.IsDesktop).Count;
            rep.Note("取了 " + probesBefore.Count + " 个探测点，其中 " + deskPoints + " 个点下面是桌面，其余是别的窗口：" + Summary(probesBefore));

            // ---------- 第一轮：开始 → 四个模式 → 换语言 → 换数量 → 按 Esc ----------
            rep.Line("");
            rep.Line("== 第一轮：开始跳，最后按 Esc ==");
            c.Start();
            bool running = await WaitUntil(() => c.State == DanceState.Running, 45);
            rep.Check(running, "点「开始」后进入跳舞状态");
            if (!running) { rep.Note("状态文字：" + OneLine(c.StatusText)); c.EmergencyRestore(); ExitCode = 1; return; }
            await Task.Delay(3500);
            IntPtr stage = Native.FindWindow(null, StageTitle);
            rep.Check(stage != IntPtr.Zero && Native.IsWindowVisible(stage), "舞台窗口已经打开");
            rep.Note("舞台窗口层级（程序自己的检查）：" + c.StageLayerNote);
            rep.Note("帧率 " + c.Fps + " fps，Esc 热键" + (c.HotKeys.EscapeRegistered ? "已注册" : "没注册上") + "，" + c.AudioDebug);
            rep.Note("状态文字：" + OneLine(c.StatusText));
            LayerCheck(probesBefore, stage, "打篮球");
            BitmapSource? shotDance = Shot("2-basketball.png");
            if (shotBefore != null && shotDance != null)
            {
                double d = Diff(shotBefore, shotDance, work, panelRect);
                rep.Soft(d > 0.002, "跳舞时的画面和开始前不一样（工作区里 " + (d * 100).ToString("0.00") + "% 的像素变了；一点没变说明舞台没露出来）");
            }

            int shotNo = 3;
            foreach (DanceMode m in DanceModes.All)
            {
                if (m == DanceMode.Figure) continue;
                c.Mode = m;
                await Task.Delay(2600);
                rep.Check(c.State == DanceState.Running, "切到「" + DanceModes.LogName(m) + "」后还在跳（" + c.Fps + " fps）");
                Shot(shotNo + "-" + m.ToString().ToLowerInvariant() + ".png");
                shotNo++;
            }
            c.Mode = DanceMode.Figure;
            await Task.Delay(1500);

            // 跳舞中换语言：只换字，不能打断跳舞
            Lang original = L.Current, other = original == Lang.Zh ? Lang.En : Lang.Zh;
            panel.SelfTestSwitchLanguage(other);
            await Task.Delay(1500);
            rep.Check(c.State == DanceState.Running && c.Fps > 0, "跳舞中把语言换成" + (other == Lang.Zh ? "中文" : " English") + "：还在跳（" + c.Fps + " fps）");
            rep.Note("换语言后的状态文字：" + OneLine(c.StatusText));
            Shot("6-language-switched.png");
            panel.SelfTestSwitchLanguage(original);
            await Task.Delay(600);

            // 跳舞中改数量：过一两秒按新数量重排
            c.TargetCount = 24;
            await Task.Delay(4500);
            rep.Check(c.State == DanceState.Running, "跳舞中把数量改成 24 后还在跳");
            rep.Note("改数量后的状态文字：" + OneLine(c.StatusText));
            Shot("7-count-24.png");

            // 按 Esc（模拟真的按键，走全局热键那条路）
            TestNative.keybd_event(0x1B, 0, 0, UIntPtr.Zero);
            TestNative.keybd_event(0x1B, 0, 2, UIntPtr.Zero);
            bool escWorked = await WaitUntil(() => c.State == DanceState.Idle, 4);
            rep.Soft(escWorked, "模拟按下 Esc 后停止并恢复（走的是全局热键；测试机不接受模拟按键时这一项会不成立）");
            if (!escWorked) c.EmergencyRestore();
            await AfterRestore(before, "按 Esc 后");
            await Task.Delay(700);
            BitmapSource? shotAfter1 = Shot("8-after-esc.png");
            if (shotBefore != null && shotAfter1 != null)
            {
                double d = Diff(shotBefore, shotAfter1, work, panelRect);
                rep.Soft(d < 0.01, "恢复后的画面和开始前一样（工作区里只有 " + (d * 100).ToString("0.00") + "% 的像素不同）");
            }

            // ---------- 第二轮：「恢复桌面」按钮（图标飞回原位再撤舞台）----------
            rep.Line("");
            rep.Line("== 第二轮：用「恢复桌面」停止 ==");
            c.TargetCount = 48;
            c.Start();
            running = await WaitUntil(() => c.State == DanceState.Running, 45);
            rep.Check(running, "第二次开始后进入跳舞状态");
            await Task.Delay(2500);
            c.Stop(true);
            bool idle = await WaitUntil(() => c.State == DanceState.Idle, 8);
            rep.Check(idle, "「恢复桌面」后回到就绪状态");
            if (!idle) c.EmergencyRestore();
            await AfterRestore(before, "「恢复桌面」后");

            // ---------- 第三轮：在桌面上点一下 ----------
            rep.Line("");
            rep.Line("== 第三轮：在桌面上点一下停止 ==");
            c.Start();
            running = await WaitUntil(() => c.State == DanceState.Running, 45);
            rep.Check(running, "第三次开始后进入跳舞状态");
            await Task.Delay(2500);
            stage = Native.FindWindow(null, StageTitle);
            Probe? target = null;
            foreach (Probe p in ProbePoints(work, panelRect)) if (p.Root == stage && stage != IntPtr.Zero) { target = p; break; }
            if (target == null) rep.Soft(false, "找不到一个最上面就是舞台的点，没法试「点桌面停止」");
            else
            {
                TestNative.GetCursorPos(out POINT old);
                TestNative.SetCursorPos(target.Pt.X, target.Pt.Y);
                await Task.Delay(120);
                TestNative.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                TestNative.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                bool clicked = await WaitUntil(() => c.State == DanceState.Idle, 8);
                TestNative.SetCursorPos(old.X, old.Y);
                rep.Soft(clicked, "在舞台上 (" + target.Pt.X + "," + target.Pt.Y + ") 模拟点一下后停止并恢复");
            }
            if (c.State != DanceState.Idle) c.Stop(false);
            await WaitUntil(() => c.State == DanceState.Idle, 5);
            await AfterRestore(before, "点桌面后");
            await Task.Delay(700);
            BitmapSource? shotEnd = Shot("9-after.png");
            if (shotBefore != null && shotEnd != null)
            {
                double d = Diff(shotBefore, shotEnd, work, panelRect);
                rep.Soft(d < 0.01, "全部结束后的画面和开始前一样（工作区里只有 " + (d * 100).ToString("0.00") + "% 的像素不同）");
            }

            rep.Line("");
            rep.Line(rep.Failed ? "结论：有失败项。" : "结论：安全相关的项全部通过（能开始、能恢复、恢复后舞台窗口没了、图标位置没变）。带「记录」的项见上，不算通过或失败。");
            ExitCode = rep.Failed ? 1 : 0;
        }

        /// <summary>
        /// 真窗口的样子：就绪状态下，从截屏里把控制窗口那一块裁出来，中文、英文各存一张
        /// （panel-window-zh.png / panel-window-en.png）。这是屏幕上真正显示的窗口，带标题栏。
        /// </summary>
        async Task PanelShots()
        {
            Lang original = L.Current;
            try
            {
                foreach (Lang lg in new[] { original, original == Lang.Zh ? Lang.En : Lang.Zh })
                {
                    if (lg != L.Current)
                    {
                        panel.SelfTestSwitchLanguage(lg);
                        await Task.Delay(900);
                    }
                    BitmapSource? full = Capture();
                    RECT r = VisiblePanelRect();
                    if (full == null) { rep.Note("截不到屏，没有控制窗口的图"); break; }
                    int x = Math.Max(0, r.Left), y = Math.Max(0, r.Top);
                    int w = Math.Min(full.PixelWidth, r.Right) - x, h = Math.Min(full.PixelHeight, r.Bottom) - y;
                    if (w < 50 || h < 50) { rep.Note("控制窗口不在主屏里（" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + "），没有它的图"); break; }
                    var crop = new CroppedBitmap(full, new Int32Rect(x, y, w, h));
                    crop.Freeze();
                    string name = "panel-window-" + L.Code(lg) + ".png";
                    Save(crop, Path.Combine(outDir, name));
                    rep.Note("控制窗口（" + (lg == Lang.Zh ? "中文" : "English") + "）真窗口截图 " + w + "x" + h + "：" + name + "，状态文字：" + OneLine(c.StatusText));
                }
            }
            catch (Exception ex) { rep.Note("截控制窗口出错：" + ex.Message); }
            if (L.Current != original)
            {
                panel.SelfTestSwitchLanguage(original);
                await Task.Delay(700);
            }
        }

        /// <summary>每次恢复之后都要成立的三件事。</summary>
        async Task AfterRestore(Dictionary<string, POINT>? before, string when)
        {
            rep.Check(c.State == DanceState.Idle, when + "：程序回到就绪状态");
            bool gone = await WaitUntil(() => Native.FindWindow(null, StageTitle) == IntPtr.Zero, 4);
            rep.Check(gone, when + "：舞台窗口已经没了");
            if (before == null) return;
            Dictionary<string, POINT>? now = await ReadPositions();
            if (now == null) { rep.Soft(false, when + "：这次没读到图标位置，没法核对"); return; }
            rep.Check(SamePositions(before, now, out string detail), when + "：" + detail);
        }

        // ================= 强制结束的那一轮 =================

        async Task RunHold()
        {
            rep.Line("桌面图标跳舞 Windows 版 · 实跑测试（等着被强制结束）· " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            await WaitUntil(() => c.CatalogLoaded, 40);
            await Task.Delay(800);
            RECT panelRect = PanelRect();
            Dictionary<string, POINT>? before = await ReadPositions();
            var sb = new StringBuilder();
            sb.AppendLine("#panel\t" + panelRect.Left + "\t" + panelRect.Top + "\t" + panelRect.Right + "\t" + panelRect.Bottom);
            sb.AppendLine("#positions\t" + (before == null ? "none" : before.Count.ToString()));
            if (before != null) foreach (var kv in before) sb.AppendLine(kv.Key.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') + "\t" + kv.Value.X + "\t" + kv.Value.Y);
            File.WriteAllText(Path.Combine(outDir, "positions-before.tsv"), sb.ToString(), new UTF8Encoding(false));
            Shot("k0-before.png");
            c.Start();
            bool running = await WaitUntil(() => c.State == DanceState.Running, 45);
            rep.Check(running, "开始后进入跳舞状态");
            if (!running) { c.EmergencyRestore(); ExitCode = 1; return; }
            await Task.Delay(3000);
            Shot("k1-dancing.png");
            rep.Line("正在跳舞，等着被强制结束…");
            File.WriteAllText(Path.Combine(outDir, "hold-ready.txt"), "ready");
            await Task.Delay(150000);
            rep.Fail("等了 150 秒没有被结束，自己恢复桌面并退出");
            c.EmergencyRestore();
            ExitCode = 3;
        }

        /// <summary>--livetest-after：上一个进程被强制结束之后，核对桌面是不是原样。不开任何窗口。</summary>
        public static int AfterKill(string outDir)
        {
            try { Directory.CreateDirectory(outDir); } catch { }
            var rep = new Report(Path.Combine(outDir, "livetest-kill.txt"));
            try
            {
                rep.Line("桌面图标跳舞 Windows 版 · 强制结束后的复核 · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                bool gone = false;
                for (int i = 0; i < 50 && !gone; i++)
                {
                    gone = Native.FindWindow(null, StageTitle) == IntPtr.Zero;
                    if (!gone) Thread.Sleep(100);
                }
                rep.Check(gone, "进程被强制结束后：舞台窗口已经没了（没有任何东西留在桌面上）");

                var before = new Dictionary<string, POINT>();
                bool hadPositions = false;
                RECT panelRect = default;
                string file = Path.Combine(outDir, "positions-before.tsv");
                if (File.Exists(file))
                {
                    foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                    {
                        string[] f = raw.Split('\t');
                        if (f.Length >= 5 && f[0] == "#panel")
                        {
                            int.TryParse(f[1], out panelRect.Left); int.TryParse(f[2], out panelRect.Top);
                            int.TryParse(f[3], out panelRect.Right); int.TryParse(f[4], out panelRect.Bottom);
                        }
                        else if (f.Length >= 2 && f[0] == "#positions") hadPositions = f[1] != "none";
                        else if (f.Length >= 3 && !raw.StartsWith("#") && int.TryParse(f[f.Length - 2], out int x) && int.TryParse(f[f.Length - 1], out int y))
                            before[string.Join("\t", f, 0, f.Length - 2)] = new POINT { X = x, Y = y };
                    }
                }
                else rep.Fail("找不到开始前记下的图标位置（positions-before.tsv）");

                if (hadPositions)
                {
                    Dictionary<string, POINT>? now = DesktopLoader.ReadPositions();
                    if (now == null) rep.Soft(false, "强制结束后没读到图标位置，没法核对");
                    else rep.Check(SamePositions(before, now, out string detail), "进程被强制结束后：" + detail);
                }
                else rep.Note("开始前就没读到图标位置，这一项没法核对");

                Thread.Sleep(600);
                BitmapSource? after = Capture();
                if (after != null) Save(after, Path.Combine(outDir, "k2-after-kill.png"));
                string k0 = Path.Combine(outDir, "k0-before.png");
                if (after != null && File.Exists(k0))
                {
                    BitmapSource b0 = Load(k0);
                    double d = Diff(b0, after, WorkArea(), panelRect);
                    rep.Soft(d < 0.01, "强制结束后的画面和开始前一样（工作区里只有 " + (d * 100).ToString("0.00") + "% 的像素不同，控制窗口那一块不算）");
                }
                rep.Line(rep.Failed ? "结论：有失败项。" : "结论：强制结束后桌面是原样。");
            }
            catch (Exception ex) { rep.Fail("复核抛出异常：" + ex); }
            CopyLog(outDir);
            return rep.Failed ? 1 : 0;
        }

        // ================= 小工具 =================

        /// <summary>每行写完立刻落盘：就算中途被结束，已经做到哪一步也看得到。</summary>
        sealed class Report
        {
            readonly string path;
            readonly object gate = new object();
            public bool Failed { get; private set; }
            public Report(string path)
            {
                this.path = path;
                try { File.WriteAllText(path, "", new UTF8Encoding(true)); } catch { }
            }
            public void Line(string s)
            {
                lock (gate) { try { File.AppendAllText(path, s + Environment.NewLine, Encoding.UTF8); } catch { } }
                Log.Write("[实跑测试] " + s);
            }
            public void Check(bool ok, string what) { if (!ok) Failed = true; Line((ok ? "[通过] " : "[失败] ") + what); }
            public void Fail(string what) { Failed = true; Line("[失败] " + what); }
            /// <summary>只记录，不影响结果。</summary>
            public void Soft(bool ok, string what) { Line((ok ? "[记录·是] " : "[记录·否] ") + what); }
            public void Note(string what) { Line("    " + what); }
        }

        static string OneLine(string s) => s.Replace("\r", "").Replace("\n", " | ");

        static async Task<bool> WaitUntil(Func<bool> cond, double seconds)
        {
            double deadline = Clock.Now + seconds;
            while (!cond())
            {
                if (Clock.Now >= deadline) return false;
                await Task.Delay(100);
            }
            return true;
        }

        /// <summary>只读地问一遍每个桌面图标的位置（要在单独的 STA 线程上问）。最多等 30 秒。</summary>
        static async Task<Dictionary<string, POINT>?> ReadPositions()
        {
            var tcs = new TaskCompletionSource<Dictionary<string, POINT>?>();
            var th = new Thread(() =>
            {
                try { tcs.TrySetResult(DesktopLoader.ReadPositions()); }
                catch (Exception ex) { Log.Write("[实跑测试] 读图标位置出错：" + ex.Message); tcs.TrySetResult(null); }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
            Task done = await Task.WhenAny(tcs.Task, Task.Delay(30000));
            return done == tcs.Task ? tcs.Task.Result : null;
        }

        static bool SamePositions(Dictionary<string, POINT> before, Dictionary<string, POINT> now, out string detail)
        {
            int same = 0, moved = 0, missing = 0;
            string firstMoved = "";
            foreach (var kv in before)
            {
                if (!now.TryGetValue(kv.Key, out POINT p)) { missing++; continue; }
                if (p.X == kv.Value.X && p.Y == kv.Value.Y) same++;
                else
                {
                    moved++;
                    if (firstMoved.Length == 0) firstMoved = "（例如 " + kv.Key + "：" + kv.Value.X + "," + kv.Value.Y + " → " + p.X + "," + p.Y + "）";
                }
            }
            int extra = now.Count - same - moved;
            bool ok = moved == 0 && missing == 0 && extra == 0;
            detail = ok ? same + " 个桌面图标的位置和开始前一模一样，没多没少"
                        : "图标和开始前不一样：位置变了 " + moved + " 个" + firstMoved + "，少了 " + missing + " 个，多了 " + extra + " 个";
            return ok;
        }

        RECT PanelRect()
        {
            try
            {
                IntPtr h = new WindowInteropHelper(panel).Handle;
                if (h != IntPtr.Zero && Native.GetWindowRect(h, out RECT r)) return r;
            }
            catch { }
            return default;
        }

        /// <summary>控制窗口看得见的那一圈（不含系统加在窗口外面、看不见的那几像素边）。</summary>
        RECT VisiblePanelRect()
        {
            try
            {
                IntPtr h = new WindowInteropHelper(panel).Handle;
                if (h != IntPtr.Zero && TestNative.DwmGetWindowAttribute(h, 9, out RECT r, Marshal.SizeOf<RECT>()) == 0 && r.Right > r.Left) return r;
            }
            catch { }
            return PanelRect();
        }

        static RECT WorkArea()
        {
            try
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                IntPtr mon = Native.MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1);
                if (mon != IntPtr.Zero && Native.GetMonitorInfo(mon, ref mi)) return mi.rcWork;
            }
            catch { }
            return new RECT { Left = 0, Top = 0, Right = TestNative.GetSystemMetrics(0), Bottom = TestNative.GetSystemMetrics(1) };
        }

        // ---------- 「这个点最上面是谁的窗口」----------

        sealed class Probe
        {
            public POINT Pt;
            public IntPtr Root;
            public string Class = "";
            public bool IsDesktop => Class == "Progman" || Class == "WorkerW";
        }

        /// <summary>在工作区里均匀取一些点（避开控制窗口），记下每个点最上面那个窗口是谁。只是问，不碰任何窗口。</summary>
        static List<Probe> ProbePoints(RECT work, RECT panelRect)
        {
            var list = new List<Probe>();
            int w = work.Right - work.Left, h = work.Bottom - work.Top;
            for (int row = 0; row < 5; row++)
            {
                for (int col = 0; col < 7; col++)
                {
                    var pt = new POINT { X = work.Left + (int)(w * (0.08 + 0.84 * col / 6.0)), Y = work.Top + (int)(h * (0.08 + 0.84 * row / 4.0)) };
                    if (pt.X >= panelRect.Left - 12 && pt.X <= panelRect.Right + 12 && pt.Y >= panelRect.Top - 12 && pt.Y <= panelRect.Bottom + 12) continue;
                    IntPtr hit = TestNative.WindowFromPoint(pt);
                    IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : TestNative.GetAncestor(hit, 2);
                    var sb = new StringBuilder(128);
                    if (root != IntPtr.Zero) TestNative.GetClassName(root, sb, sb.Capacity);
                    list.Add(new Probe { Pt = pt, Root = root, Class = sb.ToString() });
                }
            }
            return list;
        }

        static string Summary(List<Probe> probes)
        {
            var count = new Dictionary<string, int>();
            foreach (Probe p in probes) { string k = p.Class.Length == 0 ? "（无）" : p.Class; count[k] = count.TryGetValue(k, out int n) ? n + 1 : 1; }
            var parts = new List<string>();
            foreach (var kv in count) parts.Add(kv.Key + " × " + kv.Value);
            return string.Join("，", parts);
        }

        /// <summary>
        /// 舞台应该「只比桌面高一层」：原来露着桌面的点，现在最上面应该是舞台；
        /// 原来被别的窗口盖着的点，现在应该还是那个窗口（舞台不能跑到别人上面）。
        /// </summary>
        void LayerCheck(List<Probe> before, IntPtr stage, string when)
        {
            int deskTotal = 0, deskNowStage = 0, otherTotal = 0, otherSame = 0;
            var odd = new List<string>();
            foreach (Probe b in before)
            {
                IntPtr hit = TestNative.WindowFromPoint(b.Pt);
                IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : TestNative.GetAncestor(hit, 2);
                if (b.IsDesktop)
                {
                    deskTotal++;
                    if (root == stage && stage != IntPtr.Zero) deskNowStage++;
                    else if (odd.Count < 3) odd.Add("(" + b.Pt.X + "," + b.Pt.Y + ") 最上面是 " + ClassOf(root));
                }
                else
                {
                    otherTotal++;
                    if (root == b.Root) otherSame++;
                    else if (odd.Count < 3) odd.Add("(" + b.Pt.X + "," + b.Pt.Y + ") 原来是 " + b.Class + "，现在是 " + (root == stage ? "舞台" : ClassOf(root)));
                }
            }
            if (deskTotal > 0)
                rep.Soft(deskNowStage == deskTotal, when + "：原来露着桌面的 " + deskTotal + " 个点里，现在有 " + deskNowStage + " 个最上面是舞台（应该全部都是）");
            else rep.Note(when + "：没有一个探测点露着桌面（屏幕被别的窗口占满了），没法核对「舞台盖住了桌面」");
            if (otherTotal > 0)
                rep.Soft(otherSame == otherTotal, when + "：原来被别的窗口盖着的 " + otherTotal + " 个点里，有 " + otherSame + " 个还是原来那个窗口（应该全部都是：舞台不能盖到别的窗口上面）");
            foreach (string s in odd) rep.Note("对不上的点：" + s);
        }

        static string ClassOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "（无）";
            var sb = new StringBuilder(128);
            TestNative.GetClassName(h, sb, sb.Capacity);
            return sb.Length == 0 ? "（无名）" : sb.ToString();
        }

        // ---------- 截屏和比对（只有测试模式用）----------

        BitmapSource? Shot(string name)
        {
            try
            {
                BitmapSource? b = Capture();
                if (b == null) { rep.Note("截屏失败：" + name); return null; }
                Save(b, Path.Combine(outDir, name));
                return b;
            }
            catch (Exception ex) { rep.Note("截屏 " + name + " 出错：" + ex.Message); return null; }
        }

        /// <summary>主屏的画面，转成每像素 4 字节的格式。</summary>
        static BitmapSource? Capture()
        {
            int w = TestNative.GetSystemMetrics(0), h = TestNative.GetSystemMetrics(1);
            if (w <= 0 || h <= 0) return null;
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;
            IntPtr memDc = IntPtr.Zero, bmp = IntPtr.Zero;
            try
            {
                memDc = TestNative.CreateCompatibleDC(screenDc);
                bmp = TestNative.CreateCompatibleBitmap(screenDc, w, h);
                if (memDc == IntPtr.Zero || bmp == IntPtr.Zero) return null;
                IntPtr old = TestNative.SelectObject(memDc, bmp);
                bool ok = TestNative.BitBlt(memDc, 0, 0, w, h, screenDc, 0, 0, 0x00CC0020 | 0x40000000);
                TestNative.SelectObject(memDc, old);
                if (!ok) return null;
                BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var conv = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
                conv.Freeze();
                return conv;
            }
            finally
            {
                if (bmp != IntPtr.Zero) Native.DeleteObject(bmp);
                if (memDc != IntPtr.Zero) TestNative.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        static void Save(BitmapSource b, string path)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(b));
            using var fs = File.Create(path);
            enc.Save(fs);
        }

        static BitmapSource Load(string path)
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(Path.GetFullPath(path));
            img.EndInit();
            var conv = new FormatConvertedBitmap(img, PixelFormats.Bgr32, null, 0);
            conv.Freeze();
            return conv;
        }

        /// <summary>两张截屏在工作区里（不算控制窗口那一块）有多大比例的像素明显不同。尺寸不一样时返回 1。</summary>
        static double Diff(BitmapSource a, BitmapSource b, RECT area, RECT exclude)
        {
            if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return 1;
            int w = a.PixelWidth, h = a.PixelHeight, stride = w * 4;
            var pa = new byte[stride * h];
            var pb = new byte[stride * h];
            new FormatConvertedBitmap(a, PixelFormats.Bgr32, null, 0).CopyPixels(pa, stride, 0);
            new FormatConvertedBitmap(b, PixelFormats.Bgr32, null, 0).CopyPixels(pb, stride, 0);
            int x0 = Math.Max(0, area.Left), y0 = Math.Max(0, area.Top), x1 = Math.Min(w, area.Right), y1 = Math.Min(h, area.Bottom);
            long total = 0, diff = 0;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    if (x >= exclude.Left - 8 && x < exclude.Right + 8 && y >= exclude.Top - 8 && y < exclude.Bottom + 8) continue;
                    int i = y * stride + x * 4;
                    int d = Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]);
                    total++;
                    if (d > 48) diff++;
                }
            }
            return total == 0 ? 0 : (double)diff / total;
        }

        static void CopyLog(string outDir)
        {
            try { File.Copy(AppPaths.LogFile, Path.Combine(outDir, "desktop-dance.log"), true); } catch { }
        }

        /// <summary>
        /// 只有测试模式用的 Windows API：截屏、问「这个点是谁的窗口」、模拟按一下 Esc、模拟点一下鼠标。
        /// 故意不放进 Native：平时运行的程序用不到它们。
        /// </summary>
        static class TestNative
        {
            [DllImport("user32.dll")]
            public static extern int GetSystemMetrics(int nIndex);

            [DllImport("user32.dll")]
            public static extern IntPtr WindowFromPoint(POINT pt);

            [DllImport("user32.dll")]
            public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

            [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
            public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

            [DllImport("user32.dll")]
            public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

            [DllImport("user32.dll")]
            public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

            [DllImport("user32.dll")]
            public static extern bool SetCursorPos(int x, int y);

            [DllImport("user32.dll")]
            public static extern bool GetCursorPos(out POINT lpPoint);

            /// <summary>只读：问窗口看得见的边框在哪（属性 9 = DWMWA_EXTENDED_FRAME_BOUNDS）。</summary>
            [DllImport("dwmapi.dll")]
            public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

            [DllImport("gdi32.dll")]
            public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

            [DllImport("gdi32.dll")]
            public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

            [DllImport("gdi32.dll")]
            public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

            [DllImport("gdi32.dll")]
            public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

            [DllImport("gdi32.dll")]
            public static extern bool DeleteDC(IntPtr hdc);
        }
    }
}
