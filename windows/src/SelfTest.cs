using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopDance
{
    /// <summary>
    /// 自检：DesktopDance.exe --selftest &lt;输出文件夹&gt;
    /// 第一部分是纯计算（动作数据、四种队形、节拍检测），有一项不对就返回 1。
    /// 第二部分是探测这台机器（读桌面、取图标、铺壁纸、离屏画一张图），只记录，不影响结果。
    /// 不开窗口、不注册热键、不接声卡——那些只能在真人面前的 Windows 上看。
    /// </summary>
    public static class SelfTest
    {
        static readonly StringBuilder report = new StringBuilder();
        static bool failed;

        static void Line(string s) { report.AppendLine(s); }
        static void Check(bool ok, string what) { Line((ok ? "[通过] " : "[失败] ") + what); if (!ok) failed = true; }

        public static int Run(string outDir)
        {
            try { Directory.CreateDirectory(outDir); } catch { }
            Line("桌面图标跳舞 Windows 版 · 自检 · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Line("系统：" + Environment.OSVersion.VersionString);
            Line("");
            Line("== 一、计算部分 ==");
            MotionClip? clip = null;
            try { clip = Logic(); }
            catch (Exception ex) { failed = true; Line("[失败] 计算部分抛出异常：" + ex); }

            Line("");
            Line("== 二、探测这台机器（只记录，不算通过或失败）==");
            LoadResult? res = null;
            try { res = Probe(); }
            catch (Exception ex) { Line("探测抛出异常：" + ex); }
            try { Render(outDir, res, clip); }
            catch (Exception ex) { Line("离屏渲染抛出异常：" + ex); }
            try { PanelProbe(outDir, res); }
            catch (Exception ex) { Line("控制窗口排版探测抛出异常：" + ex); }
            L.Current = Lang.Zh;

            Line("");
            Line(failed ? "结论：计算部分有失败项。" : "结论：计算部分全部通过。窗口层级、热键、系统声音没有在这里测。");
            try { File.WriteAllText(Path.Combine(outDir, "selftest.txt"), report.ToString(), new UTF8Encoding(true)); } catch { }
            return failed ? 1 : 0;
        }

        static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        static Pose[] GridHomes(int n, double w, double h)
        {
            var p = new Pose[n];
            int rows = Math.Max(1, (int)((h - 60) / 100));
            for (int i = 0; i < n; i++)
                p[i] = new Pose(new Vec(40 + (i / rows) * 76, h - 60 - (i % rows) * 100), 0, 1, 1, 1, i);
            return p;
        }

        static MotionClip? Logic()
        {
            // 1. 动作数据
            MotionClip? clip = MotionClip.LoadEmbedded();
            Check(clip != null, "exe 里带着动作数据，并且能读出来");
            if (clip != null)
            {
                Line("    帧数 " + clip.FrameCount + "，时长 " + clip.Duration.ToString("0.00") + " 秒，最高点 " + clip.MaxY.ToString("0.000") +
                     "，横向 " + clip.MaxAbsX.ToString("0.000") + "，肢体 " + clip.MaxLocalX.ToString("0.000") + "，走位 " + clip.MaxRootX.ToString("0.000"));
                Check(clip.Duration > 5 && clip.Duration < 120, "动作时长合理");
                Check(clip.MaxY > 1.0 && clip.MaxY < 2.5, "小人高度合理（最高点在 1～2.5 个身高之间）");
                bool ok = true;
                double headMin = 9, headMax = -9;
                for (int i = 0; i < 400; i++)
                {
                    MotionClip.Sample s = clip.SampleAt(clip.Duration * 1.5 * i / 400);
                    foreach (Vec j in s.Joints) if (!Finite(j.X) || !Finite(j.Y)) ok = false;
                    if (!Finite(s.Ball.X) || !Finite(s.Ball.Y) || !Finite(s.BallAlpha)) ok = false;
                    double hy = s.Joints[(int)Joint.Nose].Y;
                    headMin = Math.Min(headMin, hy); headMax = Math.Max(headMax, hy);
                }
                Check(ok, "动作数据取样 400 次，全部是正常数字");
                Line("    鼻子高度范围 " + headMin.ToString("0.00") + " ～ " + headMax.ToString("0.00") + "（单位：身高）");
                Check(headMin > 0.2 && headMax < 2.0, "头的位置合理");
            }

            // 2. 四种队形 × 全屏 / 竖屏 × 不同数量
            var stages = new (string name, RectD r)[]
            {
                ("全屏", new RectD(56, 40, 1920 - 112, 1040 - 70)),
                ("竖屏", new RectD(697, 40, 525, 1040 - 70)),
            };
            foreach (int n in new[] { 5, 12, 48, 96 })
            {
                foreach (var (sname, rect) in stages)
                {
                    foreach (DanceMode mode in DanceModes.All)
                    {
                        var ch = new Choreographer { Stage = rect, IconSize = 48, Clip = clip, BallIndex = 0 };
                        Pose[] home = GridHomes(n, 1920, 1080);
                        var homes = new Vec[n];
                        for (int i = 0; i < n; i++) homes[i] = home[i].P;
                        ch.Homes = homes;
                        ch.GrooveUsesHomes = sname == "全屏";
                        ch.SetMode(mode, 0, home);
                        var clock = new BeatClock();
                        var silent = new BeatAnalyzer.Features();
                        bool ok = true;
                        double minX = 1e9, maxX = -1e9, minY = 1e9, maxY = -1e9;
                        double t = 0;
                        for (int f = 0; f < 1500; f++)
                        {
                            double dt = 1.0 / 60;
                            t += dt;
                            double d = clock.Update(dt, t, silent, 1);
                            Pose[] poses = ch.Poses(n, new Tempo { Beat = clock.Beat, DBeat = d, Dt = dt, Energy = clock.Energy, Pulse = clock.Pulse });
                            if (poses.Length != n) ok = false;
                            foreach (Pose p in poses)
                            {
                                if (!Finite(p.P.X) || !Finite(p.P.Y) || !Finite(p.Rot) || !Finite(p.Sx) || !Finite(p.Sy) || !Finite(p.Alpha)) ok = false;
                                if (p.Sx <= 0 || p.Sy <= 0 || p.Sx > 4 || p.Sy > 4) ok = false;
                                minX = Math.Min(minX, p.P.X); maxX = Math.Max(maxX, p.P.X);
                                minY = Math.Min(minY, p.P.Y); maxY = Math.Max(maxY, p.P.Y);
                            }
                        }
                        // 允许略微出界（外层会夹回安全范围），但不能飞到几个屏幕外
                        bool inRange = minX > -1500 && maxX < 3500 && minY > -1500 && maxY < 2600;
                        Check(ok && inRange, DanceModes.LogName(mode) + " · " + sname + " · " + n + " 个图标：25 秒，数字正常，范围 x " +
                              minX.ToString("0") + "～" + maxX.ToString("0") + "，y " + minY.ToString("0") + "～" + maxY.ToString("0"));
                    }
                }
            }

            // 2b. 打篮球：幅度拉到最小 / 最大、鼓点脉冲拉满时，小人不出舞台，而且完全不受鼓点影响
            if (clip != null)
            {
                foreach (var (sname, rect) in stages)
                {
                    foreach (double amp in new[] { 0.3, 1.0, 2.0 })
                    {
                        var a = new Choreographer { Stage = rect, IconSize = 48, Clip = clip, Amp = amp };
                        var b = new Choreographer { Stage = rect, IconSize = 48, Clip = clip, Amp = amp };
                        Pose[] home = GridHomes(48, 1920, 1080);
                        a.SetMode(DanceMode.Figure, 0, home);
                        b.SetMode(DanceMode.Figure, 0, home);
                        bool same = true;
                        double worst = 0;
                        for (int f = 0; f < 1200; f++)
                        {
                            double dt = 1.0 / 60;
                            Pose[] pa = a.Poses(48, new Tempo { Beat = f / 30.0, DBeat = 1 / 30.0, Dt = dt, Energy = 0.55, Pulse = 0 });
                            Pose[] pb = b.Poses(48, new Tempo { Beat = f / 30.0, DBeat = 1 / 30.0, Dt = dt, Energy = 1, Pulse = 1 });
                            for (int i = 0; i < 48; i++)
                            {
                                if (pa[i].P.X != pb[i].P.X || pa[i].P.Y != pb[i].P.Y || pa[i].Sx != pb[i].Sx || pa[i].Rot != pb[i].Rot) same = false;
                                if (i == 0) continue;                 // 0 号是球，抛出去时允许飞出舞台（外层会夹回屏幕内）
                                double over = Math.Max(Math.Max(rect.MinX - pa[i].P.X, pa[i].P.X - rect.MaxX), Math.Max(rect.MinY - pa[i].P.Y, pa[i].P.Y - rect.MaxY));
                                worst = Math.Max(worst, over);
                            }
                        }
                        Check(same, "打篮球 · " + sname + " · 幅度 " + amp.ToString("0.0") + "：有没有鼓点，画面完全一样（不跟拍）");
                        // 偶尔甩出舞台一点是设计内的（外层会夹回屏幕安全范围），这里只拦明显算错的情况
                        Check(worst < 400, "打篮球 · " + sname + " · 幅度 " + amp.ToString("0.0") + "：小人基本待在舞台里（身体最多超出舞台 " + worst.ToString("0") + " 点）");
                    }
                }
            }

            // 3. 节拍检测：合成一段 128 BPM 的底鼓，看能不能跟上
            {
                const double sr = 48000, bpm = 128;
                var an = new BeatAnalyzer();
                var clock = new BeatClock();
                int chunk = 480;
                var buf = new float[chunk];
                var rnd = new Random(7);
                double period = 60 / bpm;
                long total = (long)(sr * 14);
                for (long pos = 0; pos < total; pos += chunk)
                {
                    for (int i = 0; i < chunk; i++)
                    {
                        double ts = (pos + i) / sr;
                        double since = ts % period;
                        double kick = 0.6 * Math.Exp(-since / 0.09) * Math.Sin(2 * Math.PI * 100 * since);
                        buf[i] = (float)(kick + (rnd.NextDouble() - 0.5) * 0.01);
                    }
                    double start = pos / sr, now = (pos + chunk) / sr;
                    an.Process(buf, chunk, sr, start);
                    clock.Update(chunk / sr, now, an.Drain(now), 1);
                }
                Line("    合成 128 BPM 底鼓 14 秒 → 检出 " + clock.Bpm.ToString("0.0") + " BPM，" + an.DebugSummary());
                Check(Math.Abs(clock.Bpm - bpm) < 6, "节拍检测能跟上 128 BPM");
                Check(clock.Locked, "有声音时处于「跟随」状态");
                var quiet = new BeatClock();
                var none = new BeatAnalyzer.Features();
                for (int i = 0; i < 600; i++) quiet.Update(1.0 / 60, i / 60.0, none, 1);
                Check(Math.Abs(quiet.Beat - 20) < 0.01 && Math.Abs(quiet.Bpm - 120) < 0.01, "没声音时是 120 BPM 的内置节拍（10 秒走 20 拍）");
            }
            // 4. 界面语言：中文 / English 两套字都在，切换后立刻取到另一套；日志用的名字不跟着变
            {
                Lang keep = L.Current;
                bool both = true, logStays = true;
                var seen = new HashSet<string>();
                foreach (Lang lg in new[] { Lang.Zh, Lang.En })
                {
                    L.Current = lg;
                    foreach (DanceMode m in DanceModes.All)
                    {
                        string t = DanceModes.Title(m);
                        if (string.IsNullOrWhiteSpace(t) || !seen.Add(t)) both = false;
                        if (DanceModes.LogName(m) != (m == DanceMode.Snake ? "列队" : m == DanceMode.Figure ? "打篮球" : m == DanceMode.Groove ? "律动" : "自由")) logStays = false;
                    }
                }
                L.Current = Lang.En;
                bool en = DanceModes.Title(DanceMode.Figure) == "Basketball" && L.T("中", "en") == "en" && new Msg("中", "en").Text == "en";
                L.Current = Lang.Zh;
                bool zh = DanceModes.Title(DanceMode.Figure) == "打篮球" && L.T("中", "en") == "中" && new Msg("中", "en").Text == "中";
                L.Current = keep;
                Check(both && en && zh, "界面语言：四个模式名中英文各一套，切换语言后立刻换过来");
                Check(logStays, "界面语言：日志里用的模式名始终是中文，不随界面语言变");
                Check(L.Parse("zh") == Lang.Zh && L.Parse("EN") == Lang.En && L.Parse("") == null && L.Parse("fr") == null && L.Parse(null) == null
                      && L.Code(Lang.Zh) == "zh" && L.Code(Lang.En) == "en", "界面语言：设置文件里的 zh / en 能读能写，别的值当作没选过");
            }
            return clip;
        }

        /// <summary>
        /// 不显示窗口，把控制窗口按中文、英文各排一遍版，各存一张图（panel-zh.png / panel-en.png），
        /// 并记下有没有哪一行比窗口宽。只记录，不算通过或失败——真正显示出来什么样还是要在 Windows 上看。
        /// </summary>
        static void PanelProbe(string outDir, LoadResult? res)
        {
            var controller = new DanceController(Dispatcher.CurrentDispatcher);
            var panel = new PanelWindow(controller, new Settings { NoSave = true });
            // 把刚才探测到的桌面图标告诉它，这样图上的状态文字和篮球菜单就是这台机器上「就绪」时真正会显示的样子
            if (res != null)
            {
                var cat = new NameCatalog();
                foreach (DancerItem it in res.Items) (it.IsGuest ? cat.Guests : cat.Desktop).Add(it.Name);
                controller.SelfTestSetCatalog(cat);
            }
            foreach (Lang lg in new[] { Lang.Zh, Lang.En })
            {
                var notes = new List<string>();
                BitmapSource bmp = panel.SelfTestRender(lg, notes);
                string name = "panel-" + L.Code(lg) + ".png";
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(Path.Combine(outDir, name))) enc.Save(fs);
                Line("  控制窗口（" + (lg == Lang.Zh ? "中文" : "English") + "）：" + bmp.PixelWidth + "x" + bmp.PixelHeight + "，" + string.Join("；", notes) + "，图片 " + name);
            }
            L.Current = Lang.Zh;
        }

        static LoadResult? Probe()
        {
            LoadResult? res = null;
            Exception? err = null;
            var th = new Thread(() =>
            {
                try { res = DesktopLoader.Load(48, ""); }
                catch (Exception ex) { err = ex; }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
            if (!th.Join(TimeSpan.FromSeconds(90))) { Line("读桌面 90 秒没返回，放弃"); return null; }
            if (err != null) { Line("读桌面抛出异常：" + err); return null; }
            if (res == null) return null;
            foreach (string d in res.Diag) Line("  " + d);
            int withIcon = res.Items.FindAll(it => it.Icon != null).Count;
            Line("  共 " + res.Items.Count + " 个图标（桌面 " + res.DesktopCount + "，借用 " + (res.Items.Count - res.DesktopCount) + "），取到图片 " + withIcon + " 个");
            Line("  图标位置：" + (res.PositionsFromShell ? "从资源管理器读到了" : "没读到"));
            Line("  " + (res.CurtainNote.Length > 0 ? res.CurtainNote : "遮罩：无"));
            for (int i = 0; i < res.Items.Count && i < 8; i++)
            {
                DancerItem it = res.Items[i];
                Line("    " + (it.IsGuest ? "借用 " : "桌面 ") + it.Name + (it.HasPos ? "  中心 (" + it.PxX.ToString("0") + "," + it.PxY.ToString("0") + ")" : "") +
                     (it.Icon != null ? "  图片 " + it.Icon.PixelWidth + "x" + it.Icon.PixelHeight + " " + it.Icon.Format : "  没有图片"));
            }
            return res;
        }

        /// <summary>离屏画出每种队形的一帧，存成图片，人可以看一眼对不对。</summary>
        static void Render(string outDir, LoadResult? res, MotionClip? clip)
        {
            double scale = res != null ? res.Screen.Scale : 1;
            double w = res != null ? res.Screen.MonW / scale : 1920, h = res != null ? res.Screen.MonH / scale : 1080;
            double iconDip = res != null ? res.IconDip : 48;
            var items = new List<DancerItem>();
            if (res != null) items.AddRange(res.Items);
            for (int i = items.Count; i < 48; i++) items.Add(new DancerItem { Name = "图标 " + (i + 1), IsGuest = true });
            Pose[] home = GridHomes(items.Count, w, h);
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].HasPos) home[i].P = items[i].Home;
            }
            var homes = new Vec[items.Count];
            for (int i = 0; i < items.Count; i++) homes[i] = home[i].P;

            var host = new StageHost(w, h, iconDip, (res != null ? res.CellW : 76) - 6, 1.0, items);
            host.SetCurtain(res?.Curtain, res != null ? res.CurtainColor : Color.FromRgb(32, 36, 48));
            host.SetLabelsVisible(true, -1);
            host.Apply(home);
            Save(host, Path.Combine(outDir, "0-home.jpg"));

            var rect = new RectD(56, 40 + 48, w - 112, h - 48 - 70);
            int idx = 1;
            foreach (DanceMode mode in DanceModes.All)
            {
                var ch = new Choreographer { Stage = rect, IconSize = iconDip, Clip = clip, Homes = homes };
                ch.SetMode(mode, 0, home);
                var clock = new BeatClock();
                var silent = new BeatAnalyzer.Features();
                Pose[] poses = home;
                double t = 0;
                for (int f = 0; f < 60 * 14; f++)
                {
                    double dt = 1.0 / 60;
                    t += dt;
                    double d = clock.Update(dt, t, silent, 1);
                    poses = ch.Poses(items.Count, new Tempo { Beat = clock.Beat, DBeat = d, Dt = dt, Energy = clock.Energy, Pulse = clock.Pulse });
                }
                host.SetLabelsVisible(mode != DanceMode.Figure, -1);
                host.Apply(poses);
                Save(host, Path.Combine(outDir, idx + "-" + mode.ToString().ToLowerInvariant() + ".jpg"));
                idx++;
            }
            Line("  离屏渲染：已画出原位和四种队形（" + (int)w + "x" + (int)h + "），图片在输出文件夹里");
        }

        static void Save(StageHost host, string path)
        {
            RenderTargetBitmap bmp = host.RenderToBitmap();
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
    }
}
