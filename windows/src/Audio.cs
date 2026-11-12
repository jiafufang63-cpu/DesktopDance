using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DesktopDance
{
    /// <summary>简单的能量 / 鼓点检测。音频线程喂样本，画面线程每帧取结果。Mac 版 BeatAnalyzer 的移植。</summary>
    public sealed class BeatAnalyzer
    {
        public struct Onset { public double Time; public float Strength; }

        public sealed class Features
        {
            public List<Onset> Onsets = new List<Onset>();
            public float Level;
            public bool Active;
        }

        readonly object gate = new object();
        readonly List<Onset> pending = new List<Onset>();
        float levelOut;
        double lastLoud = -100;
        int dbgCalls, dbgOnsets;
        float dbgMaxFull;

        // 以下只在音频线程里用
        const int Hop = 512;
        float lp, accLow, accFull;
        int accCount;
        float prevLow, prevFull;
        readonly float[] fluxHist = new float[64];
        int fluxIdx;
        float peak = 1e-4f;
        float smoothed;
        double lastOnset = -100;
        readonly List<Onset> found = new List<Onset>();

        public string DebugSummary()
        {
            lock (gate) return "收到音频 " + dbgCalls + " 段，检出鼓点 " + dbgOnsets + " 个，最大音量 " + dbgMaxFull.ToString("0.0000");
        }

        public void Reset()
        {
            lock (gate) { pending.Clear(); levelOut = 0; lastLoud = -100; }
        }

        /// <summary>samples：单声道 float。startTime：这段声音响起的时刻（Clock.Now 的时钟）。</summary>
        public void Process(float[] samples, int count, double sampleRate, double startTime)
        {
            if (count <= 0 || sampleRate <= 0) return;
            float a = (float)Math.Exp(-2 * Math.PI * 160 / sampleRate);       // 160Hz 低通，抓底鼓
            found.Clear();
            double loudAt = -1;
            for (int i = 0; i < count; i++)
            {
                float x = samples[i];
                lp = a * lp + (1 - a) * x;
                accLow += lp * lp;
                accFull += x * x;
                accCount++;
                if (accCount >= Hop)
                {
                    float low = (float)Math.Sqrt(accLow / Hop);
                    float full = (float)Math.Sqrt(accFull / Hop);
                    accLow = 0; accFull = 0; accCount = 0;
                    double t = startTime + i / sampleRate;
                    float flux = Math.Max(0, low - prevLow) + 0.4f * Math.Max(0, full - prevFull);
                    prevLow = low; prevFull = full;
                    float mean = 0;
                    for (int k = 0; k < fluxHist.Length; k++) mean += fluxHist[k];
                    mean /= fluxHist.Length;
                    float varSum = 0;
                    for (int k = 0; k < fluxHist.Length; k++) varSum += (fluxHist[k] - mean) * (fluxHist[k] - mean);
                    float std = (float)Math.Sqrt(varSum / fluxHist.Length);
                    fluxHist[fluxIdx] = flux;
                    fluxIdx = (fluxIdx + 1) % fluxHist.Length;
                    if (flux > mean * 1.5f + std * 1.4f + 0.004f && t - lastOnset > 0.24)
                    {
                        lastOnset = t;
                        found.Add(new Onset { Time = t, Strength = Math.Min(1f, flux / (mean + std * 3 + 0.02f)) });
                    }
                    peak = Math.Max(full, peak * 0.9997f);
                    float lv = Math.Min(1f, full / (peak + 1e-5f));
                    smoothed += (lv - smoothed) * (lv > smoothed ? 0.5f : 0.08f);
                    if (full > 0.003f) loudAt = t;
                }
            }
            lock (gate)
            {
                dbgCalls++; dbgOnsets += found.Count; dbgMaxFull = Math.Max(dbgMaxFull, peak);
                pending.AddRange(found);
                if (pending.Count > 64) pending.RemoveRange(0, pending.Count - 64);
                levelOut = smoothed;
                if (loudAt > 0) lastLoud = loudAt;
            }
        }

        /// <summary>取出「到现在为止已经响过」的鼓点。</summary>
        public Features Drain(double now)
        {
            var f = new Features();
            lock (gate)
            {
                for (int i = 0; i < pending.Count;)
                {
                    if (pending[i].Time <= now) { f.Onsets.Add(pending[i]); pending.RemoveAt(i); }
                    else i++;
                }
                f.Level = levelOut;
                f.Active = now - lastLoud < 1.2;
            }
            return f;
        }
    }

    /// <summary>把鼓点变成连续的拍子相位。没有声音时就是一个 120 BPM 的节拍器。Mac 版 BeatClock 的移植。</summary>
    public sealed class BeatClock
    {
        public double Bpm { get; private set; } = 120;
        public double Beat { get; private set; }
        public double Energy { get; private set; } = 0.55;
        public double Pulse { get; private set; }
        public bool Locked { get; private set; }

        double lastOnset = -100;
        readonly List<double> iois = new List<double>();
        double strength = 0.6;

        public void Reset()
        {
            Beat = 0; Pulse = 0; Energy = 0.55; iois.Clear(); lastOnset = -100; Locked = false; strength = 0.6;
        }

        /// <summary>返回这一帧前进了多少拍。</summary>
        public double Update(double dt, double now, BeatAnalyzer.Features f, double speed)
        {
            Locked = f.Active;
            if (f.Active)
            {
                Energy += (f.Level - Energy) * Math.Min(1, dt * 8);
                foreach (var o in f.Onsets)
                {
                    // 用鼓点真实响起的时刻（音频是一批一批送来的，会晚一点）
                    strength += (o.Strength - strength) * 0.3;
                    if (iois.Count < 4) Pulse = Math.Max(Pulse, 0.55 + 0.45 * o.Strength);    // 还没摸清速度时，先跟着鼓点闪
                    double ioi = o.Time - lastOnset;
                    lastOnset = o.Time;
                    if (ioi > 0.2 && ioi < 2.0)
                    {
                        double x = ioi;
                        while (x < 0.4) x *= 2;             // 折到 75~150 BPM
                        while (x >= 0.8) x /= 2;
                        iois.Add(x);
                        if (iois.Count > 16) iois.RemoveAt(0);
                        if (iois.Count >= 4)
                        {
                            var sorted = new List<double>(iois);
                            sorted.Sort();
                            double med = sorted[sorted.Count / 2];
                            Bpm += (60 / med - Bpm) * 0.2;
                        }
                    }
                    if (Math.Abs(speed - 1) < 0.06)
                    {
                        // 速度为 1 时把相位往鼓点上靠：鼓点那一刻应该正好是整数拍
                        double atOnset = Beat - (now - o.Time) * Bpm / 60 * speed;
                        double err = atOnset - M.Round(atOnset);
                        if (Math.Abs(err) < 0.3) Beat -= err * 0.25;
                    }
                }
            }
            else
            {
                Energy += (0.55 - Energy) * Math.Min(1, dt * 2);
            }
            double prev = Beat;
            double d = dt * Bpm / 60 * speed;
            Beat += d;
            if (Math.Floor(Beat) > Math.Floor(prev))
                Pulse = Math.Max(Pulse, f.Active ? (iois.Count >= 4 ? 0.5 + 0.5 * strength : 0.3) : 0.9);
            Pulse *= Math.Exp(-dt * 7);
            return d;
        }
    }

    /// <summary>
    /// 听电脑正在播放的声音（网易云、抖音、浏览器…），只用来检测节拍。
    /// 用 Windows 自带的 WASAPI「回环采集」：读的是送往扬声器的那一路，不是麦克风，
    /// 系统不会弹权限框；不录音、不保存。
    /// 换了输出设备（插拔耳机）时会自动重新接上。
    /// </summary>
    public sealed class SystemAudioCapture
    {
        readonly BeatAnalyzer analyzer;
        readonly object gate = new object();
        WasapiLoopbackCapture? capture;
        MMDeviceEnumerator? enumerator;
        Timer? watch;
        string deviceId = "";
        string deviceName = "";
        int channels = 2, bits = 32;
        double sampleRate = 48000;
        float[] mono = new float[8192];
        double lastSound = -100;
        volatile bool wanted;
        volatile bool dead;                // 采集自己停了（设备被拔掉之类），等看门狗重新接
        bool warnedFormat;

        public SystemAudioCapture(BeatAnalyzer analyzer) { this.analyzer = analyzer; }

        public bool Running => wanted && capture != null;
        public string DeviceName => deviceName;

        /// <summary>最近 4 秒内有没有听到声音。</summary>
        public bool Hearing => Clock.Now - Volatile.Read(ref lastSound) < 4;

        /// <summary>开始听。返回 null 表示成功，否则是出错原因（中英文各一份）。请在后台线程调用。</summary>
        public Msg? Start()
        {
            lock (gate)
            {
                wanted = true;
                Msg? err = Open();
                if (watch == null) watch = new Timer(Watch, null, 3000, 3000);
                return err;
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                wanted = false;
                if (watch != null) { watch.Dispose(); watch = null; }
                Close();
            }
        }

        Msg? Open()
        {
            Close();
            try
            {
                enumerator ??= new MMDeviceEnumerator();
                MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var cap = new WasapiLoopbackCapture(device);
                WaveFormat fmt = cap.WaveFormat;
                channels = Math.Max(1, fmt.Channels);
                bits = fmt.BitsPerSample;
                sampleRate = fmt.SampleRate;
                deviceId = device.ID ?? "";
                try { deviceName = device.FriendlyName ?? ""; } catch { deviceName = ""; }
                if (bits != 32 && bits != 16)
                {
                    cap.Dispose();
                    return new Msg("这个输出设备的声音格式不认识（" + fmt + "）", "this output device uses an audio format the app does not understand (" + fmt + ")");
                }
                cap.DataAvailable += OnData;
                cap.RecordingStopped += OnStopped;
                dead = false;
                cap.StartRecording();
                capture = cap;
                Log.Write("系统声音已接通：" + deviceName + "，格式 " + fmt);
                return null;
            }
            catch (Exception ex)
            {
                Log.Write("系统声音接不上：" + ex.GetType().Name + " " + ex.Message);
                return new Msg(ex.Message, ex.Message);       // 系统给的出错文字，原样带上
            }
        }

        void Close()
        {
            var cap = capture;
            capture = null;
            if (cap == null) return;
            try { cap.DataAvailable -= OnData; cap.RecordingStopped -= OnStopped; } catch { }
            try { cap.StopRecording(); } catch { }
            try { cap.Dispose(); } catch { }
        }

        /// <summary>看门狗：采集自己停了、或者默认输出设备换了，就重新接。</summary>
        void Watch(object? state)
        {
            if (!wanted) return;
            if (!Monitor.TryEnter(gate)) return;
            try
            {
                if (!wanted) return;
                bool reopen = dead || capture == null;
                if (!reopen)
                {
                    try
                    {
                        enumerator ??= new MMDeviceEnumerator();
                        string id = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID ?? "";
                        if (id.Length > 0 && id != deviceId) { reopen = true; Log.Write("默认输出设备换了，重新接系统声音"); }
                    }
                    catch { }
                }
                if (reopen) Open();
            }
            catch (Exception ex) { Log.Write("系统声音看门狗出错：" + ex.Message); }
            finally { Monitor.Exit(gate); }
        }

        void OnStopped(object? sender, StoppedEventArgs e)
        {
            // 这里可能就在采集线程上，不能在这儿 Dispose（会自己等自己）。只做标记，交给看门狗。
            if (e.Exception != null) Log.Write("系统声音中断：" + e.Exception.Message);
            dead = true;
        }

        void OnData(object? sender, WaveInEventArgs e)
        {
            try
            {
                int bytesPerFrame = (bits / 8) * channels;
                if (bytesPerFrame <= 0) return;
                int frames = e.BytesRecorded / bytesPerFrame;
                if (frames <= 0) return;
                if (mono.Length < frames) mono = new float[frames * 2];
                float peak = 0;
                float inv = 1f / channels;
                if (bits == 32)
                {
                    ReadOnlySpan<float> src = MemoryMarshal.Cast<byte, float>(new ReadOnlySpan<byte>(e.Buffer, 0, frames * bytesPerFrame));
                    for (int j = 0; j < frames; j++)
                    {
                        float v = 0;
                        int o = j * channels;
                        for (int c = 0; c < channels; c++) v += src[o + c];
                        v *= inv;
                        if (float.IsNaN(v) || float.IsInfinity(v)) v = 0;
                        mono[j] = v;
                        float av = Math.Abs(v);
                        if (av > peak) peak = av;
                    }
                }
                else
                {
                    ReadOnlySpan<short> src = MemoryMarshal.Cast<byte, short>(new ReadOnlySpan<byte>(e.Buffer, 0, frames * bytesPerFrame));
                    for (int j = 0; j < frames; j++)
                    {
                        float v = 0;
                        int o = j * channels;
                        for (int c = 0; c < channels; c++) v += src[o + c] / 32768f;
                        v *= inv;
                        mono[j] = v;
                        float av = Math.Abs(v);
                        if (av > peak) peak = av;
                    }
                }
                double now = Clock.Now;
                if (peak > 0.0005f) Volatile.Write(ref lastSound, now);
                analyzer.Process(mono, frames, sampleRate, now - frames / sampleRate);
            }
            catch (Exception ex)
            {
                if (!warnedFormat) { warnedFormat = true; Log.Write("处理系统声音时出错：" + ex.Message); }
            }
        }
    }
}
