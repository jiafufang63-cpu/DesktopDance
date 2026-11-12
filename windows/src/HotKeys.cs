using System;
using System.Threading;

namespace DesktopDance
{
    /// <summary>
    /// 全局热键：只有 Esc，而且只在跳舞期间占用。
    /// 热键跑在自己的线程上，不依赖界面线程——万一界面卡死，按热键仍然有反应（见 Program 里的处理：
    /// 界面 3 秒内没响应就直接结束进程，舞台窗口随进程一起消失）。
    /// </summary>
    public sealed class HotKeys
    {
        public const int IdEscape = 2;
        const uint MsgEscOn = Native.WM_APP + 1, MsgEscOff = Native.WM_APP + 2, MsgQuit = Native.WM_APP + 3;

        readonly ManualResetEventSlim ready = new ManualResetEventSlim(false);
        Thread? thread;
        uint threadId;
        volatile bool escOk;

        /// <summary>热键按下时调用（在热键线程上）。参数是 IdEscape。</summary>
        public Action<int>? Pressed;

        public bool EscapeRegistered => escOk;

        public void Start()
        {
            if (thread != null) return;
            thread = new Thread(Loop) { IsBackground = true, Name = "hotkeys" };
            thread.Start();
            ready.Wait(3000);
        }

        void Loop()
        {
            try
            {
                threadId = Native.GetCurrentThreadId();
                Native.PeekMessage(out MSG _, IntPtr.Zero, 0, 0, 0);          // 让系统给这个线程建好消息队列
            }
            finally { ready.Set(); }

            while (Native.GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == Native.WM_HOTKEY)
                {
                    int id = (int)msg.wParam.ToInt64();
                    Log.Write("热键触发：Esc");
                    try { Pressed?.Invoke(id); } catch (Exception ex) { Log.Write("热键处理出错：" + ex.Message); }
                }
                else if (msg.message == MsgEscOn)
                {
                    if (!escOk)
                    {
                        escOk = Native.RegisterHotKey(IntPtr.Zero, IdEscape, Native.MOD_NOREPEAT, Native.VK_ESCAPE);
                        if (!escOk) Log.Write("热键 Esc 注册失败（可能被别的软件占用），请点「恢复桌面」按钮或在桌面上点一下");
                    }
                }
                else if (msg.message == MsgEscOff)
                {
                    if (escOk) { Native.UnregisterHotKey(IntPtr.Zero, IdEscape); escOk = false; }
                }
                else if (msg.message == MsgQuit)
                {
                    break;
                }
            }
            if (escOk) Native.UnregisterHotKey(IntPtr.Zero, IdEscape);
        }

        /// <summary>跳舞期间占用 Esc；停下来就还给别的程序。</summary>
        public void SetEscape(bool on)
        {
            if (threadId != 0) Native.PostThreadMessage(threadId, on ? MsgEscOn : MsgEscOff, IntPtr.Zero, IntPtr.Zero);
        }

        public void Stop()
        {
            if (threadId != 0) Native.PostThreadMessage(threadId, MsgQuit, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
