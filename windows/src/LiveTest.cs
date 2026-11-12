using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopDance
{
    /// <summary>
    /// 实跑测试（GitHub 上自动构建时会跑；自己也可以跑）：和平时一样把程序开起来，然后由脚本代替人去点。
    ///
    ///   DesktopDance.exe --livetest 输出文件夹
    ///       像人一样逐行操作控制窗口上的每个控件（开始、模式、自动切换、篮球、跟音乐、速度、幅度、数量、语言、
    ///       暂停 / 继续、图标名称、录屏隐藏），每一行都核对两件事：后台（场上实际的情况）真的跟着变了，窗口上显示的也对。
    ///       然后用三种方式各恢复一次（按 Esc、「恢复桌面」、点桌面），每次恢复后核对：舞台窗口没了、
    ///       每个桌面图标的位置和开始前一样。结果写进 livetest.txt，并留下截屏。
    ///   DesktopDance.exe --livetest-hold 输出文件夹
    ///       开始跳，然后一直跳着不动，等外面把这个进程强制结束（模拟任务管理器里的「结束任务」）。
    ///   DesktopDance.exe --livetest-after 输出文件夹
    ///       上面那个进程被强制结束之后运行：核对舞台窗口没了、图标位置没变。
    ///
    /// 「算失败」的：每一行「控件 → 后台 → 显示」的衔接，以及安全相关的几条（能开始、能恢复、恢复后舞台窗口没了、图标位置没变）。
    /// 只做记录的：舞台是不是正好垫在桌面图标上面、模拟的按键和鼠标有没有生效、恢复前后的画面比对——它们取决于跑测试的那台机器。
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
        PanelWindow.TestHandles ui = null!;                 // 窗口上真正的控件（RunFull 一开始取）
        RECT workArea;
        int realClicks, fallbackClicks;
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
                Thread.Sleep(hold ? 200000 : 270000);
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

            // ---------- 第一轮：像人一样逐行操作控制窗口，每一行都核对「后台真的照办了」和「窗口上显示对了」 ----------
            rep.Line("");
            rep.Line("== 第一轮：逐行操作控制窗口（按钮和勾选项用鼠标真的点；下拉和滑块直接选 / 拖到某个值），最后按 Esc ==");
            ui = panel.SelfTestHandles();
            workArea = work;
            rep.Check(ui.Start.IsEnabled && !ui.Pause.IsEnabled && !ui.Restore.IsEnabled, "就绪时：「开始」能点，「暂停」「恢复桌面」是灰的");
            rep.Check(ui.Status.Text.Contains(L.T("就绪", "Ready")), "就绪时：状态文字写着「就绪」");
            rep.Check(ui.CountLabel.Text == c.TargetCount.ToString() && (int)Math.Round(ui.Count.Value) == c.TargetCount,
                      "就绪时：「数量」滑块、旁边的数字和后台的数量一致（" + c.TargetCount + "）");

            bool started = await Press(ui.Start, null, () => c.State != DanceState.Idle);
            bool running = started && await WaitUntil(() => c.State == DanceState.Running, 45);
            rep.Check(running, "点「开始」后进入跳舞状态");
            if (!running) { rep.Note("状态文字：" + OneLine(c.StatusText)); c.EmergencyRestore(); ExitCode = 1; return; }
            await Task.Delay(3500);
            IntPtr stage = Native.FindWindow(null, StageTitle);
            rep.Check(stage != IntPtr.Zero && Native.IsWindowVisible(stage), "舞台窗口已经打开");
            rep.Note("舞台窗口层级（程序自己的检查）：" + c.StageLayerNote);
            rep.Note("帧率 " + c.Fps + " fps，Esc 热键" + (c.HotKeys.EscapeRegistered ? "已注册" : "没注册上") + "，" + c.AudioDebug);
            rep.Note("状态文字：" + OneLine(c.StatusText));
            rep.Check(!ui.Start.IsEnabled && ui.Pause.IsEnabled && ui.Restore.IsEnabled, "跳舞时：「开始」变灰，「暂停」「恢复桌面」能点");
            rep.Check(ui.Status.Text.Contains(L.T("跳舞中", "Dancing")), "跳舞时：状态文字写着「跳舞中」");
            rep.Check(c.DancerCount == c.TargetCount, "跳舞时：场上跳舞的图标数（" + c.DancerCount + "）等于「数量」（" + c.TargetCount + "）；桌面图标 " + c.DesktopCount + " 个，借来 " + c.GuestCount + " 个");
            LayerCheck(probesBefore, stage, "打篮球");
            BitmapSource? shotDance = Shot("2-basketball.png");
            if (shotBefore != null && shotDance != null)
            {
                double d = Diff(shotBefore, shotDance, work, panelRect);
                rep.Soft(d > 0.002, "跳舞时的画面和开始前不一样（工作区里 " + (d * 100).ToString("0.00") + "% 的像素变了；一点没变说明舞台没露出来）");
            }

            await RowMode();
            await RowAutoSwitch();
            await RowBall();
            await RowFollowMusicSpeedAmp();
            await RowCount();
            await RowLanguage();
            await RowPauseNamesHide();
            rep.Soft(fallbackClicks == 0, "鼠标点击：" + realClicks + " 次真的点中并生效" + (fallbackClicks > 0 ? "，另有 " + fallbackClicks + " 次没点中、改成直接触发那个控件的点击事件" : ""));

            // 按 Esc（模拟真的按键，走全局热键那条路）
            TestNative.keybd_event(0x1B, 0, 0, UIntPtr.Zero);
            TestNative.keybd_event(0x1B, 0, 2, UIntPtr.Zero);
            bool escWorked = await WaitUntil(() => c.State == DanceState.Idle, 4);
            rep.Soft(escWorked, "模拟按下 Esc 后停止并恢复（走的是全局热键；测试机不接受模拟按键时这一项会不成立）");
            if (!escWorked) c.EmergencyRestore();
            await AfterRestore(before, "按 Esc 后");
            await Task.Delay(700);
            BitmapSource? shotAfter1 = Shot("11-after-esc.png");
            if (shotBefore != null && shotAfter1 != null)
            {
                double d = Diff(shotBefore, shotAfter1, work, panelRect, PanelRect());
                rep.Soft(d < 0.01, "恢复后的画面和开始前一样（工作区里只有 " + (d * 100).ToString("0.00") + "% 的像素不同，控制窗口那一块不算）");
            }

            // ---------- 第二轮：「恢复桌面」按钮（图标飞回原位再撤舞台）----------
            rep.Line("");
            rep.Line("== 第二轮：用「恢复桌面」停止 ==");
            started = await Press(ui.Start, null, () => c.State != DanceState.Idle);
            running = started && await WaitUntil(() => c.State == DanceState.Running, 45);
            rep.Check(running, "第二次点「开始」后进入跳舞状态");
            await Task.Delay(2500);
            await Press(ui.Restore, null, () => c.State == DanceState.Stopping || c.State == DanceState.Idle);
            bool idle = await WaitUntil(() => c.State == DanceState.Idle, 8);
            rep.Check(idle, "点「恢复桌面」后图标飞回原位，回到就绪状态");
            rep.Check(ui.Start.IsEnabled && !ui.Pause.IsEnabled && !ui.Restore.IsEnabled && Txt(ui.Start.Content) == L.T("开始蹦迪", "Start"),
                      "恢复后：按钮回到就绪时的样子");
            if (!idle) c.EmergencyRestore();
            await AfterRestore(before, "「恢复桌面」后");

            // ---------- 第三轮：在桌面上点一下 ----------
            rep.Line("");
            rep.Line("== 第三轮：在桌面上点一下停止 ==");
            started = await Press(ui.Start, null, () => c.State != DanceState.Idle);
            running = started && await WaitUntil(() => c.State == DanceState.Running, 45);
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
            BitmapSource? shotEnd = Shot("12-after.png");
            if (shotBefore != null && shotEnd != null)
            {
                double d = Diff(shotBefore, shotEnd, work, panelRect, PanelRect());
                rep.Soft(d < 0.01, "全部结束后的画面和开始前一样（工作区里只有 " + (d * 100).ToString("0.00") + "% 的像素不同）");
            }

            rep.Line("");
            rep.Line(rep.Failed ? "结论：有失败项。" : "结论：安全相关的项全部通过（能开始、能恢复、恢复后舞台窗口没了、图标位置没变）。带「记录」的项见上，不算通过或失败。");
            ExitCode = rep.Failed ? 1 : 0;
        }

        // ================= 逐行操作控制窗口 =================
        // 每一行的套路都一样：在真窗口上动那个控件 → 核对后台（场上实际的情况）真的变了 → 核对窗口上显示的也对。
        // 「算失败」的是这两条衔接；鼠标点没点中只做记录（点不中时改成直接触发控件的点击事件，衔接照样查）。

        static string Txt(object? content) => content as string ?? (content as TextBlock)?.Text ?? "";

        /// <summary>把鼠标挪到控件上，真的按下、松开一次。</summary>
        async Task MouseClick(FrameworkElement el)
        {
            Point local = el is ToggleButton ? new Point(8, el.ActualHeight / 2) : new Point(el.ActualWidth / 2, el.ActualHeight / 2);
            Point scr = el.PointToScreen(local);
            TestNative.SetCursorPos((int)Math.Round(scr.X), (int)Math.Round(scr.Y));
            await Task.Delay(150);
            TestNative.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(40);
            TestNative.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(120);
        }

        /// <summary>
        /// 像人一样用鼠标点一下按钮或勾选项。took = 「程序确实照办了」。
        /// 点了两次都没反应（测试机不接受模拟鼠标）时，改成直接触发这个控件的点击事件——
        /// 走的仍然是控件上接的那段处理代码，所以「控件 → 后台」的衔接照样查得到。
        /// </summary>
        async Task<bool> Press(ButtonBase b, bool? wantChecked, Func<bool> took, double wait = 2)
        {
            for (int attempt = 0; attempt < 2 && b.IsEnabled; attempt++)
            {
                await MouseClick(b);
                if (await WaitUntil(took, wait)) { realClicks++; return true; }
            }
            fallbackClicks++;
            if (b is ToggleButton t && wantChecked != null) t.IsChecked = wantChecked;
            b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return await WaitUntil(took, wait);
        }

        async Task RowMode()
        {
            rep.Line("-- 模式 --");
            int shotNo = 3;
            for (int i = 0; i < DanceModes.All.Length; i++)
            {
                DanceMode m = DanceModes.All[i];
                if (m == DanceMode.Figure) continue;
                ui.Mode.SelectedIndex = i;
                await Task.Delay(2600);
                rep.Check(c.Mode == m && c.State == DanceState.Running, "「模式」选「" + DanceModes.LogName(m) + "」：后台换成了这个模式，还在跳（" + c.Fps + " fps）");
                Shot(shotNo + "-" + m.ToString().ToLowerInvariant() + ".png");
                shotNo++;
            }
            ui.Mode.SelectedIndex = Array.IndexOf(DanceModes.All, DanceMode.Figure);
            await Task.Delay(1200);
            rep.Check(c.Mode == DanceMode.Figure, "「模式」选回「打篮球」：后台换回来了");
        }

        async Task RowAutoSwitch()
        {
            rep.Line("-- 自动切换 --");
            ui.Mode.SelectedIndex = Array.IndexOf(DanceModes.All, DanceMode.Groove);
            await Task.Delay(500);
            bool on = await Press(ui.Auto, true, () => c.AutoSwitch);
            rep.Check(on, "勾上「自动切换」：后台开了自动切换");
            double t0 = Clock.Now;
            bool switched = await WaitUntil(() => c.Mode != DanceMode.Groove, 20);
            rep.Check(switched, "勾上之后不用管它，" + (Clock.Now - t0).ToString("0") + " 秒后自己从「律动」换到了「" + DanceModes.LogName(c.Mode) + "」");
            await Task.Delay(400);
            rep.Check(ui.Mode.SelectedIndex == Array.IndexOf(DanceModes.All, c.Mode), "自动换了模式后：「模式」下拉显示的也跟着变了");
            bool off = await Press(ui.Auto, false, () => !c.AutoSwitch);
            rep.Check(off, "去掉「自动切换」的勾：后台关了");
            ui.Mode.SelectedIndex = Array.IndexOf(DanceModes.All, DanceMode.Figure);
            await Task.Delay(1200);
        }

        async Task RowBall()
        {
            rep.Line("-- 篮球 --");
            if (ui.Ball.Items.Count < 3) { rep.Note("「篮球」下拉里可选的图标不到两个，这一行没法试"); return; }
            string was = c.BallInUse;
            ui.Ball.SelectedIndex = 2;
            string want = ui.Ball.SelectedItem as string ?? "";
            bool ok = await WaitUntil(() => c.BallName == want && DesktopLoader.Matches(c.BallInUse, want), 8);
            rep.Check(ok, "「篮球」选「" + want + "」：场上当球的图标换成了「" + c.BallInUse + "」（原来是「" + was + "」）");
            ui.Ball.SelectedIndex = 0;
            ok = await WaitUntil(() => c.BallName.Length == 0, 3);
            await Task.Delay(600);
            rep.Check(ok, "「篮球」选回「自动」：后台改回自动，现在当球的是「" + c.BallInUse + "」");
        }

        /// <summary>这段时间里节拍每秒走几拍。</summary>
        async Task<double> BeatRate(int ms)
        {
            double b0 = c.BeatNow, t0 = Clock.Now;
            await Task.Delay(ms);
            return (c.BeatNow - b0) / Math.Max(0.001, Clock.Now - t0);
        }

        /// <summary>这段时间里，每个跳舞的图标在屏幕上来回走过的范围（横向 + 纵向，单位是点），取平均。</summary>
        async Task<double> Motion(double seconds)
        {
            int n = c.DancerCount;
            if (n <= 0) return 0;
            var lo = new Vec[n];
            var hi = new Vec[n];
            for (int i = 0; i < n; i++) { lo[i] = new Vec(1e9, 1e9); hi[i] = new Vec(-1e9, -1e9); }
            double end = Clock.Now + seconds;
            while (Clock.Now < end)
            {
                Pose[] poses = c.CurrentPoses;
                for (int i = 0; i < n && i < poses.Length; i++)
                {
                    Vec q = poses[i].P;
                    lo[i] = new Vec(Math.Min(lo[i].X, q.X), Math.Min(lo[i].Y, q.Y));
                    hi[i] = new Vec(Math.Max(hi[i].X, q.X), Math.Max(hi[i].Y, q.Y));
                }
                await Task.Delay(30);
            }
            double sum = 0;
            int counted = 0;
            for (int i = 0; i < n; i++)
            {
                if (hi[i].X < lo[i].X) continue;
                sum += (hi[i].X - lo[i].X) + (hi[i].Y - lo[i].Y);
                counted++;
            }
            return counted == 0 ? 0 : sum / counted;
        }

        async Task RowFollowMusicSpeedAmp()
        {
            rep.Line("-- 跟着电脑正在播放的音乐跳 --");
            bool was = c.UseSystemAudio;
            bool ok = await Press(ui.FollowMusic, !was, () => c.UseSystemAudio == !was);
            rep.Check(ok, (was ? "去掉" : "勾上") + "「跟着音乐跳」：后台" + (was ? "不再听系统声音" : "开始听系统声音"));
            // 速度、幅度在「不跟音乐」的时候量，这样节拍只由滑块决定，量出来的数才说明问题
            if (c.UseSystemAudio) { await Press(ui.FollowMusic, false, () => !c.UseSystemAudio); }
            await Task.Delay(900);
            rep.Check(ui.Status.Text.Contains(L.T("内置节拍", "Built-in beat")), "不跟音乐时：状态文字写着「内置节拍」");

            rep.Line("-- 速度 --");
            ui.Speed.Value = 1;
            await Task.Delay(400);
            double r1 = await BeatRate(2500);
            ui.Speed.Value = 2;
            await Task.Delay(400);
            double r2 = await BeatRate(2500);
            rep.Check(Math.Abs(c.Speed - 2) < 1e-6 && r1 > 0.5 && r2 / r1 > 1.6 && r2 / r1 < 2.4,
                      "「速度」拖到最右（2 倍）：节拍真的快了一倍（每秒 " + r1.ToString("0.00") + " 拍 → " + r2.ToString("0.00") + " 拍）");
            ui.Speed.Value = 0.5;
            await Task.Delay(400);
            double r3 = await BeatRate(2500);
            rep.Check(Math.Abs(c.Speed - 0.5) < 1e-6 && r1 > 0.5 && r3 / r1 > 0.4 && r3 / r1 < 0.62,
                      "「速度」拖到最左（0.5 倍）：节拍真的慢了一半（每秒 " + r3.ToString("0.00") + " 拍）");
            ui.Speed.Value = 1;

            rep.Line("-- 幅度 --");
            ui.Mode.SelectedIndex = Array.IndexOf(DanceModes.All, DanceMode.Groove);
            await Task.Delay(1800);
            ui.Amp.Value = 0.3;
            await Task.Delay(500);
            double m1 = await Motion(2.5);
            ui.Amp.Value = 2;
            await Task.Delay(500);
            double m2 = await Motion(2.5);
            rep.Check(Math.Abs(c.Amp - 2) < 1e-6 && m2 > m1 * 1.5,
                      "「幅度」从最左拖到最右（律动模式下量）：图标的动作真的变大了（平均摆动范围 " + m1.ToString("0.0") + " 点 → " + m2.ToString("0.0") + " 点）");
            Shot("6-groove-amp-max.png");
            ui.Amp.Value = 1;
            ui.Mode.SelectedIndex = Array.IndexOf(DanceModes.All, DanceMode.Figure);
            await Task.Delay(1200);

            if (was)
            {
                ok = await Press(ui.FollowMusic, true, () => c.UseSystemAudio);
                rep.Check(ok, "重新勾上「跟着音乐跳」：后台又开始听系统声音");
            }
        }

        async Task RowCount()
        {
            rep.Line("-- 数量（上次真机测出来的问题就在这一行：拖了滑块，场上的个数没跟着变）--");
            foreach (int n in new[] { 12, 96, 48 })
            {
                ui.Count.Value = n;
                bool ok = await WaitUntil(() => c.TargetCount == n && c.DancerCount == n && c.State == DanceState.Running, 12);
                await Task.Delay(1500);
                int stay = Math.Max(0, c.DesktopCount - n);
                rep.Check(ok && c.Bystanders == stay,
                          "「数量」拖到 " + n + "：场上真的是 " + c.DancerCount + " 个在跳，" + c.Bystanders + " 个桌面图标留在原位不动（桌面图标共 " + c.DesktopCount + " 个，借来的在跳 " + Math.Max(0, c.DancerCount - Math.Min(c.DesktopCount, c.DancerCount)) + " 个）");
                rep.Check(ui.CountLabel.Text == n.ToString(), "「数量」拖到 " + n + "：滑块旁边的数字显示 " + ui.CountLabel.Text);
                rep.Note("状态文字：" + OneLine(ui.Status.Text));
                Shot("7-count-" + n + ".png");
            }
        }

        async Task RowLanguage()
        {
            rep.Line("-- 语言 --");
            Lang original = L.Current, other = original == Lang.Zh ? Lang.En : Lang.Zh;
            ui.Language.SelectedIndex = other == Lang.Zh ? 0 : 1;
            await Task.Delay(1500);
            rep.Check(L.Current == other && c.State == DanceState.Running && c.Fps > 0, "「语言」选" + (other == Lang.Zh ? "中文" : " English") + "：换过去了，还在跳（" + c.Fps + " fps）");
            rep.Check(Txt(ui.Pause.Content) == (other == Lang.Zh ? "暂停" : "Pause") && Txt(ui.Restore.Content) == (other == Lang.Zh ? "恢复桌面" : "Restore")
                      && ui.Status.Text.Contains(other == Lang.Zh ? "跳舞中" : "Dancing") && (ui.Mode.SelectedItem as string ?? "") == (other == Lang.Zh ? "打篮球" : "Basketball"),
                      "换语言后：按钮、模式名、状态文字都换成了" + (other == Lang.Zh ? "中文" : "英文"));
            rep.Note("换语言后的状态文字：" + OneLine(ui.Status.Text));
            Shot("8-language-switched.png");
            ui.Language.SelectedIndex = original == Lang.Zh ? 0 : 1;
            await Task.Delay(800);
            rep.Check(L.Current == original && Txt(ui.Pause.Content) == (original == Lang.Zh ? "暂停" : "Pause"), "「语言」选回原来的：字换回来了");
        }

        async Task RowPauseNamesHide()
        {
            rep.Line("-- 暂停 / 继续 --");
            BitmapSource? r1 = Capture();
            await Task.Delay(700);
            BitmapSource? r2 = Capture();
            double moving = r1 != null && r2 != null ? Diff(r1, r2, workArea, PanelRect()) : -1;

            bool ok = await Press(ui.Pause, null, () => c.State == DanceState.Paused);
            rep.Check(ok, "点「暂停」：后台进入暂停");
            await Task.Delay(500);
            rep.Check(Txt(ui.Start.Content) == L.T("继续", "Resume") && ui.Start.IsEnabled && !ui.Pause.IsEnabled, "暂停时：「开始」按钮变成了「继续」并且能点，「暂停」变灰");
            rep.Check(ui.Status.Text.Contains(L.T("已暂停", "Paused")), "暂停时：状态文字写着「已暂停」");
            // 连截三张：点下暂停 0.5 秒时一张（p0），之后每隔 0.8 秒各一张（p1、p2）。
            // 「定住了」看的是 p1 和 p2；p0 和 p1 的差别单独记下来，并指出差在屏幕上哪一块，方便分辨是什么在变。
            BitmapSource? p0 = Capture();
            await Task.Delay(800);
            BitmapSource? p1 = Capture();
            await Task.Delay(800);
            BitmapSource? p2 = Capture();
            if (p0 != null && p1 != null && p2 != null)
            {
                RECT pnl = PanelRect();
                double still = DiffBox(p1, p2, workArea, out RECT box2, pnl);
                rep.Check(still < 0.0005, "暂停时画面真的定住了（隔 0.8 秒的两张截屏只有 " + (still * 100).ToString("0.000") + "% 的像素不同；跳舞时隔 0.7 秒是 " + (moving * 100).ToString("0.00") + "%）"
                          + (still > 0 ? "，不同的地方在屏幕 (" + box2.Left + "," + box2.Top + ")-(" + box2.Right + "," + box2.Bottom + ")" : ""));
                // p0 和 p1 之间通常会有不到千分之二的像素不同。逐像素查过：变的是图标名字的笔画——
                // 文字停下来之后，系统（WPF）会把它重新描一遍、描得更清楚——图标本身没有再动。所以这里只说明，不当成问题；
                // 要是差得多（超过千分之五），那就不是描字能解释的了，算失败。
                double settle = DiffBox(p0, p1, workArea, out RECT box1, pnl);
                rep.Check(settle < 0.005, "点下暂停半秒后图标就不再动了（和 0.8 秒后的截屏比，只有 " + (settle * 100).ToString("0.000") + "% 的像素不同）");
                if (settle > 0)
                    rep.Note("这点差别是图标名字的笔画：文字停下来后系统会把它重新描清楚，图标本身没动。差别落在屏幕 (" + box1.Left + "," + box1.Top + ")-(" + box1.Right + "," + box1.Bottom + ")");
                if (settle >= 0.005 || still >= 0.0005)
                {
                    Save(p0, Path.Combine(outDir, "9a-paused-0.5s.png"));
                    Save(p1, Path.Combine(outDir, "9b-paused-1.3s.png"));
                    Save(p2, Path.Combine(outDir, "9c-paused-2.1s.png"));
                }
            }
            else rep.Note("截不到屏，没法核对「暂停时画面定住」");

            rep.Line("-- 图标名称（趁暂停画面不动时比对）--");
            bool namesWere = c.ShowLabels;
            ok = await Press(ui.Names, !namesWere, () => c.ShowLabels == !namesWere);
            rep.Check(ok, (namesWere ? "去掉" : "勾上") + "「图标名称」：后台照办了");
            await Task.Delay(500);
            BitmapSource? p3 = Capture();
            if (p2 != null && p3 != null)
            {
                double changed = Diff(p2, p3, workArea, PanelRect());
                rep.Check(changed > 0.0003, (namesWere ? "去掉" : "勾上") + "「图标名称」后：画面上的名字真的" + (namesWere ? "没了" : "出来了") + "（" + (changed * 100).ToString("0.00") + "% 的像素变了）");
            }
            Shot("9-paused-names-toggled.png");
            ok = await Press(ui.Names, namesWere, () => c.ShowLabels == namesWere);
            await Task.Delay(500);
            BitmapSource? p4 = Capture();
            if (p2 != null && p4 != null)
            {
                double back = Diff(p2, p4, workArea, PanelRect());
                rep.Check(ok && back < 0.0005, "把「图标名称」改回去：画面和刚才一样（只有 " + (back * 100).ToString("0.000") + "% 的像素不同）");
            }

            rep.Line("-- 截屏 / 录屏时隐藏本窗口 --");
            RECT pr = VisiblePanelRect();
            BitmapSource? a = Capture();
            ok = await Press(ui.Hide, true, () => ui.Settings.HidePanelFromCapture);
            rep.Check(ok, "勾上「截屏 / 录屏时隐藏本窗口」：后台照办了");
            await Task.Delay(700);
            BitmapSource? b = Capture();
            if (a != null && b != null)
            {
                double gone = Diff(a, b, pr);
                rep.Soft(gone > 0.5, "勾上后截屏：控制窗口不在截屏里了（它那一块有 " + (gone * 100).ToString("0") + "% 的像素变了；系统太旧不支持时这一项不成立）");
                Save(b, Path.Combine(outDir, "10-panel-hidden-from-capture.png"));
            }
            ok = await Press(ui.Hide, false, () => !ui.Settings.HidePanelFromCapture);
            await Task.Delay(700);
            BitmapSource? a2 = Capture();
            if (a != null && a2 != null)
            {
                double back = Diff(a, a2, pr);
                rep.Check(ok && back < 0.1, "去掉这个勾：控制窗口又能截到了");
            }

            ok = await Press(ui.Start, null, () => c.State == DanceState.Running);
            rep.Check(ok, "点「继续」：接着跳");
            await Task.Delay(800);
            rep.Check(Txt(ui.Start.Content) == L.T("开始蹦迪", "Start") && !ui.Start.IsEnabled && ui.Pause.IsEnabled, "继续后：按钮回到跳舞时的样子");
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
            List<Probe> probes = ProbePoints(WorkArea(), panelRect);
            rep.Note("取了 " + probes.Count + " 个探测点：" + Summary(probes));
            c.Start();
            bool running = await WaitUntil(() => c.State == DanceState.Running, 45);
            rep.Check(running, "开始后进入跳舞状态");
            if (!running) { c.EmergencyRestore(); ExitCode = 1; return; }
            await Task.Delay(3000);
            rep.Note("舞台窗口层级（程序自己的检查）：" + c.StageLayerNote);
            LayerCheck(probes, Native.FindWindow(null, StageTitle), "强制结束前");
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

        /// <summary>
        /// 两张截屏在工作区里有多大比例的像素明显不同。exclude 是不算在内的几块（控制窗口开始时和现在所在的位置——
        /// 它的状态文字行数会变，窗口可能变高或被挪过）。尺寸不一样时返回 1。
        /// </summary>
        static double Diff(BitmapSource a, BitmapSource b, RECT area, params RECT[] exclude) => DiffBox(a, b, area, out _, exclude);

        /// <summary>同 Diff，另外给出不同的像素落在屏幕上哪一块（box；没有不同时是空的）。</summary>
        static double DiffBox(BitmapSource a, BitmapSource b, RECT area, out RECT box, params RECT[] exclude)
        {
            box = default;
            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
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
                    bool skip = false;
                    foreach (RECT ex in exclude)
                        if (x >= ex.Left - 8 && x < ex.Right + 8 && y >= ex.Top - 8 && y < ex.Bottom + 8) { skip = true; break; }
                    if (skip) continue;
                    int i = y * stride + x * 4;
                    int d = Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]);
                    total++;
                    if (d > 48)
                    {
                        diff++;
                        if (x < bx0) bx0 = x;
                        if (y < by0) by0 = y;
                        if (x > bx1) bx1 = x;
                        if (y > by1) by1 = y;
                    }
                }
            }
            if (bx1 >= 0) box = new RECT { Left = bx0, Top = by0, Right = bx1 + 1, Bottom = by1 + 1 };
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
