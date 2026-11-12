using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopDance
{
    public enum DanceState { Idle, Preparing, Running, Paused, Stopping }

    /// <summary>在一个单独的 STA 线程上跑一段活（读桌面要用 COM，不能卡住界面）。</summary>
    public static class StaWorker
    {
        public static void Run(Action work)
        {
            var th = new Thread(() =>
            {
                try { work(); }
                catch (Exception ex) { Log.Write("后台任务出错：" + ex); }
                finally
                {
                    try { Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch { }
                }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Name = "shell-worker";
            th.Start();
        }
    }

    /// <summary>
    /// 总控：开始 / 暂停 / 恢复，驱动每一帧。Mac 版 DanceController 的移植。
    /// 安全模型和 Mac 版一样：真实桌面图标从头到尾不被移动、不被隐藏、不被改名；
    /// 我们只是在它们上方盖一个属于本进程的窗口。窗口没了，桌面就是原样。
    /// </summary>
    public sealed class DanceController
    {
        readonly Dispatcher dispatcher;
        public DanceState State { get; private set; } = DanceState.Idle;
        public event Action? Changed;
        public event Action? CatalogChanged;
        public readonly HotKeys HotKeys = new HotKeys();

        // ---- 设置（控制窗口直接改这些）----
        DanceMode mode = DanceMode.Figure;
        bool portrait, showGuide = true, showLabels = true, autoSwitch, useSystemAudio = true;
        double amp = 1;
        int targetCount = 48;
        string ballName = "";

        public DanceMode Mode { get => mode; set { if (mode != value) { mode = value; ModeChanged(); } } }
        public bool Portrait { get => portrait; set { if (portrait != value) { portrait = value; LayoutStage(true); } } }
        public bool ShowGuide { get => showGuide; set { if (showGuide != value) { showGuide = value; LayoutStage(false); } } }
        public bool ShowLabels { get => showLabels; set { if (showLabels != value) { showLabels = value; RefreshLabels(); } } }
        public bool AutoSwitch { get => autoSwitch; set { autoSwitch = value; modeStartBeat = clock.Beat; } }
        public double Speed = 1;
        public double Amp { get => amp; set { amp = value; choreo.Amp = value; } }
        /// <summary>图标总数。跳舞中改它：拖完松手后按新数量重新排一次。</summary>
        public int TargetCount { get => targetCount; set { if (targetCount != value) { targetCount = value; ScheduleReload(); } } }
        /// <summary>名字里包含这段文字的图标当篮球；空 = 第一个图标。</summary>
        public string BallName
        {
            get => ballName;
            set
            {
                ballName = value ?? "";
                UpdateBall();
                // 选来当篮球的那个现在没在跳（留在原位的，或者还没借来的）：重新排一次，把它换上场
                if (IsActive && ballName.Length > 0 && !BallIsDancing()) { reloadForced = true; ScheduleReload(); }
            }
        }
        public bool UseSystemAudio
        {
            get => useSystemAudio;
            set { if (useSystemAudio != value) { useSystemAudio = value; if (IsActive) RestartAudio(); } }
        }

        // ---- 状态 ----
        public int Fps { get; private set; }
        public int DesktopCount { get; private set; }
        public NameCatalog Catalog { get; private set; } = new NameCatalog();
        public bool CatalogLoaded { get; private set; }
        // 给用户看的提示都存成中英文两份（Msg），显示时才按当前语言取：跳舞中切换语言也会跟着换。
        static readonly Msg AudioBuiltIn = new Msg("内置节拍", "Built-in beat");
        static readonly Msg AudioConnecting = new Msg("正在连接系统声音…", "Connecting to system audio…");
        static readonly Msg AudioFollowing = new Msg("跟随系统声音", "Following system audio");
        Msg note = Msg.None;
        Msg audioNote = AudioBuiltIn;
        string curtainNote = "";         // 写日志用，中文
        Msg curtainProblem = Msg.None;
        Msg verifyNote = Msg.None;
        Msg layerNote = Msg.None;

        StageWindow? stage;
        StageWindow? staleStage;         // 重排时被换下来的旧舞台：新舞台画出第一帧后再关，免得闪一下
        double staleDeadline;
        List<DancerItem> items = new List<DancerItem>();
        LoadResult? load;
        int dancerCount;                 // items 里前这么多个在跳舞；后面的桌面图标留在原位不动
        int bystanders;                  // 留在原位不动的桌面图标有几个
        int loadedTarget;                // 现在场上这批图标是按哪个「数量」取的
        bool reloadForced, reloading;
        DispatcherTimer? reloadTimer;
        readonly Choreographer choreo = new Choreographer();
        readonly BeatAnalyzer analyzer = new BeatAnalyzer();
        readonly BeatClock clock = new BeatClock();
        readonly SystemAudioCapture sysAudio;
        Task audioChain = Task.CompletedTask;
        int prepareToken;
        bool ticking;
        volatile bool stageUp;

        RectD visibleLocal;
        double stageW, stageH;
        double lastTick, modeStartBeat;
        double tempoRate = 1;            // 「打篮球」随音乐快慢的播放倍率（平滑后的）
        Pose[] lastPoses = Array.Empty<Pose>();
        Pose[]? transFrom;
        double transStart, transDur = 0.6, transStagger;
        int frames;
        double fpsStamp;

        public DanceController(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            sysAudio = new SystemAudioCapture(analyzer);
        }

        public bool IsActive => State == DanceState.Running || State == DanceState.Paused;
        /// <summary>舞台窗口现在是不是盖在桌面上（热键线程会读）。</summary>
        public bool StageUp => stageUp;
        public int Bpm => (int)M.Round(clock.Bpm);
        /// <summary>舞台窗口开出来后自己检查到的层级情况（中文，给日志和实跑测试看）。</summary>
        public string StageLayerNote => stage?.LayerNote ?? "";
        public int GuestCount => Math.Max(0, items.Count - DesktopCount);

        void SetState(DanceState s) { State = s; Changed?.Invoke(); }

        // ================= 开始 =================

        public void Start()
        {
            if (State == DanceState.Paused) { TogglePause(); return; }
            if (State != DanceState.Idle) return;
            note = Msg.None;
            verifyNote = Msg.None;
            SetState(DanceState.Preparing);
            int token = ++prepareToken;
            int target = targetCount;
            string ball = ballName;
            Log.Write("开始准备：目标 " + target + " 个图标，篮球「" + (ball.Length == 0 ? "第一个图标" : ball) + "」");
            LoadAsync(target, ball, token, false);
        }

        void LoadAsync(int target, string ball, int token, bool reload)
        {
            StaWorker.Run(() =>
            {
                LoadResult res;
                try { res = DesktopLoader.Load(target, ball); }
                catch (Exception ex)
                {
                    Log.Write("读取桌面出错：" + ex);
                    res = new LoadResult();
                    res.Diag.Add("读取桌面出错：" + ex.Message);
                }
                dispatcher.BeginInvoke(new Action(() => OnLoaded(res, token, target, reload)));
            });
        }

        void OnLoaded(LoadResult res, int token, int target, bool reload)
        {
            if (token != prepareToken) return;
            if (reload)
            {
                reloading = false;
                // 读图标的这一两秒里被暂停或停止了：先不换。暂停的话，继续时会按当时的数量再来一次。
                if (State != DanceState.Running) { Changed?.Invoke(); return; }
            }
            else if (State != DanceState.Preparing) return;
            foreach (string d in res.Diag) Log.Write("  " + d);
            if (res.Items.Count == 0)
            {
                note = new Msg("没有可用的图标（详情见日志）", "No icons available (see the log for details)");
                if (reload) { loadedTarget = target; Changed?.Invoke(); }      // 场上原来那批继续跳
                else SetState(DanceState.Idle);
                return;
            }
            // 结束后的复核要和最开始比，所以重排时沿用开始时读到的位置
            if (reload && load != null && load.PositionsFromShell && res.PositionsFromShell) res.Snapshot = load.Snapshot;
            loadedTarget = target;
            load = res;
            items = res.Items;
            DesktopCount = res.DesktopCount;
            // 「数量」是多少，就有多少个图标跳舞。桌面图标比它多时，排在前面的跳，其余的留在原位不动。
            dancerCount = Math.Min(items.Count, Math.Max(1, target));
            // 当篮球的那个必须在跳舞的那一批里
            int ballAt = ballName.Length > 0 ? items.FindIndex(it => DesktopLoader.Matches(it.Name, ballName)) : -1;
            if (ballAt >= dancerCount)
            {
                DancerItem swap = items[ballAt];
                items[ballAt] = items[dancerCount - 1];
                items[dancerCount - 1] = swap;
            }
            bystanders = 0;
            for (int i = dancerCount; i < items.Count; i++) if (!items[i].IsGuest) bystanders++;
            ScreenInfo sc = res.Screen;
            double k = sc.Scale;
            stageW = sc.MonW / k;
            stageH = sc.MonH / k;
            visibleLocal = new RectD((sc.WorkX - sc.MonX) / k, (sc.MonY + sc.MonH - sc.WorkY - sc.WorkH) / k, sc.WorkW / k, sc.WorkH / k);
            AssignHomes(res);
            curtainNote = res.CurtainNote;
            curtainProblem = res.CurtainProblem;

            try
            {
                var host = new StageHost(stageW, stageH, res.IconDip, res.CellW - 6, k, items);
                host.SetCurtain(res.Curtain, res.CurtainColor);
                var wnd = new StageWindow(sc, host);
                wnd.Clicked += () => { Log.Write("点击桌面，停止"); Stop(true); };
                Begin(wnd, res, reload);
            }
            catch (Exception ex)
            {
                Log.Write("搭舞台出错：" + ex);
                Teardown(new Msg("搭舞台出错：" + ex.Message, "Could not set up the stage: " + ex.Message));
            }
        }

        /// <summary>原位：真实图标用资源管理器里的位置；拿不到位置的和补位图标排进默认网格里还空着的格子。</summary>
        void AssignHomes(LoadResult res)
        {
            var taken = new List<Vec>();
            foreach (var it in items) if (it.HasPos) taken.Add(it.Home);
            int slot = 0;
            foreach (var it in items)
            {
                if (it.HasPos) continue;
                Vec p = GridHome(slot, res);
                int guard = 0;
                while (guard < 600 && taken.Exists(t => Math.Abs(t.X - p.X) < res.CellW * 0.9 && Math.Abs(t.Y - p.Y) < res.CellH * 0.9))
                {
                    slot++; guard++;
                    p = GridHome(slot, res);
                }
                it.Home = p;
                taken.Add(p);
                slot++;
            }
        }

        /// <summary>仿 Windows 桌面的默认排列：从左上角开始，一列一列往右排。</summary>
        Vec GridHome(int slot, LoadResult res)
        {
            RectD v = visibleLocal;
            int rows = Math.Max(1, (int)((v.H - 10) / res.CellH));
            int col = slot / rows, row = slot % rows;
            return new Vec(v.MinX + res.CellW / 2 + col * res.CellW, v.MaxY - 6 - res.IconDip / 2 - row * res.CellH);
        }

        /// <summary>reload：跳舞中按新数量换了一批图标。节拍和声音接着用，不从头来。</summary>
        void Begin(StageWindow wnd, LoadResult res, bool reload)
        {
            StageWindow? old = stage;
            if (old != null)
            {
                // 旧舞台先留着盖住桌面，等新舞台画出第一帧再关（由 OnRendering 到点关掉；最多等 1 秒）。
                // 一换下来就记进 staleStage：后面哪一步出错，Teardown 都会把它一起关掉。
                CloseStaleStage();
                staleStage = old;
                staleDeadline = Clock.Now + 1.0;
                wnd.ContentRendered += (s, e) => { if (staleStage == old) staleDeadline = Math.Min(staleDeadline, Clock.Now + 0.1); };
            }
            stage = wnd;
            var homes = new Vec[items.Count];
            for (int i = 0; i < items.Count; i++) homes[i] = items[i].Home;
            choreo.Homes = homes;
            choreo.Amp = amp;
            choreo.IconSize = res.IconDip;
            choreo.GridW = res.CellW;
            choreo.GridH = res.CellH;
            choreo.Clip = MotionClip.LoadDefault();
            UpdateBall();
            LayoutStage(false);
            if (!reload)
            {
                clock.Reset();
                analyzer.Reset();
                tempoRate = 1;
            }
            Pose[] home = HomePoses(0);
            choreo.SetMode(mode, clock.Beat, Dancing(home));
            wnd.Host.Apply(home);
            lastPoses = home;
            RefreshLabels();
            wnd.Open();
            if (staleStage != null) staleDeadline = Clock.Now + 1.0;
            stageUp = true;
            layerNote = wnd.LayerProblem;
            BeginTransition(home, 1.1, 0.5);
            HotKeys.SetEscape(true);

            modeStartBeat = clock.Beat;
            frames = 0;
            lastTick = Clock.Now;
            fpsStamp = lastTick;
            if (!ticking) { CompositionTarget.Rendering += OnRendering; ticking = true; }
            note = Msg.None;
            SetState(DanceState.Running);
            if (!reload) StartAudio();
            if (NeedsReload) ScheduleReload();
            Log.Write("打篮球动作：" + (choreo.Clip == null ? "手写动作（没有动作数据）" : "动作数据 " + choreo.Clip.Duration.ToString("0.0") + " 秒"));
            Log.Write((reload ? "重新排队：" : "开始：") + "跳舞 " + dancerCount + " 个，留在原位 " + bystanders + " 个；桌面图标 " + DesktopCount + " 个，补位 " + GuestCount + " 个，模式 " + DanceModes.LogName(mode) + "，" + curtainNote +
                      "，图标位置" + (res.PositionsFromShell ? "来自资源管理器" : "未知（从默认网格出发）"));
        }

        // ================= 暂停 / 停止 / 紧急恢复 =================

        public void TogglePause()
        {
            if (State == DanceState.Running) SetState(DanceState.Paused);
            else if (State == DanceState.Paused)
            {
                lastTick = Clock.Now;
                SetState(DanceState.Running);
                if (NeedsReload) ScheduleReload();
            }
        }

        // ================= 跳舞中改「数量」 =================

        /// <summary>场上这批图标和现在的设置对不上了（数量改了，或者要换一个没在跳的图标当篮球）。</summary>
        bool NeedsReload => reloadForced || loadedTarget != targetCount;

        /// <summary>
        /// 「数量」变了：跳舞中拖完松手后（0.35 秒没再动）按新数量重新排一次。
        /// 暂停时不打断暂停、准备期间也不丢这次改动：回到跳舞状态时会看 NeedsReload 再换。
        /// </summary>
        void ScheduleReload()
        {
            reloadTimer?.Stop();
            if (State != DanceState.Running) return;
            if (reloadTimer == null)
            {
                var t = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(0.35) };
                t.Tick += (s, e) => { t.Stop(); ReloadItems(); };
                reloadTimer = t;
            }
            reloadTimer.Start();
        }

        /// <summary>
        /// 按新的数量重新取图标并重新开场。读图标的一两秒里场上原来那批继续跳；
        /// Esc 热键一直保持注册，随时可以恢复。
        /// </summary>
        void ReloadItems()
        {
            if (State != DanceState.Running) return;
            int target = targetCount;
            int token = ++prepareToken;                  // 还没读完的上一次重排作废
            reloading = false;
            if (!NeedsReload) { Changed?.Invoke(); return; }
            reloadForced = false;
            reloading = true;
            Changed?.Invoke();
            Log.Write("数量改为 " + target + "，重新排队");
            LoadAsync(target, ballName, token, true);
        }

        void CloseStaleStage()
        {
            StageWindow? w = staleStage;
            staleStage = null;
            if (w == null) return;
            try { w.Close(); } catch (Exception ex) { Log.Write("关旧舞台窗口出错：" + ex.Message); }
        }

        /// <summary>正常停止：图标飞回原位后撤掉舞台。animated=false 时立刻撤。</summary>
        public void Stop(bool animated)
        {
            if (State == DanceState.Idle) return;
            if (State == DanceState.Running && animated)
            {
                StopAudio();
                State = DanceState.Stopping;
                BeginTransition(lastPoses, 0.45, 0.1);
                Changed?.Invoke();
            }
            else Teardown();
        }

        /// <summary>紧急恢复（Esc / 出错 / 屏幕设置变了）：不做动画，立刻撤掉舞台。</summary>
        public void EmergencyRestore(Msg? why = null)
        {
            if (State == DanceState.Idle && stage == null) return;
            Teardown(why);
        }

        /// <summary>problem：出了问题才传，会显示在控制窗口里；正常恢复不用说什么。</summary>
        void Teardown(Msg? problem = null)
        {
            prepareToken++;
            if (ticking) { CompositionTarget.Rendering -= OnRendering; ticking = false; }
            StopAudio();
            reloadTimer?.Stop();
            reloadForced = false;
            reloading = false;
            StageWindow? w = stage;
            stage = null;
            if (w != null)
            {
                try { w.Close(); } catch (Exception ex) { Log.Write("关舞台窗口出错：" + ex.Message); }
            }
            CloseStaleStage();
            stageUp = false;
            HotKeys.SetEscape(false);
            Dictionary<string, POINT>? before = load != null && load.PositionsFromShell ? load.Snapshot : null;
            load = null;
            items = new List<DancerItem>();
            lastPoses = Array.Empty<Pose>();
            transFrom = null;
            Fps = 0;
            note = problem ?? Msg.None;
            SetState(DanceState.Idle);
            Log.Write("已恢复桌面" + (problem != null ? "（" + problem.Value.Zh + "）" : ""));
            if (before != null && before.Count > 0) VerifyPositions(before);
            RefreshCatalog();
        }

        /// <summary>
        /// 结束后只读复核：再问一遍每个图标的位置，和开始前比。
        /// 本程序从不移动图标，所以正常应该一个都没变；这里只报告，不写回任何东西。
        /// </summary>
        void VerifyPositions(Dictionary<string, POINT> before)
        {
            StaWorker.Run(() =>
            {
                Dictionary<string, POINT>? now = DesktopLoader.ReadPositions();
                string msg;
                Msg shown = Msg.None;
                if (now == null) msg = "";
                else
                {
                    int moved = 0, same = 0;
                    foreach (var kv in before)
                    {
                        if (!now.TryGetValue(kv.Key, out POINT p)) continue;
                        if (p.X == kv.Value.X && p.Y == kv.Value.Y) same++; else moved++;
                    }
                    if (moved > 0)
                        shown = new Msg("注意：有 " + moved + " 个桌面图标的位置和开始前不一样（本程序不会移动图标，可能是系统或你自己动的）",
                                        "Note: " + moved + " desktop icon(s) are not where they were before the start (this app never moves icons; the system or you may have moved them)");
                    msg = moved == 0 ? "已复核：" + same + " 个桌面图标的位置和开始前一样"
                                     : "注意：有 " + moved + " 个桌面图标的位置和开始前不一样（本程序不会移动图标，可能是系统或你自己动的）";
                }
                if (msg.Length > 0) Log.Write(msg);
                // 一个都没变（正常情况）只记日志；有变化才在控制窗口里提醒
                dispatcher.BeginInvoke(new Action(() => { verifyNote = shown; Changed?.Invoke(); }));
            });
        }

        // ================= 音频 =================

        /// <summary>默认跟着电脑正在播放的声音跳。接不上或没声音时用内置 120 拍。</summary>
        void StartAudio()
        {
            analyzer.Reset();
            if (!useSystemAudio) { audioNote = AudioBuiltIn; return; }
            audioNote = AudioConnecting;
            audioChain = audioChain.ContinueWith(_ =>
            {
                Msg? err = sysAudio.Start();
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!IsActive || !useSystemAudio) { StopAudio(); return; }
                    if (err != null)
                    {
                        audioNote = AudioBuiltIn;
                        note = new Msg("接不上系统声音（" + err.Value.Zh + "），先用内置节拍",
                                       "Could not connect to system audio (" + err.Value.En + "); using the built-in beat");
                    }
                    else audioNote = AudioFollowing;
                    Changed?.Invoke();
                }));
            }, TaskScheduler.Default);
        }

        void StopAudio()
        {
            audioChain = audioChain.ContinueWith(_ =>
            {
                try { sysAudio.Stop(); } catch (Exception ex) { Log.Write("停止系统声音出错：" + ex.Message); }
            }, TaskScheduler.Default);
        }

        void RestartAudio()
        {
            StopAudio();
            StartAudio();
            Changed?.Invoke();
        }

        void UpdateBall()
        {
            int idx = 0;
            if (ballName.Length > 0)
            {
                int found = items.FindIndex(it => DesktopLoader.Matches(it.Name, ballName));
                if (found >= 0 && found < dancerCount) idx = found;
            }
            choreo.BallIndex = idx;
            RefreshLabels();
        }

        bool BallIsDancing()
        {
            int found = items.FindIndex(it => DesktopLoader.Matches(it.Name, ballName));
            return found >= 0 && found < dancerCount;
        }

        void RefreshLabels()
        {
            stage?.Host.SetLabelsVisible(showLabels, mode == DanceMode.Figure ? choreo.BallIndex : -1);
        }

        /// <summary>后台刷新「可以当篮球的图标名字」。</summary>
        public void RefreshCatalog()
        {
            StaWorker.Run(() =>
            {
                NameCatalog cat = DesktopLoader.ListNames();
                dispatcher.BeginInvoke(new Action(() =>
                {
                    Catalog = cat;
                    CatalogLoaded = true;
                    CatalogChanged?.Invoke();
                    Changed?.Invoke();
                }));
            });
        }

        /// <summary>只给自检用：直接告诉它桌面上有哪些图标（平时是 RefreshCatalog 在后台读出来的），好让控制窗口显示成「就绪」的样子。</summary>
        public void SelfTestSetCatalog(NameCatalog cat)
        {
            Catalog = cat;
            CatalogLoaded = true;
            CatalogChanged?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>可以当篮球的图标名字（给控制窗口的下拉菜单用）。</summary>
        public List<string> CandidateNames()
        {
            var all = new List<string>(Catalog.Desktop);
            int room = Math.Max(0, TargetCount - Catalog.Desktop.Count);
            for (int i = 0; i < Catalog.Guests.Count && i < room; i++) all.Add(Catalog.Guests[i]);
            return all;
        }

        // ================= 布局与过渡 =================

        Pose[] HomePoses(double guestAlpha)
        {
            var p = new Pose[items.Count];
            for (int i = 0; i < items.Count; i++)
                p[i] = new Pose(items[i].Home, 0, 1, 1, items[i].IsGuest ? guestAlpha : 1, i);
            return p;
        }

        /// <summary>只取跳舞的那一批（items 里排在前面的 dancerCount 个）。</summary>
        Pose[] Dancing(Pose[] all)
        {
            if (all.Length <= dancerCount) return all;
            var d = new Pose[dancerCount];
            Array.Copy(all, d, dancerCount);
            return d;
        }

        /// <summary>跳舞的那一批后面接上不跳的：桌面图标留在原位不动；没上场的借用图标不显示。</summary>
        Pose[] WithBystanders(Pose[] dancing)
        {
            if (dancing.Length >= items.Count) return dancing;
            var all = new Pose[items.Count];
            Array.Copy(dancing, all, dancing.Length);
            for (int i = dancing.Length; i < all.Length; i++)
                all[i] = new Pose(items[i].Home, 0, 1, 1, items[i].IsGuest ? 0 : 1, i - 10000);     // z 很小：跳舞的图标从它们上面经过
            return all;
        }

        void BeginTransition(Pose[] startPoses, double dur, double stagger)
        {
            transFrom = startPoses;
            transStart = Clock.Now;
            transDur = dur;
            transStagger = stagger;
        }

        void ModeChanged()
        {
            if (!IsActive) return;
            choreo.SetMode(mode, clock.Beat, Dancing(lastPoses));
            modeStartBeat = clock.Beat;
            BeginTransition(lastPoses, 0.7, 0.25);
            RefreshLabels();
            Changed?.Invoke();
        }

        /// <summary>算出舞台范围：全屏 = 任务栏以外的工作区；竖屏 = 其中居中的 9:16。</summary>
        void LayoutStage(bool animated)
        {
            StageWindow? wnd = stage;
            if (wnd == null) return;
            RectD v = visibleLocal;
            RectD area = v;
            RectD? guideRect = null;
            if (portrait)
            {
                double w = Math.Min(v.W, M.Round(v.H * 9 / 16));
                area = new RectD(M.Round(v.MidX - w / 2), v.MinY, w, v.H);
                guideRect = area.Inset(1, 1);
            }
            RectD dance = area.Inset(portrait ? 30 : 56, 0);
            dance.Y += 40;
            dance.H -= 40 + 30;
            choreo.Stage = dance;
            choreo.GrooveUsesHomes = !portrait;
            wnd.Host.SetGuide(showGuide && portrait ? guideRect : null);
            if (animated && State == DanceState.Running) BeginTransition(lastPoses, 0.6, 0.2);
        }

        // ================= 每一帧 =================

        void OnRendering(object? sender, EventArgs e)
        {
            double now = Clock.Now;
            double dt = Math.Min(Math.Max(now - lastTick, 0), 0.05);
            if (dt <= 0) return;                                  // 同一帧里被叫了两次
            lastTick = now;
            StageWindow? wnd = stage;
            if (staleStage != null && now >= staleDeadline) CloseStaleStage();
            if ((State != DanceState.Running && State != DanceState.Stopping) || wnd == null) return;

            frames++;
            if (now - fpsStamp >= 1)
            {
                int newFps = (int)M.Round(frames / (now - fpsStamp));
                frames = 0;
                fpsStamp = now;
                bool changed = newFps != Fps;
                Fps = newFps;
                // 每秒刷新一次状态文字（有没有听到声音、BPM 都会变）
                if (changed || State == DanceState.Running) Changed?.Invoke();
            }

            Pose[] poses;
            if (State == DanceState.Stopping)
            {
                poses = HomePoses(0);
            }
            else
            {
                BeatAnalyzer.Features feat = analyzer.Drain(now);
                double dBeat = clock.Update(dt, now, feat, Speed);
                // 「打篮球」跟音乐的方式：歌快动作就快、歌慢动作就慢（100 拍 = 原速，范围 0.75–1.4 倍）。
                // 速度是慢慢滑过去的（约 2 秒），不在鼓点上做任何瞬间变化，所以不会抖。
                // 没在听音乐、没听到声音或还没跟上节拍时回到原速。
                bool following = useSystemAudio && sysAudio.Running && sysAudio.Hearing && clock.Locked;
                double tempoTarget = following ? Math.Min(Math.Max(clock.Bpm / 100, 0.75), 1.4) : 1;
                tempoRate += (tempoTarget - tempoRate) * Math.Min(1, dt / 2.0);
                choreo.ClipRate = Speed * tempoRate;
                if (autoSwitch && clock.Beat - modeStartBeat >= DanceModes.AutoBeats(mode))
                {
                    DanceMode[] all = DanceModes.All;
                    Mode = all[(Array.IndexOf(all, mode) + 1) % all.Length];
                }
                poses = WithBystanders(choreo.Poses(dancerCount, new Tempo { Beat = clock.Beat, DBeat = dBeat, Dt = dt, Energy = clock.Energy, Pulse = clock.Pulse }));
            }

            Pose[]? startPoses = transFrom;
            if (startPoses != null && startPoses.Length == poses.Length)
            {
                double el = now - transStart;
                int n = Math.Max(poses.Length, 1);
                for (int i = 0; i < poses.Length && i < dancerCount; i++)
                {
                    double ti = (el - transStagger * i / n) / transDur;
                    poses[i] = Pose.Mix(startPoses[i], poses[i], M.Smooth(ti));
                }
                if (el >= transDur + transStagger)
                {
                    transFrom = null;
                    if (State == DanceState.Stopping) { Teardown(); return; }
                }
            }
            else if (State == DanceState.Stopping)
            {
                Teardown();
                return;
            }

            // 不让任何跳舞的图标跑到任务栏下面或屏幕外面（留在原位的不用管）
            RectD safe = visibleLocal.Inset(22, 22);
            for (int i = 0; i < poses.Length && i < dancerCount; i++)
            {
                Vec p = poses[i].P;
                poses[i].P = new Vec(M.Clamp(p.X, safe.MinX, safe.MaxX), M.Clamp(p.Y, safe.MinY, safe.MaxY));
            }
            wnd.Host.Apply(poses);
            lastPoses = poses;
        }

        // ================= 状态文字 =================

        public string StatusText
        {
            get
            {
                var parts = new List<string>();
                switch (State)
                {
                    case DanceState.Idle:
                        if (!CatalogLoaded) parts.Add(L.T("就绪 · 正在数桌面图标…", "Ready · counting desktop icons…"));
                        else
                        {
                            int n = Catalog.Desktop.Count;
                            parts.Add(L.T("就绪 · 桌面上有 " + n + " 个图标", "Ready · " + n + " icons on the desktop"));
                            if (n < TargetCount) parts.Add(L.T("不足 " + TargetCount + " 个，开始时借已安装应用的图标补位（只显示，不建文件）",
                                                               "Fewer than " + TargetCount + ": icons of installed apps will fill in (display only, no files are created)"));
                        }
                        break;
                    case DanceState.Preparing:
                        parts.Add(L.T("准备中…（读图标和壁纸，通常 1～3 秒）", "Preparing… (reading icons and wallpaper, usually 1–3 seconds)"));
                        break;
                    default:
                        parts.Add(State == DanceState.Paused ? L.T("已暂停", "Paused")
                                  : (State == DanceState.Stopping ? L.T("正在回到原位…", "Returning to place…") : L.T("跳舞中", "Dancing")));
                        parts.Add(bystanders > 0
                            ? L.T(dancerCount + " 个图标在跳，其余 " + bystanders + " 个留在原位", dancerCount + " icons dancing, the other " + bystanders + " stay in place")
                            : L.T("桌面图标 " + DesktopCount + " + 补位 " + GuestCount, "Desktop icons " + DesktopCount + " + borrowed " + GuestCount));
                        if (reloading) parts.Add(L.T("正在按新的数量重新排队…", "Rearranging for the new number…"));
                        parts.Add(audioNote.Text + " · " + Bpm + " BPM · " + Fps + " fps");
                        if (mode == DanceMode.Figure && Math.Abs(tempoRate - 1) > 0.02)
                            parts.Add(L.T("跟着音乐：动作速度 " + tempoRate.ToString("0.00") + " 倍", "Following the music: speed ×" + tempoRate.ToString("0.00")));
                        if (useSystemAudio && sysAudio.Running && !sysAudio.Hearing)
                            parts.Add(L.T("现在没听到声音，先按内置节拍跳。电脑一放歌就会自动跟上",
                                          "No sound right now, so it is dancing to the built-in beat. It will follow as soon as music plays"));
                        if (!curtainProblem.IsEmpty) parts.Add(curtainProblem.Text);        // 壁纸正常铺上了就不用说；铺不上才说明背景为什么变了
                        if (!layerNote.IsEmpty) parts.Add(layerNote.Text);
                        if (!HotKeys.EscapeRegistered) parts.Add(L.T("Esc 被别的软件占用了，恢复请点「恢复桌面」或在桌面上点一下",
                                                                     "Esc is taken by another app; click Restore or click the desktop instead"));
                        break;
                }
                if (!note.IsEmpty) parts.Add(note.Text);
                if (State == DanceState.Idle && !verifyNote.IsEmpty) parts.Add(verifyNote.Text);
                return string.Join("\n", parts);
            }
        }

        public string AudioDebug => analyzer.DebugSummary() + "，" + (clock.Locked ? "已跟上声音" : "没有声音");
    }
}
