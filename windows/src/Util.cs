using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DesktopDance
{
    /// <summary>二维点 / 向量。舞台坐标：原点在左下角，y 向上（和 Mac 版一致，画的时候再翻转）。</summary>
    public readonly struct Vec
    {
        public readonly double X, Y;
        public Vec(double x, double y) { X = x; Y = y; }
        public static readonly Vec Zero = new Vec(0, 0);
        public static Vec operator +(Vec a, Vec b) => new Vec(a.X + b.X, a.Y + b.Y);
        public static Vec operator -(Vec a, Vec b) => new Vec(a.X - b.X, a.Y - b.Y);
        public static Vec operator *(Vec a, double k) => new Vec(a.X * k, a.Y * k);
    }

    public struct RectD
    {
        public double X, Y, W, H;
        public RectD(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
        public double MinX => X;
        public double MaxX => X + W;
        public double MidX => X + W / 2;
        public double MinY => Y;
        public double MaxY => Y + H;
        public double MidY => Y + H / 2;
        public RectD Inset(double dx, double dy) => new RectD(X + dx, Y + dy, W - 2 * dx, H - 2 * dy);
    }

    public static class M
    {
        public static double Lerp(double a, double b, double t) => a + (b - a) * t;
        public static Vec Lerp(Vec a, Vec b, double t) => new Vec(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        public static double Clamp(double v, double lo, double hi) => Math.Min(Math.Max(v, lo), hi);
        public static double Smooth(double t) { double x = Clamp(t, 0, 1); return x * x * (3 - 2 * x); }
        public static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);
        public static double Round(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

        /// <summary>稳定的伪随机数（0..1），同样的输入永远得到同样的结果。</summary>
        public static double Hash01(int a, int b)
        {
            unchecked
            {
                ulong x = (ulong)(long)a * 0x9E3779B97F4A7C15UL + (ulong)(long)b * 0xBF58476D1CE4E5B9UL;
                x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
                x ^= x >> 27; x *= 0x94D049BB133111EBUL;
                x ^= x >> 31;
                return (double)(x >> 11) / (double)(1UL << 53);
            }
        }
    }

    /// <summary>一个图标在某一帧的姿态。</summary>
    public struct Pose
    {
        public Vec P;
        public double Rot, Sx, Sy, Alpha, Z;

        public Pose(Vec p, double rot = 0, double sx = 1, double sy = 1, double alpha = 1, double z = 0)
        {
            P = p; Rot = rot; Sx = sx; Sy = sy; Alpha = alpha; Z = z;
        }

        public static Pose Mix(Pose a, Pose b, double t) =>
            new Pose(M.Lerp(a.P, b.P, t), M.Lerp(a.Rot, b.Rot, t), M.Lerp(a.Sx, b.Sx, t),
                     M.Lerp(a.Sy, b.Sy, t), M.Lerp(a.Alpha, b.Alpha, t), t < 0.5 ? a.Z : b.Z);
    }

    /// <summary>全程序共用的时钟（秒）。音频线程和画面线程用同一个，鼓点时刻才对得上。</summary>
    public static class Clock
    {
        static readonly Stopwatch sw = Stopwatch.StartNew();
        public static double Now => sw.Elapsed.TotalSeconds;
    }

    /// <summary>界面语言：中文或英文。第一次按系统语言定，之后记住用户在控制窗口里选的。</summary>
    public enum Lang { Zh, En }

    /// <summary>界面文字二选一。所有给用户看的字都走 L.T(中文, 英文)；日志和自检报告一律中文，不走这里。</summary>
    public static class L
    {
        /// <summary>默认中文；正常启动时由 Program 按设置 / 系统语言定（自检不改它，所以自检报告始终是中文）。</summary>
        public static volatile Lang Current = Lang.Zh;

        public static string T(string zh, string en) => Current == Lang.Zh ? zh : en;

        /// <summary>系统显示语言是中文就用中文，否则英文。</summary>
        public static Lang SystemDefault()
        {
            try
            {
                return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? Lang.Zh : Lang.En;
            }
            catch { return Lang.En; }
        }

        /// <summary>"zh" / "en" → 语言；别的（包括空）→ null。</summary>
        public static Lang? Parse(string? code)
        {
            if (string.Equals(code, "zh", StringComparison.OrdinalIgnoreCase)) return Lang.Zh;
            if (string.Equals(code, "en", StringComparison.OrdinalIgnoreCase)) return Lang.En;
            return null;
        }

        public static string Code(Lang l) => l == Lang.Zh ? "zh" : "en";
    }

    /// <summary>
    /// 一句要显示给用户的话，中英文各一份。存下来的是两份，显示时才按当前语言取，
    /// 所以跳舞中切换语言，已经在显示的提示也会跟着换。写日志用 Zh。
    /// </summary>
    public readonly struct Msg
    {
        public readonly string Zh, En;
        public Msg(string zh, string en) { Zh = zh ?? ""; En = en ?? ""; }
        public static readonly Msg None = new Msg("", "");
        public bool IsEmpty => string.IsNullOrEmpty(Zh);
        public string Text => L.T(Zh ?? "", En ?? "");
    }

    public static class AppPaths
    {
        static string? dir;

        /// <summary>%LocalAppData%\DesktopDance —— 只放日志和设置，不放别的。</summary>
        public static string Dir
        {
            get
            {
                if (dir == null)
                {
                    string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopDance");
                    try { Directory.CreateDirectory(d); } catch { }
                    dir = d;
                }
                return dir;
            }
        }

        public static string LogFile => Path.Combine(Dir, "desktop-dance.log");
        public static string SettingsFile => Path.Combine(Dir, "settings.json");
        public static string MotionOverride => Path.Combine(Dir, "basketball-motion.json");
    }

    public static class Log
    {
        static readonly object gate = new object();

        public static void StartSession(string header)
        {
            try
            {
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    string old = AppPaths.LogFile + ".old";
                    File.Copy(AppPaths.LogFile, old, true);
                    File.WriteAllText(AppPaths.LogFile, "", Encoding.UTF8);
                }
            }
            catch { }
            Write("======== " + header + " ========");
        }

        public static void Write(string s)
        {
            string line = "[" + DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + "] " + s + Environment.NewLine;
            Debug.Write(line);
            try { lock (gate) File.AppendAllText(AppPaths.LogFile, line, Encoding.UTF8); } catch { }
        }
    }

    /// <summary>记住上次的几个选项。</summary>
    public sealed class Settings
    {
        public string BallName = "";              // 空 = 第一个图标
        public bool FollowSystemAudio = true;
        public bool HidePanelFromCapture = false;
        public string Language = "";              // "zh" / "en"；空 = 还没选过，跟系统语言
        public bool NoSave;                       // 实跑测试时设为 true：测试期间不往设置文件里写任何东西

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(AppPaths.SettingsFile)) return s;
                using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) return s;
                if (r.TryGetProperty("ballName", out var b) && b.ValueKind == JsonValueKind.String) s.BallName = b.GetString() ?? "";
                if (r.TryGetProperty("followSystemAudio", out var f) && (f.ValueKind == JsonValueKind.True || f.ValueKind == JsonValueKind.False)) s.FollowSystemAudio = f.GetBoolean();
                if (r.TryGetProperty("hidePanelFromCapture", out var h) && (h.ValueKind == JsonValueKind.True || h.ValueKind == JsonValueKind.False)) s.HidePanelFromCapture = h.GetBoolean();
                if (r.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String && L.Parse(l.GetString()) is Lang lang) s.Language = L.Code(lang);
            }
            catch (Exception ex) { Log.Write("读设置失败：" + ex.Message); }
            return s;
        }

        public void Save()
        {
            if (NoSave) return;
            try
            {
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WriteString("ballName", BallName);
                    w.WriteBoolean("followSystemAudio", FollowSystemAudio);
                    w.WriteBoolean("hidePanelFromCapture", HidePanelFromCapture);
                    if (Language.Length > 0) w.WriteString("language", Language);      // 没选过就不写，继续跟系统语言
                    w.WriteEndObject();
                }
                File.WriteAllBytes(AppPaths.SettingsFile, ms.ToArray());
            }
            catch (Exception ex) { Log.Write("写设置失败：" + ex.Message); }
        }
    }
}
