using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace DesktopDance
{
    public enum Joint
    {
        Nose, Neck, LSho, RSho, LElb, RElb, LWri, RWri, Root, LHip, RHip, LKne, RKne, LAnk, RAnk, LEar, REar
    }

    /// <summary>
    /// 处理好的动作片段：按镜头分段补洞、平滑，换算成「以身高为单位、脚下为原点」的坐标，供小人回放。
    /// 数据文件 motion/basketball-motion.json 和 Mac 版共用：每帧 17 个关节的 [x, y, 置信度]，
    /// 坐标是画面归一化坐标（0...1，原点左下，y 向上）。这里是 Mac 版 MotionClip 的逐行移植。
    /// </summary>
    public sealed class MotionClip
    {
        public const int JointCount = 17;
        static readonly string[] JointNames =
        {
            "nose", "neck", "lsho", "rsho", "lelb", "relb", "lwri", "rwri", "root",
            "lhip", "rhip", "lkne", "rkne", "lank", "rank", "lear", "rear",
        };

        public struct Sample
        {
            public Vec[] Joints;
            public Vec Ball;
            public double BallAlpha;
        }

        public double Duration { get; }
        public int FrameCount => count;
        public double MaxAbsX { get; private set; } = 0.5;
        public double MaxY { get; private set; } = 1.0;
        public double MaxRootX { get; private set; }            // 整个人左右走位的最大幅度
        public double MaxLocalX { get; private set; } = 0.3;    // 肢体（和球）离身体中线最远多少

        readonly double dt;
        readonly int count;
        readonly Vec[][] tracks;          // [关节][帧]
        readonly Vec[] ballTrack;
        readonly double[] ballVisible;    // 0...1
        readonly int[] cutFrames;         // 每个镜头的第一帧（不含第 0 帧）

        /// <summary>先找 %LocalAppData%\DesktopDance\basketball-motion.json（想换动作的人放这里），没有就用 exe 里自带的。</summary>
        public static MotionClip? LoadDefault()
        {
            try
            {
                if (File.Exists(AppPaths.MotionOverride))
                {
                    using var fs = File.OpenRead(AppPaths.MotionOverride);
                    var c = TryParse(fs, "外部文件 " + AppPaths.MotionOverride);
                    if (c != null) return c;
                }
            }
            catch (Exception ex) { Log.Write("读外部动作数据失败：" + ex.Message); }
            return LoadEmbedded();
        }

        public static MotionClip? LoadEmbedded()
        {
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("basketball-motion.json");
                if (s == null) { Log.Write("exe 里没有找到动作数据"); return null; }
                return TryParse(s, "内置动作数据");
            }
            catch (Exception ex) { Log.Write("读内置动作数据失败：" + ex.Message); return null; }
        }

        public static MotionClip? TryParse(Stream stream, string what)
        {
            try
            {
                using var doc = JsonDocument.Parse(stream);
                return new MotionClip(doc.RootElement);
            }
            catch (Exception ex)
            {
                Log.Write(what + " 用不了：" + ex.Message);
                return null;
            }
        }

        MotionClip(JsonElement root)
        {
            var framesEl = root.GetProperty("frames");
            int n = framesEl.GetArrayLength();
            if (n < 8) throw new InvalidDataException("帧数太少");
            double aspect = root.GetProperty("aspect").GetDouble();

            var ts = new double[n];
            var J = new Vec?[JointCount][];
            for (int k = 0; k < JointCount; k++) J[k] = new Vec?[n];
            var ballRaw = new Vec?[n];
            int fi = 0;
            foreach (var f in framesEl.EnumerateArray())
            {
                ts[fi] = f.GetProperty("t").GetDouble();
                if (f.TryGetProperty("j", out var jo) && jo.ValueKind == JsonValueKind.Object)
                {
                    for (int k = 0; k < JointCount; k++)
                    {
                        if (jo.TryGetProperty(JointNames[k], out var v) && v.ValueKind == JsonValueKind.Array &&
                            v.GetArrayLength() >= 3 && v[2].GetDouble() > 0.25)
                        {
                            J[k][fi] = new Vec(v[0].GetDouble() * aspect, v[1].GetDouble());
                        }
                    }
                }
                if (f.TryGetProperty("ball", out var b) && b.ValueKind == JsonValueKind.Array &&
                    b.GetArrayLength() >= 4 && b[3].GetDouble() > 0.3)
                {
                    ballRaw[fi] = new Vec(b[0].GetDouble() * aspect, b[1].GetDouble());
                }
                fi++;
            }

            count = n;
            double total = ts[n - 1] - ts[0];
            if (total <= 0) throw new InvalidDataException("时间戳不对");
            dt = total / (n - 1);
            Duration = total + dt;

            // 镜头分段
            var starts = new List<int> { 0 };
            if (root.TryGetProperty("cuts", out var cutsEl) && cutsEl.ValueKind == JsonValueKind.Array)
            {
                var cuts = new List<double>();
                foreach (var c in cutsEl.EnumerateArray()) cuts.Add(c.GetDouble());
                cuts.Sort();
                foreach (double c in cuts)
                {
                    int i = Array.FindIndex(ts, t => t >= c - 1e-6);
                    if (i >= 0 && i - starts[starts.Count - 1] >= 5 && n - i >= 5) starts.Add(i);
                }
            }
            cutFrames = starts.Skip(1).ToArray();
            var segs = new List<(int a, int b)>();           // 每段 [a, b)
            for (int s = 0; s < starts.Count; s++) segs.Add((starts[s], s + 1 < starts.Count ? starts[s + 1] : n));

            // 在一个镜头内，把置信度低的帧用前后好帧线性补上
            for (int k = 0; k < JointCount; k++)
                foreach (var (a, b) in segs) Fill(J[k], a, b, int.MaxValue);

            Vec?[] neckO = J[(int)Joint.Neck], rootO = J[(int)Joint.Root];
            if (neckO.Any(p => !p.HasValue) || rootO.Any(p => !p.HasValue)) throw new InvalidDataException("脖子或胯的数据不全");
            Vec[] neck = neckO.Select(p => p!.Value).ToArray();
            Vec[] rootT = rootO.Select(p => p!.Value).ToArray();

            // 人离镜头的远近会变。逐帧用骨骼长度估计「他在画面里有多高」：
            // 骨头正对镜头时会显得短、侧过来才是真实长度，所以取一小段时间里偏大的值，再平滑。
            var boneRefs = new (Joint a, Joint b, double len)[]
            {
                (Joint.Neck, Joint.Root, 0.29), (Joint.LHip, Joint.LKne, 0.245), (Joint.RHip, Joint.RKne, 0.245),
                (Joint.LKne, Joint.LAnk, 0.246), (Joint.RKne, Joint.RAnk, 0.246),
                (Joint.LSho, Joint.LElb, 0.172), (Joint.RSho, Joint.RElb, 0.172),
                (Joint.LElb, Joint.LWri, 0.157), (Joint.RElb, Joint.RWri, 0.157), (Joint.LSho, Joint.RSho, 0.20),
            };
            var inst = new double[n];
            var ratios = new List<double>();
            for (int i = 0; i < n; i++)
            {
                ratios.Clear();
                foreach (var (ja, jb, len) in boneRefs)
                {
                    Vec? p = J[(int)ja][i], q = J[(int)jb][i];
                    if (p.HasValue && q.HasValue) ratios.Add(M.Hypot(p.Value.X - q.Value.X, p.Value.Y - q.Value.Y) / len);
                }
                ratios.Sort((x, y) => y.CompareTo(x));       // 从大到小
                inst[i] = ratios.Count >= 3 ? ratios[2] : (ratios.Count > 0 ? ratios[ratios.Count - 1] : 0.5);   // 取第三大的，避开个别识别错的骨头
            }
            int win = Math.Max(1, (int)M.Round(1.0 / dt));
            var unit = new double[n];
            for (int i = 0; i < n; i++)
            {
                int lo = Math.Max(0, i - win), hi = Math.Min(n - 1, i + win);
                var w = new double[hi - lo + 1];
                Array.Copy(inst, lo, w, 0, w.Length);
                Array.Sort(w);
                unit[i] = Math.Max(w[Math.Min(w.Length - 1, w.Length * 8 / 10)], 0.02);
            }
            unit = BoxSmooth(unit, win / 2);

            // 缺失的关节用相邻关节推出来
            Vec[] Or(Joint j, Func<int, Vec> fb)
            {
                var src = J[(int)j];
                var o = new Vec[n];
                for (int i = 0; i < n; i++) o[i] = src[i] ?? fb(i);
                return o;
            }
            Vec[] lsho = Or(Joint.LSho, i => new Vec(neck[i].X + unit[i] * 0.09, neck[i].Y));
            Vec[] rsho = Or(Joint.RSho, i => new Vec(neck[i].X - unit[i] * 0.09, neck[i].Y));
            Vec[] lelb = Or(Joint.LElb, i => new Vec(lsho[i].X, lsho[i].Y - unit[i] * 0.15));
            Vec[] relb = Or(Joint.RElb, i => new Vec(rsho[i].X, rsho[i].Y - unit[i] * 0.15));
            Vec[] lwri = Or(Joint.LWri, i => new Vec(lelb[i].X, lelb[i].Y - unit[i] * 0.15));
            Vec[] rwri = Or(Joint.RWri, i => new Vec(relb[i].X, relb[i].Y - unit[i] * 0.15));
            Vec[] lhip = Or(Joint.LHip, i => new Vec(rootT[i].X + unit[i] * 0.05, rootT[i].Y));
            Vec[] rhip = Or(Joint.RHip, i => new Vec(rootT[i].X - unit[i] * 0.05, rootT[i].Y));
            Vec[] lkne = Or(Joint.LKne, i => new Vec(lhip[i].X, lhip[i].Y - unit[i] * 0.21));
            Vec[] rkne = Or(Joint.RKne, i => new Vec(rhip[i].X, rhip[i].Y - unit[i] * 0.21));
            Vec[] lank = Or(Joint.LAnk, i => new Vec(lkne[i].X, lkne[i].Y - unit[i] * 0.21));
            Vec[] rank = Or(Joint.RAnk, i => new Vec(rkne[i].X, rkne[i].Y - unit[i] * 0.21));
            Vec[] nose = Or(Joint.Nose, i => new Vec(neck[i].X, neck[i].Y + unit[i] * 0.11));
            Vec[] lear = Or(Joint.LEar, i => nose[i]);
            Vec[] rear = Or(Joint.REar, i => nose[i]);

            // 顺序必须和 Joint 枚举一致
            var all = new Vec[JointCount][];
            all[(int)Joint.Nose] = nose; all[(int)Joint.Neck] = neck; all[(int)Joint.LSho] = lsho; all[(int)Joint.RSho] = rsho;
            all[(int)Joint.LElb] = lelb; all[(int)Joint.RElb] = relb; all[(int)Joint.LWri] = lwri; all[(int)Joint.RWri] = rwri;
            all[(int)Joint.Root] = rootT; all[(int)Joint.LHip] = lhip; all[(int)Joint.RHip] = rhip; all[(int)Joint.LKne] = lkne;
            all[(int)Joint.RKne] = rkne; all[(int)Joint.LAnk] = lank; all[(int)Joint.RAnk] = rank; all[(int)Joint.LEar] = lear;
            all[(int)Joint.REar] = rear;

            // 篮球：短暂丢失（≤ 8 帧）插值补上；长时间没有就算「球不在了」
            foreach (var (a, b) in segs) Fill(ballRaw, a, b, 8);
            var vis = new double[n];
            var ball = new Vec[n];
            for (int i = 0; i < n; i++)
            {
                vis[i] = ballRaw[i].HasValue ? 1 : 0;
                ball[i] = ballRaw[i] ?? new Vec(rwri[i].X, rwri[i].Y - unit[i] * 0.07);
            }
            // 球不在时的占位：沿用最后一次看到的位置，淡出时不会瞬移
            Vec? lastSeen = null;
            for (int i = 0; i < n; i++)
            {
                if (ballRaw[i].HasValue) lastSeen = ball[i];
                else if (lastSeen.HasValue) ball[i] = lastSeen.Value;
            }
            // 可见性做 4 帧的淡入淡出
            var visS = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = 0;
                for (int k = -2; k <= 2; k++) s += vis[Math.Min(Math.Max(i + k, 0), n - 1)];
                visS[i] = s / 5;
            }
            vis = visS;

            // 平滑（手腕脚踝动得快，少平滑一点，保住动作的脆劲）
            for (int k = 0; k < JointCount; k++)
            {
                var jn = (Joint)k;
                bool quick = jn == Joint.LWri || jn == Joint.RWri || jn == Joint.LAnk || jn == Joint.RAnk;
                foreach (var (a, b) in segs) all[k] = Smoothed(all[k], a, b, quick ? 1 : 2);
            }
            foreach (var (a, b) in segs) ball = Smoothed(ball, a, b, 1);

            // 地面：一小段时间里脚踝的最低处（跳起来时地面不跟着上去），再平滑
            var ankleMin = new double[n];
            for (int i = 0; i < n; i++) ankleMin[i] = Math.Min(all[(int)Joint.LAnk][i].Y, all[(int)Joint.RAnk][i].Y);
            var ground = new double[n];
            for (int i = 0; i < n; i++)
            {
                double m = double.MaxValue;
                for (int k = Math.Max(0, i - win); k <= Math.Min(n - 1, i + win); k++) m = Math.Min(m, ankleMin[k]);
                ground[i] = m;
            }
            ground = BoxSmooth(ground, win / 2);
            // 注意：这里用的是平滑前的胯（和 Mac 版一致）
            double centerX = neckO.Length > 0 ? rootO.Select(p => p!.Value.X).OrderBy(x => x).ElementAt(n / 2) : 0;

            for (int i = 0; i < n; i++)
            {
                double u = unit[i], gy = ground[i] - u * 0.03;
                for (int k = 0; k < JointCount; k++)
                    all[k][i] = new Vec((all[k][i].X - centerX) / u, (all[k][i].Y - gy) / u);
                ball[i] = new Vec((ball[i].X - centerX) / u, (ball[i].Y - gy) / u);
            }

            // 校准：让「站直时的头顶」正好是 1 个身高
            var headY = new double[n];
            for (int i = 0; i < n; i++)
                headY[i] = (all[(int)Joint.Nose][i].Y + all[(int)Joint.LEar][i].Y + all[(int)Joint.REar][i].Y) / 3;
            double standing = Pct(headY, 0.9) + 0.085;
            if (standing > 0.3)
            {
                double c = 1 / standing;
                for (int k = 0; k < JointCount; k++)
                    for (int i = 0; i < n; i++) all[k][i] = all[k][i] * c;
                for (int i = 0; i < n; i++) ball[i] = ball[i] * c;
            }
            tracks = all;
            ballTrack = ball;
            ballVisible = vis;

            // 舞台缩放用的幅度。最高点取真实最大值（跳起来不能出框）；横向和球取常态幅度。
            var rootX = tracks[(int)Joint.Root];
            var topY = new double[n];
            var localX = new double[n];
            var absX = new double[n];
            double rx = 0;
            for (int i = 0; i < n; i++)
            {
                double ty = 0, lx = 0, ax = 0;
                for (int k = 0; k < JointCount; k++)
                {
                    var p = tracks[k][i];
                    ty = Math.Max(ty, p.Y);
                    lx = Math.Max(lx, Math.Abs(p.X - rootX[i].X));
                    ax = Math.Max(ax, Math.Abs(p.X));
                }
                if (vis[i] > 0.5)
                {
                    lx = Math.Max(lx, Math.Abs(ball[i].X - rootX[i].X));
                    ax = Math.Max(ax, Math.Abs(ball[i].X));
                }
                topY[i] = ty; localX[i] = lx; absX[i] = ax;
                rx = Math.Max(rx, Math.Abs(rootX[i].X));
            }
            MaxY = Math.Max(topY.Max(), 1.0) + 0.10;
            MaxAbsX = Pct(absX, 0.97) + 0.08;
            MaxLocalX = Pct(localX, 0.97) + 0.08;
            MaxRootX = rx;
        }

        /// <summary>把 [a, b) 里缺的点用前后好点线性补上。maxGap = int.MaxValue 时，头尾缺的也用最近的好点顶上。</summary>
        static void Fill(Vec?[] pts, int a, int b, int maxGap)
        {
            var good = new List<int>();
            for (int i = a; i < b; i++) if (pts[i].HasValue) good.Add(i);
            if (good.Count == 0) return;
            var src = (Vec?[])pts.Clone();                   // 查找只看原始数据，不看刚补上的
            for (int i = a; i < b; i++)
            {
                if (src[i].HasValue) continue;
                int before = -1, after = -1;
                foreach (int g in good)
                {
                    if (g < i) before = g;
                    else if (g > i) { after = g; break; }
                }
                if (before >= 0 && after >= 0)
                {
                    if (after - before - 1 <= maxGap)
                        pts[i] = M.Lerp(src[before]!.Value, src[after]!.Value, (double)(i - before) / (after - before));
                }
                else if (maxGap == int.MaxValue)
                {
                    pts[i] = src[before >= 0 ? before : after];
                }
            }
        }

        static Vec[] Smoothed(Vec[] t, int a, int b, int radius)
        {
            var o = (Vec[])t.Clone();
            if (radius <= 0) return o;
            for (int i = a; i < b; i++)
            {
                double sx = 0, sy = 0, wsum = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int j = Math.Min(Math.Max(i + k, a), b - 1);
                    double w = radius + 1 - Math.Abs(k);
                    sx += t[j].X * w; sy += t[j].Y * w; wsum += w;
                }
                o[i] = new Vec(sx / wsum, sy / wsum);
            }
            return o;
        }

        static double[] BoxSmooth(double[] v, int r)
        {
            var o = new double[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                int lo = Math.Max(0, i - r), hi = Math.Min(v.Length - 1, i + r);
                double s = 0;
                for (int k = lo; k <= hi; k++) s += v[k];
                o[i] = s / (hi - lo + 1);
            }
            return o;
        }

        static double Pct(double[] v, double p)
        {
            var s = (double[])v.Clone();
            Array.Sort(s);
            return s[Math.Min(s.Length - 1, (int)(s.Length * p))];
        }

        Sample Frame(double f)
        {
            double ff = Math.Min(Math.Max(f, 0), count - 1);
            int i = (int)ff;
            int j = Math.Min(i + 1, count - 1);
            if (Array.IndexOf(cutFrames, j) >= 0) j = i;     // 不跨镜头插值
            double k = ff - i;
            var joints = new Vec[JointCount];
            for (int q = 0; q < JointCount; q++) joints[q] = M.Lerp(tracks[q][i], tracks[q][j], k);
            return new Sample
            {
                Joints = joints,
                Ball = M.Lerp(ballTrack[i], ballTrack[j], k),
                BallAlpha = M.Lerp(ballVisible[i], ballVisible[j], k),
            };
        }

        static Sample Blend(Sample a, Sample b, double w)
        {
            // 球：哪一边看不见，就直接用看得见那一边的位置（否则球会从被抛走的地方飘回来）
            Vec ball = a.BallAlpha < 0.05 ? b.Ball : (b.BallAlpha < 0.05 ? a.Ball : M.Lerp(a.Ball, b.Ball, w));
            var joints = new Vec[JointCount];
            for (int q = 0; q < JointCount; q++) joints[q] = M.Lerp(a.Joints[q], b.Joints[q], w);
            return new Sample { Joints = joints, Ball = ball, BallAlpha = M.Lerp(a.BallAlpha, b.BallAlpha, w) };
        }

        /// <summary>取 t 秒时的姿态。循环播放；镜头切换处和首尾相接处用 0.2 秒过渡。</summary>
        public Sample SampleAt(double time)
        {
            double t = time % Duration;
            if (t < 0) t += Duration;
            double f = t / dt;
            Sample cur = Frame(f);
            double blendFrames = 0.2 / dt;
            int cut = -1;
            foreach (int c in cutFrames) if (c <= f) cut = c;
            if (cut >= 0 && f - cut < blendFrames)
                return Blend(Frame(cut - 1), cur, M.Smooth((f - cut) / blendFrames));
            if (t > Duration - 0.2)
                return Blend(cur, Frame(0), M.Smooth((t - (Duration - 0.2)) / 0.2));
            return cur;
        }
    }
}
