using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopDance
{
    /// <summary>左下角的小控制窗口。上面所有的字都有中文 / English 两套，在最下面的「语言」里切换，立刻生效。</summary>
    public sealed class PanelWindow : Window
    {
        readonly DanceController c;
        readonly Settings settings;
        readonly Button startBtn = new Button();
        readonly Button pauseBtn = new Button();
        readonly Button restoreBtn = new Button();
        readonly ComboBox modeBox = new ComboBox();
        readonly CheckBox autoChk = new CheckBox();
        readonly ComboBox ballBox = new ComboBox();
        readonly CheckBox sysChk = new CheckBox();
        readonly Slider speedSlider = new Slider { Minimum = 0.5, Maximum = 2, Value = 1 };
        readonly Slider ampSlider = new Slider { Minimum = 0.3, Maximum = 2, Value = 1 };
        readonly Slider countSlider = new Slider { Minimum = 12, Maximum = 96, Value = 48, IsSnapToTickEnabled = true, TickFrequency = 1 };
        readonly TextBlock countLabel = new TextBlock { Text = "48", Width = 22, VerticalAlignment = VerticalAlignment.Center };
        readonly CheckBox labelChk = new CheckBox();
        readonly CheckBox hideChk = new CheckBox { FontSize = 11 };
        readonly ComboBox langBox = new ComboBox();
        readonly TextBlock hint = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.DimGray };
        // 两个长句子的勾选项：字放在会自动换行的文本块里，英文比窗口宽时折成两行而不是被截掉
        readonly TextBlock sysText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        readonly TextBlock hideText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        // 每一行左边的小标题（模式 / 舞台 / …）
        readonly TextBlock capMode = Caption(), capBall = Caption(), capSpeed = Caption(),
                           capAmp = Caption(), capCount = Caption(), capLang = Caption();
        bool updating;
        bool hideUnsupported;

        public PanelWindow(DanceController controller, Settings settings)
        {
            c = controller;
            this.settings = settings;
            Title = "DesktopDance";
            Width = 340;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.CanMinimize;
            Topmost = true;
            ShowInTaskbar = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            FontSize = 12;
            Build();
            ApplyLanguage();
            Loaded += (s, e) => { PlaceBottomLeft(); ApplyCaptureVisibility(); };
            Closed += (s, e) =>
            {
                Log.Write("控制窗口被关闭，恢复桌面并退出");
                c.EmergencyRestore();
                Application.Current?.Shutdown();
            };
            c.Changed += Refresh;
            c.CatalogChanged += ReloadBallMenu;
            ReloadBallMenu();
            Refresh();
        }

        static FrameworkElement Row(params UIElement[] children)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9) };
            foreach (UIElement ch in children) p.Children.Add(ch);
            return p;
        }

        static TextBlock Caption() =>
            new TextBlock { Width = 38, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };

        static FrameworkElement SliderRow(TextBlock cap, Slider slider, params UIElement[] tail)
        {
            var g = new DockPanel { Margin = new Thickness(0, 0, 0, 9), LastChildFill = true };
            DockPanel.SetDock(cap, Dock.Left);
            g.Children.Add(cap);
            for (int i = tail.Length - 1; i >= 0; i--)
            {
                DockPanel.SetDock(tail[i], Dock.Right);
                g.Children.Add(tail[i]);
            }
            slider.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(slider);
            return g;
        }

        /// <summary>搭控件、接事件。这里不写任何给人看的字——字都在 ApplyLanguage 里，换语言时重填一遍。</summary>
        void Build()
        {
            foreach (Button b in new[] { startBtn, pauseBtn, restoreBtn })
            {
                b.Padding = new Thickness(12, 4, 12, 4);
                b.Margin = new Thickness(0, 0, 8, 0);
                b.MinWidth = 76;
            }
            startBtn.IsDefault = true;
            startBtn.Click += (s, e) => c.Start();
            pauseBtn.Click += (s, e) => c.TogglePause();
            restoreBtn.Click += (s, e) => c.Stop(true);

            modeBox.Width = 110;
            modeBox.Margin = new Thickness(0, 0, 10, 0);
            modeBox.SelectionChanged += (s, e) =>
            {
                if (updating) return;
                int i = modeBox.SelectedIndex;
                if (i >= 0 && i < DanceModes.All.Length) c.Mode = DanceModes.All[i];
                Refresh();
            };
            autoChk.VerticalAlignment = VerticalAlignment.Center;
            autoChk.Click += (s, e) => { c.AutoSwitch = autoChk.IsChecked == true; Refresh(); };

            // 以前这里有一行「舞台」（全屏 / 9:16 竖屏 + 取景框），已经拿掉：固定用全屏。
            // 竖屏排版的代码还在 DanceController.Portrait / Choreographer 里，只是界面上不再露出。

            ballBox.Width = 250;
            ballBox.SelectionChanged += (s, e) =>
            {
                if (updating) return;
                int i = ballBox.SelectedIndex;
                c.BallName = i <= 0 ? "" : (ballBox.SelectedItem as string ?? "");
                this.settings.BallName = c.BallName;
                this.settings.Save();
            };

            sysChk.Content = sysText;
            sysChk.Margin = new Thickness(0, 0, 0, 9);
            sysChk.Click += (s, e) =>
            {
                c.UseSystemAudio = sysChk.IsChecked == true;
                this.settings.FollowSystemAudio = c.UseSystemAudio;
                this.settings.Save();
                Refresh();
            };

            speedSlider.ValueChanged += (s, e) => { if (!updating) c.Speed = speedSlider.Value; };
            ampSlider.ValueChanged += (s, e) => { if (!updating) c.Amp = ampSlider.Value; };
            countSlider.ValueChanged += (s, e) =>
            {
                if (updating) return;
                int n = (int)Math.Round(countSlider.Value);
                if (n != c.TargetCount) { c.TargetCount = n; ReloadBallMenu(); }
                countLabel.Text = n.ToString();
                Refresh();
            };
            countLabel.Margin = new Thickness(6, 0, 8, 0);
            labelChk.VerticalAlignment = VerticalAlignment.Center;
            labelChk.Click += (s, e) => { c.ShowLabels = labelChk.IsChecked == true; Refresh(); };

            hideChk.Content = hideText;
            hideChk.Margin = new Thickness(0, 0, 0, 9);
            hideChk.IsChecked = settings.HidePanelFromCapture;
            hideChk.Click += (s, e) =>
            {
                this.settings.HidePanelFromCapture = hideChk.IsChecked == true;
                this.settings.Save();
                ApplyCaptureVisibility();
                Refresh();
            };

            // 语言：两项的名字不翻译，谁都认得出自己的那一项
            langBox.Items.Add("中文");
            langBox.Items.Add("English");
            langBox.Width = 110;
            langBox.SelectionChanged += (s, e) =>
            {
                if (updating) return;
                SetLanguage(langBox.SelectedIndex == 1 ? Lang.En : Lang.Zh);
            };

            var stack = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
            stack.Children.Add(Row(startBtn, pauseBtn, restoreBtn));
            stack.Children.Add(Row(capMode, modeBox, autoChk));
            stack.Children.Add(Row(capBall, ballBox));
            stack.Children.Add(sysChk);
            stack.Children.Add(SliderRow(capSpeed, speedSlider));
            stack.Children.Add(SliderRow(capAmp, ampSlider));
            stack.Children.Add(SliderRow(capCount, countSlider, countLabel, labelChk));
            stack.Children.Add(hideChk);
            stack.Children.Add(Row(capLang, langBox));
            stack.Children.Add(hint);
            stack.Children.Add(status);
            Content = stack;
        }

        /// <summary>
        /// 按当前语言把所有固定的字填一遍（按钮、小标题、勾选项、模式名、鼠标悬停提示）。
        /// 只换字和几个宽度，不碰任何设置，也不触发任何动作——跳舞中切换不会打断跳舞。
        /// 英文和 Mac 版用同一套措辞。
        /// </summary>
        void ApplyLanguage()
        {
            updating = true;
            try
            {
                bool zh = L.Current == Lang.Zh;
                // 英文的小标题更长（Language），窗口和小标题都放宽一点
                Width = zh ? 340 : 360;
                foreach (TextBlock cap in new[] { capMode, capBall, capSpeed, capAmp, capCount, capLang }) cap.Width = zh ? 38 : 62;
                ballBox.Width = zh ? 250 : 236;

                pauseBtn.Content = L.T("暂停", "Pause");
                restoreBtn.Content = L.T("恢复桌面", "Restore");
                autoChk.Content = L.T("自动切换", "Auto switch");
                labelChk.Content = L.T("图标名称", "Names");
                sysText.Text = L.T("跟着电脑正在播放的音乐跳", "Follow the music playing on this PC");
                hideText.Text = L.T("截屏 / 录屏时隐藏本窗口", "Hide this window from screenshots / recordings");
                hint.Text = L.T("Esc：立刻恢复桌面", "Press Esc to restore instantly");

                capMode.Text = L.T("模式", "Mode");
                capBall.Text = L.T("篮球", "Ball");
                capSpeed.Text = L.T("速度", "Speed");
                capAmp.Text = L.T("幅度", "Size");
                capCount.Text = L.T("数量", "Number");
                capLang.Text = L.T("语言", "Language");

                ballBox.ToolTip = L.T("「打篮球」模式里用哪个图标当篮球", "Which icon plays the ball in Basketball mode");
                sysChk.ToolTip = L.T("勾上：听电脑正在播的声音（网易云、抖音、浏览器都行）打拍子，不用麦克风。不勾或没声音：用内置 120 拍",
                                     "Ticked: follows the beat of whatever this PC is playing (music app, video, browser); no microphone is used. Unticked or silent: uses a built-in 120 BPM beat");
                hideChk.ToolTip = L.T("勾上后，本窗口不会出现在截图和录屏里（跳舞的图标不受影响）",
                                      "When ticked, this window is left out of screenshots and recordings (the dancing icons are not affected)");

                // 模式名换一遍。updating 开着，所以这里的清空 / 重选不会被当成「用户换了模式」
                modeBox.Items.Clear();
                foreach (DanceMode m in DanceModes.All) modeBox.Items.Add(DanceModes.Title(m));
                modeBox.SelectedIndex = Array.IndexOf(DanceModes.All, c.Mode);
                langBox.SelectedIndex = zh ? 0 : 1;
            }
            finally { updating = false; }
        }

        /// <summary>换语言：记住选择，把窗口上的字全部换掉。不重启、不重建窗口、不动舞台。</summary>
        void SetLanguage(Lang lang)
        {
            if (lang == L.Current) return;
            L.Current = lang;
            settings.Language = L.Code(lang);
            settings.Save();
            Log.Write("界面语言改为：" + (lang == Lang.Zh ? "中文" : "English"));
            ApplyLanguage();
            ReloadBallMenu();
            Refresh();
            // 换了语言窗口可能变高一点：要是底边掉到任务栏下面去了，往上挪回来（没掉下去就不动它）
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(KeepOnScreen));
        }

        /// <summary>只给实跑测试用：和用户在「语言」下拉里选了一项是同一条路。</summary>
        public void SelfTestSwitchLanguage(Lang lang) => SetLanguage(lang);

        /// <summary>放在主屏左下角（任务栏上方），不挡队形。</summary>
        void PlaceBottomLeft()
        {
            Rect wa = SystemParameters.WorkArea;
            Left = wa.Left + 16;
            Top = Math.Max(wa.Top, wa.Bottom - ActualHeight - 16);
        }

        void KeepOnScreen()
        {
            try
            {
                if (WindowState != WindowState.Normal) return;
                Rect wa = SystemParameters.WorkArea;
                if (Top + ActualHeight > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - ActualHeight - 16);
                if (Left + ActualWidth > wa.Right) Left = Math.Max(wa.Left, wa.Right - ActualWidth - 16);
            }
            catch (Exception ex) { Log.Write("挪控制窗口出错：" + ex.Message); }
        }

        public void Refresh()
        {
            updating = true;
            try
            {
                DanceState st = c.State;
                startBtn.Content = st == DanceState.Paused ? L.T("继续", "Resume") : L.T("开始蹦迪", "Start");
                startBtn.IsEnabled = st == DanceState.Idle || st == DanceState.Paused;
                pauseBtn.IsEnabled = st == DanceState.Running;
                restoreBtn.IsEnabled = st != DanceState.Idle;
                modeBox.SelectedIndex = Array.IndexOf(DanceModes.All, c.Mode);
                autoChk.IsChecked = c.AutoSwitch;
                labelChk.IsChecked = c.ShowLabels;
                sysChk.IsChecked = c.UseSystemAudio;
                countLabel.Text = c.TargetCount.ToString();
                string text = c.StatusText;
                if (hideUnsupported)
                    text += "\n" + L.T("这台电脑的系统不支持「录屏时隐藏本窗口」（需要 Windows 10 2004 或更新）",
                                       "This version of Windows cannot hide this window from recordings (needs Windows 10 version 2004 or newer)");
                status.Text = text;
            }
            finally { updating = false; }
        }

        void ReloadBallMenu()
        {
            updating = true;
            try
            {
                ballBox.Items.Clear();
                ballBox.Items.Add(L.T("自动（第一个图标）", "Auto (first icon)"));
                List<string> names = c.CandidateNames();
                foreach (string n in names) ballBox.Items.Add(n);
                int sel = 0;
                string key = c.BallName;
                if (key.Length > 0)
                {
                    int i = names.FindIndex(n => DesktopLoader.Matches(n, key));
                    if (i >= 0) sel = i + 1;
                    else
                    {
                        // 上次选的那个现在不在列表里（比如数量调小了）：保留这个选择，开始时会单独把它补进来
                        ballBox.Items.Add(key);
                        sel = ballBox.Items.Count - 1;
                    }
                }
                ballBox.SelectedIndex = sel;
            }
            finally { updating = false; }
        }

        /// <summary>默认可以被截屏；勾上后全屏录制时本窗口不会入镜（Windows 10 2004 及以上）。</summary>
        void ApplyCaptureVisibility()
        {
            try
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;
                bool hide = settings.HidePanelFromCapture;
                bool ok = Native.SetWindowDisplayAffinity(h, hide ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
                hideUnsupported = hide && !ok;
                if (hide) Log.Write("录屏时隐藏控制窗口：" + (ok ? "已开启" : "系统不支持"));
            }
            catch (Exception ex) { Log.Write("设置录屏隐藏出错：" + ex.Message); }
        }

        /// <summary>
        /// 只给自检用：不显示窗口，按指定语言把面板内容排一遍版、画成一张图，
        /// 并检查有没有哪一行（不会自动换行的那些）比窗口里能放的宽度还宽——那样右边的字会被截掉。
        /// 不保存语言设置。
        /// </summary>
        public BitmapSource SelfTestRender(Lang lang, List<string> notes)
        {
            L.Current = lang;
            ApplyLanguage();
            ReloadBallMenu();
            Refresh();
            var root = (StackPanel)Content;
            double w = Width - 16;                                   // 窗口宽度减去左右边框，大约就是里面能用的宽度
            double avail = w - root.Margin.Left - root.Margin.Right;
            int row = 0, tooWide = 0;
            foreach (UIElement child in root.Children)
            {
                row++;
                if (!(child is Panel)) continue;                     // 勾选项、提示、状态文字会自己换行，不用查
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (child.DesiredSize.Width > avail + 0.5)
                {
                    tooWide++;
                    notes.Add("第 " + row + " 行需要 " + child.DesiredSize.Width.ToString("0") + " 宽，窗口里只有 " + avail.ToString("0"));
                }
            }
            if (tooWide == 0) notes.Add("每一行都放得下（可用宽度 " + avail.ToString("0") + "）");
            root.Background = Brushes.White;
            root.Measure(new Size(w, double.PositiveInfinity));
            double h = Math.Max(1, root.DesiredSize.Height);
            root.Arrange(new Rect(0, 0, w, h));
            root.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            bmp.Freeze();
            return bmp;
        }
    }
}
