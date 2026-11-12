using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DesktopDance
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // 自检（GitHub 上自动构建时会跑一遍）：DesktopDance.exe --selftest <输出文件夹>
            if (args.Length >= 1 && args[0] == "--selftest")
                return SelfTest.Run(args.Length >= 2 ? args[1] : "selftest-out");

            // 实跑测试（见 LiveTest.cs）：--livetest / --livetest-hold 和平时一样把程序开起来，再由脚本代替人去点；
            // --livetest-after 是上一个进程被强制结束之后做复核，不开窗口。
            string liveDir = args.Length >= 2 ? args[1] : "livetest-out";
            if (args.Length >= 1 && args[0] == "--livetest-after") return LiveTest.AfterKill(liveDir);
            bool liveHold = args.Length >= 1 && args[0] == "--livetest-hold";
            bool live = liveHold || (args.Length >= 1 && args[0] == "--livetest");

            // 界面语言：用户在控制窗口里选过就用选的；没选过，系统是中文用中文，否则英文。
            // 测试用：--lang zh|en 只对这一次启动生效，不会被记住。
            Settings settings = Settings.Load();
            if (live) settings.NoSave = true;
            string langFrom = "系统语言";
            Lang lang = L.SystemDefault();
            if (L.Parse(settings.Language) is Lang saved) { lang = saved; langFrom = "上次选的"; }
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--lang" && L.Parse(args[i + 1]) is Lang forced) { lang = forced; langFrom = "命令行"; }
            L.Current = lang;

            using var mutex = new Mutex(true, "Local\\DesktopDance-single-instance-7d0c1b0e", out bool first);
            if (!first)
            {
                if (live) return 4;          // 测试时不弹窗（没人去点「确定」）
                MessageBox.Show(L.T("DesktopDance 已经在运行了，看看屏幕左下角或任务栏。", "DesktopDance is already running. Look at the bottom-left corner of the screen, or the taskbar."),
                                "DesktopDance", MessageBoxButton.OK, MessageBoxImage.Information);
                return 0;
            }

            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
            Log.StartSession("桌面图标跳舞 Windows 版 " + version + " · " + Environment.OSVersion.VersionString + " · " + RuntimeInformation.FrameworkDescription);

            Log.Write("界面语言：" + (lang == Lang.Zh ? "中文" : "English") + "（" + langFrom + "）");

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var controller = new DanceController(app.Dispatcher);
            controller.BallName = settings.BallName;
            controller.UseSystemAudio = settings.FollowSystemAudio;

            // 界面线程上任何没接住的错误：先把舞台撤掉，再说别的。
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Write("界面线程出错：" + e.Exception);
                try { controller.EmergencyRestore(new Msg("程序出了点错，已停止并恢复桌面（详情见日志）", "Something went wrong. Stopped and restored the desktop (details are in the log)")); } catch { }
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("未处理的错误：" + e.ExceptionObject);

            // 热键在自己的线程上。正常情况：让界面线程撤掉舞台。
            // 界面线程 3 秒没反应（卡死）而舞台还盖着桌面：直接结束进程——窗口属于本进程，进程没了它就没了。
            controller.HotKeys.Pressed = id =>
            {
                DispatcherOperation op = app.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => controller.EmergencyRestore()));
                DispatcherOperationStatus st = op.Wait(TimeSpan.FromSeconds(3));
                if (st != DispatcherOperationStatus.Completed && controller.StageUp)
                {
                    Log.Write("界面 3 秒没有响应，直接结束进程（舞台窗口随进程消失，桌面不受影响）");
                    Process.GetCurrentProcess().Kill();
                }
            };
            controller.HotKeys.Start();

            // 分辨率 / 缩放 / 显示器变了：舞台大小就不对了，直接停下。
            SystemEvents.DisplaySettingsChanged += (s, e) => app.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (controller.State != DanceState.Idle) controller.EmergencyRestore(new Msg("屏幕设置变了，已停止并恢复桌面", "Display settings changed. Stopped and restored the desktop"));
            }));

            var panel = new PanelWindow(controller, settings);
            panel.Show();
            controller.RefreshCatalog();
            LiveTest? test = live ? LiveTest.Attach(app, controller, panel, liveDir, liveHold) : null;
            int code = app.Run();
            controller.HotKeys.Stop();
            Log.Write("退出");
            return test != null ? test.ExitCode : code;
        }
    }
}
