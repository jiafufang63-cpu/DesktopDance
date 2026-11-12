using System;
using System.Collections.Generic;

namespace DesktopDance
{
    public enum DanceMode { Snake = 0, Figure = 1, Groove = 2, Free = 3 }

    public static class DanceModes
    {
        public static readonly DanceMode[] All = { DanceMode.Snake, DanceMode.Figure, DanceMode.Groove, DanceMode.Free };

        /// <summary>给用户看的模式名，跟着界面语言走（英文和 Mac 版一致）。</summary>
        public static string Title(DanceMode m)
        {
            switch (m)
            {
                case DanceMode.Snake: return L.T("列队", "Line");
                case DanceMode.Figure: return L.T("打篮球", "Basketball");
                case DanceMode.Groove: return L.T("律动", "Groove");
                default: return L.T("自由", "Free");
            }
        }

        /// <summary>写日志和自检报告用的模式名：始终中文，不随界面语言变。</summary>
        public static string LogName(DanceMode m)
        {
            switch (m)
            {
                case DanceMode.Snake: return "列队";
                case DanceMode.Figure: return "打篮球";
                case DanceMode.Groove: return "律动";
                default: return "自由";
            }
        }

        /// <summary>自动切换时每个模式停留多少拍。</summary>
        public static double AutoBeats(DanceMode m)
        {
            switch (m)
            {
                case DanceMode.Snake: return 44;
                case DanceMode.Figure: return 32;
                default: return 16;
            }
        }
    }

    /// <summary>每一帧传给编舞的节拍信息。</summary>
    public struct Tempo
    {
        public double Beat;     // 连续拍数（整数处是重拍）
        public double DBeat;    // 这一帧前进了多少拍
        public double Dt;
        public double Energy;   // 0...1 音乐强度
        public double Pulse;    // 鼓点脉冲，击中时为 1，随后衰减
    }

    /// <summary>
    /// 编舞：给定节拍，算出每个图标该在哪、转多少、压多扁。Mac 版 Choreo.swift 的逐行移植。
    /// 坐标：舞台窗口内，原点左下，y 向上，单位是「与缩放无关的点」（和 Mac 的 point 一样）。
    /// 想改队形就改这个文件——每个模式是一个独立的函数。
    /// </summary>
    public sealed class Choreographer
    {
        public RectD Stage;                         // 可以跳舞的区域
        public double IconSize = 48;
        public double Amp = 1;                      // 幅度滑块
        public Vec[] Homes = Array.Empty<Vec>();    // 每个图标的原位
        public bool GrooveUsesHomes = true;         // 律动模式：全屏时在原位跳，竖屏时排进舞台
        public int BallIndex;                       // 打篮球模式里哪个图标当篮球
        public double GridW = 76, GridH = 100;      // 桌面网格一格多大（律动模式用它分奇偶格）
        public DanceMode Mode { get; private set; } = DanceMode.Snake;

        /// <summary>动作数据。有它就照着它跳；没有就用手写动作。</summary>
        public MotionClip? Clip
        {
            get => clipData;
            set { clipData = value; clipMeans = null; fitCache.Clear(); }
        }
        public double ClipRate = 1;
        MotionClip? clipData;

        /// <summary>整段动作的「平均姿势」：胯的平均位置、每个关节和球相对胯的平均偏移。幅度放大时以它为基准。</summary>
        sealed class ClipMeans
        {
            public Vec Root, Ball;
            public Vec[] Offs = Array.Empty<Vec>();
        }
        ClipMeans? clipMeans;
        readonly Dictionary<int, (double y, double x)> fitCache = new Dictionary<int, (double y, double x)>();

        double modeBeat0;
        double clipTime;
        // 自由模式的弹簧状态
        Vec[] freePos = Array.Empty<Vec>();
        Vec[] freeVel = Array.Empty<Vec>();
        // 手写打篮球动作的状态
        double figPhase;
        bool figInit;
        Vec handA = Vec.Zero, handP = Vec.Zero;
        double leanS, hipS;

        public double BaseScale => Stage.H > Stage.W ? 0.78 : 1.0;

        public void SetMode(DanceMode m, double beat, Pose[] current)
        {
            Mode = m;
            modeBeat0 = beat;
            freePos = new Vec[current.Length];
            for (int i = 0; i < current.Length; i++) freePos[i] = current[i].P;
            freeVel = new Vec[current.Length];
            figPhase = 0;
            figInit = false;
            clipTime = 0;
        }

        public Pose[] Poses(int n, Tempo tempo)
        {
            if (n <= 0) return Array.Empty<Pose>();
            switch (Mode)
            {
                case DanceMode.Snake: return Snake(n, tempo);
                case DanceMode.Figure: return Figure(n, tempo);
                case DanceMode.Groove: return Groove(n, tempo);
                default: return Free(n, tempo);
            }
        }

        // ---------- 列队：一小列 → 拉长 → S 形 ⇄ Y 形，波浪沿队伍传递 ----------

        Pose[] Snake(int n, Tempo T)
        {
            RectD s = Stage;
            double b = T.Beat - modeBeat0;
            double bf = b;
            double e = T.Energy, pulse = T.Pulse;
            double cx = s.MidX, cy = s.MidY;
            bool portrait = s.H > s.W;
            double k = BaseScale;
            double margin = IconSize * 0.55 * k;
            double A = Amp * IconSize * k * (0.45 + 0.9 * e);                 // 横向波浪幅度

            double Lshort = Math.Min(s.H * 0.28, n * IconSize * 0.15);        // 开场：中间一小列
            double Llong = s.H - margin * 2;
            double grow = M.Smooth((b - 4) / 4);                              // 第 4~8 拍：拉长
            double toS = M.Smooth((b - 8) / 3);                               // 第 8~11 拍：弯成 S
            double wY = 0;                                                    // Y 形的权重
            double mirror = 1;
            double c = b - 11;
            if (c > 0)
            {
                // 48 拍一轮：S(14) →Y(2) Y(6) →S(2) 镜像 S(14) →Y(2) Y(6) →S(2)
                double m = c % 48;
                double Bump(double a0, double b1, double c1, double d) =>
                    M.Smooth((m - a0) / (b1 - a0)) * (1 - M.Smooth((m - c1) / (d - c1)));
                wY = Bump(14, 16, 22, 24) + Bump(38, 40, 46, 48);
                if (m >= 19 && m < 43) mirror = -1;                           // 趁着是 Y 形时翻面，看不出跳变
            }
            double L = M.Lerp(Lshort, Llong, grow);
            int stemCount = Math.Max(1, (int)(n * 0.42));
            int armTotal = n - stemCount;
            int perArm = Math.Max(1, (armTotal + 1) / 2);
            var junction = new Vec(cx, cy - s.H * 0.06);
            double armDX = Math.Min(s.W * 0.40, s.H * 0.46) * (1 + 0.06 * Math.Sin(Math.PI * bf));
            double drift = bf * 0.10;                                         // S 形自己慢慢蠕动

            var result = new Pose[n];
            for (int i = 0; i < n; i++)
            {
                double u0 = n > 1 ? (double)i / (n - 1) : 0.5;
                double u = u0 + 0.010 * Amp * Math.Sin(2 * Math.PI * (bf / 4 - u0 * 2));     // 间距一松一紧
                double ph = Math.PI * bf - 2 * Math.PI * 1.6 * u;                              // 行波：两拍一个来回，沿队伍往后传
                double d = A * (0.45 * Math.Sin(Math.PI * bf) + 0.75 * Math.Sin(ph));          // 整队摆 + 波浪

                // 竖列
                var pLine = new Vec(cx + d * M.Lerp(0.55, 1, grow), cy + (0.5 - u) * L);
                // S 形：横屏沿宽度铺开，竖屏沿高度铺开
                Vec pS;
                if (portrait)
                {
                    pS = new Vec(cx + mirror * s.W * 0.30 * Math.Sin(2 * Math.PI * 1.5 * u + drift) + d * 0.8,
                                 s.MaxY - margin - u * (s.H - margin * 2));
                }
                else
                {
                    pS = new Vec(s.MinX + margin + u * (s.W - margin * 2),
                                 cy + mirror * s.H * 0.30 * Math.Sin(2 * Math.PI * 1.25 * u + drift) + d * 0.8);
                }
                // Y 形：队尾当竖杆，队头左右交替分成两臂
                Vec pY;
                if (i >= armTotal)
                {
                    double v = (double)(i - armTotal + 1) / stemCount;
                    pY = M.Lerp(junction, new Vec(cx, s.MinY + margin), v);
                    pY = new Vec(pY.X + d * 0.6, pY.Y);
                }
                else
                {
                    double side = i % 2 == 0 ? -1 : 1;
                    double v = 1 - (double)(i / 2) / perArm;
                    pY = M.Lerp(junction, new Vec(cx + side * armDX, s.MaxY - margin), v);
                    pY = new Vec(pY.X + d * 0.6, pY.Y + d * 0.25 * side);
                }
                Vec p = M.Lerp(M.Lerp(pLine, pS, toS), pY, wY);
                p = new Vec(p.X, p.Y + 5 * pulse * Amp);
                double tilt = -0.20 * Amp * Math.Cos(ph) * (0.6 + 0.6 * e);
                double sc = k * (1 + 0.10 * pulse + 0.04 * Math.Sin(ph));
                result[i] = new Pose(p, tilt, sc, sc, 1, n - i);
            }
            return result;
        }

        // ---------- 打篮球：图标拼成小人，完全照动作数据走，不在鼓点上做任何瞬间变化 ----------

        static void Ik(Vec a, Vec target, double l1, double l2, double bend, out Vec joint, out Vec end)
        {
            Vec d = target - a;
            double dist = M.Hypot(d.X, d.Y);
            double maxD = (l1 + l2) * 0.999;
            if (dist > maxD) { d = d * (maxD / dist); dist = maxD; }
            if (dist < 1e-4) { joint = new Vec(a.X + l1, a.Y); end = a; return; }
            double aa = (l1 * l1 - l2 * l2 + dist * dist) / (2 * dist);
            double h = Math.Sqrt(Math.Max(0, l1 * l1 - aa * aa));
            double ux = d.X / dist, uy = d.Y / dist;
            joint = new Vec(a.X + ux * aa - uy * h * bend, a.Y + uy * aa + ux * h * bend);
            end = a + d;
        }

        static Vec SamplePolyline(Vec[] pts, double t)
        {
            if (pts.Length == 0) return Vec.Zero;
            if (pts.Length == 1) return pts[0];
            var lens = new double[pts.Length - 1];
            double total = 0;
            for (int i = 1; i < pts.Length; i++)
            {
                double l = M.Hypot(pts[i].X - pts[i - 1].X, pts[i].Y - pts[i - 1].Y);
                lens[i - 1] = l;
                total += l;
            }
            double want = M.Clamp(t, 0, 1) * total;
            for (int i = 0; i < lens.Length; i++)
            {
                if (want <= lens[i] || i == lens.Length - 1)
                    return M.Lerp(pts[i], pts[i + 1], lens[i] > 0 ? want / lens[i] : 0);
                want -= lens[i];
            }
            return pts[pts.Length - 1];
        }

        static int Sum(int[] a) { int s = 0; foreach (int v in a) s += v; return s; }

        /// <summary>把 total 个图标按权重分给各个部位，每个部位至少 1 个。</summary>
        static int[] Allocate(int total, double[] weights)
        {
            double sum = 0;
            foreach (double w in weights) sum += w;
            var counts = new int[weights.Length];
            for (int i = 0; i < weights.Length; i++) counts[i] = Math.Max(1, (int)M.Round(total * weights[i] / sum));
            int guard = 0;
            while (Sum(counts) > total && guard < 500)
            {
                int best = -1;
                for (int i = 0; i < counts.Length; i++)
                {
                    if (counts[i] <= 1) continue;
                    if (best < 0 || counts[best] / weights[best] < counts[i] / weights[i]) best = i;
                }
                if (best < 0) break;
                counts[best]--;
                guard++;
            }
            while (Sum(counts) < total && guard < 1000)
            {
                int best = 0;
                for (int i = 1; i < counts.Length; i++)
                    if (counts[i] / weights[i] < counts[best] / weights[best]) best = i;
                counts[best]++;
                guard++;
            }
            return counts;
        }

        /// <summary>图标太少拼不成人时：围成一圈转。</summary>
        Pose[] Ring(int n, Tempo T)
        {
            RectD s = Stage;
            double r = Math.Min(s.W, s.H) * 0.3;
            var result = new Pose[n];
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n - T.Beat * 0.4;
                double sc = BaseScale * (1 + 0.15 * T.Pulse);
                result[i] = new Pose(new Vec(s.MidX + r * Math.Cos(a), s.MidY + r * Math.Sin(a)), 0, sc, sc);
            }
            return result;
        }

        /// <summary>小人的一帧骨架。坐标以「身高」为单位，脚下为原点。P / M 是身体的两侧。</summary>
        struct Skeleton
        {
            public Vec Head, Neck, Hip;
            public Vec ShP, ElbP, HandP;
            public Vec ShM, ElbM, HandM;
            public Vec HipP, KneeP, FootP;
            public Vec HipM, KneeM, FootM;
            public Vec Ball;
            public double Lean;
            public double BallAlpha;
        }

        static Skeleton FromSample(MotionClip.Sample s)
        {
            Vec J(Joint k) => s.Joints[(int)k];
            Vec neck = J(Joint.Neck), hip = J(Joint.Root);
            // 头心：鼻子和两只耳朵的平均
            Vec head = (J(Joint.Nose) + J(Joint.LEar) + J(Joint.REar)) * (1.0 / 3.0);
            double lean = Math.Atan2(neck.X - hip.X, Math.Max(neck.Y - hip.Y, 0.01));
            return new Skeleton
            {
                Head = head, Neck = neck, Hip = hip,
                ShP = J(Joint.LSho), ElbP = J(Joint.LElb), HandP = J(Joint.LWri),
                ShM = J(Joint.RSho), ElbM = J(Joint.RElb), HandM = J(Joint.RWri),
                HipP = J(Joint.LHip), KneeP = J(Joint.LKne), FootP = J(Joint.LAnk),
                HipM = J(Joint.RHip), KneeM = J(Joint.RKne), FootM = J(Joint.RAnk),
                Ball = s.Ball, Lean = lean, BallAlpha = s.BallAlpha,
            };
        }

        ClipMeans Means(MotionClip clip)
        {
            if (clipMeans != null) return clipMeans;
            int n = Math.Max(2, (int)(clip.Duration * 20));
            Vec root = Vec.Zero, ball = Vec.Zero;
            var offs = new Vec[MotionClip.JointCount];
            double ballN = 0;
            for (int i = 0; i < n; i++)
            {
                MotionClip.Sample smp = clip.SampleAt(clip.Duration * i / n);
                Vec r = smp.Joints[(int)Joint.Root];
                root = root + r;
                for (int j = 0; j < offs.Length; j++) offs[j] = offs[j] + (smp.Joints[j] - r);
                if (smp.BallAlpha > 0.5) { ball = ball + (smp.Ball - r); ballN += 1; }
            }
            double inv = 1.0 / n;
            for (int j = 0; j < offs.Length; j++) offs[j] = offs[j] * inv;
            var m = new ClipMeans { Root = root * inv, Offs = offs, Ball = ballN > 0 ? ball * (1 / ballN) : Vec.Zero };
            clipMeans = m;
            return m;
        }

        /// <summary>动作放大 ex 倍后，小人最高点和肢体横向伸展各变成原来的多少倍（用来相应缩小小人，保证不出画）。</summary>
        void Growth(MotionClip clip, double ex, out double growY, out double growX)
        {
            int key = (int)M.Round(ex * 50);
            if (fitCache.TryGetValue(key, out var cached)) { growY = cached.y; growX = cached.x; return; }
            ClipMeans m = Means(clip);
            void Extent(double e, out double maxY, out double maxX)
            {
                maxY = 0.001; maxX = 0.001;
                int n = Math.Max(2, (int)(clip.Duration * 20));
                for (int i = 0; i < n; i++)
                {
                    MotionClip.Sample smp = clip.SampleAt(clip.Duration * i / n);
                    Vec r0 = smp.Joints[(int)Joint.Root];
                    var r1 = new Vec(r0.X, m.Root.Y + (r0.Y - m.Root.Y) * e);
                    for (int j = 0; j < smp.Joints.Length; j++)
                    {
                        Vec p = r1 + m.Offs[j] + (smp.Joints[j] - r0 - m.Offs[j]) * e;
                        maxY = Math.Max(maxY, p.Y);
                        maxX = Math.Max(maxX, Math.Abs(p.X - r1.X));       // 横向只看肢体伸多开，走位不变
                    }
                }
            }
            Extent(1, out double y1, out double x1);
            Extent(ex, out double y2, out double x2);
            growY = Math.Max(1, y2 / y1);
            growX = Math.Max(1, x2 / x1);
            fitCache[key] = (growY, growX);
        }

        Pose[] Figure(int n, Tempo T)
        {
            if (n < 8) return Ring(n, T);
            RectD s = Stage;
            Skeleton sk;
            double H;
            double spin;
            double lift = 0;
            MotionClip? clip = Clip;
            if (clip != null)
            {
                clipTime += T.Dt * ClipRate;
                // 幅度：1 = 原样；越大动作越夸张（手脚挥得更开、蹲得更低），越小越收着。
                // 做法是把这一刻的姿势相对「平均姿势」放大 ex 倍；脚相对地面的位置不受影响。
                double ex = Amp <= 1 ? Math.Max(Amp, 0.3) : 1 + (Math.Min(Amp, 2) - 1) * 0.7;
                double growY = 1, growX = 1;
                if (ex > 1.01) Growth(clip, ex, out growY, out growX);
                double topY = clip.MaxY * growY, reachX = clip.MaxLocalX * growX;
                // 先按高度撑满舞台；宽度不够时只压缩「整个人左右走位」，肢体动作保持原样
                double h = s.H * 0.98 / topY;
                double halfW = s.W * 0.5;
                double travel = 1;
                if ((clip.MaxRootX + reachX) * h > halfW)
                {
                    travel = clip.MaxRootX > 0.001 ? M.Clamp((halfW / h - reachX) / clip.MaxRootX, 0, 1) : 0;
                    if (reachX * h > halfW) h = halfW / reachX;
                }
                MotionClip.Sample smp = clip.SampleAt(clipTime);
                if (Math.Abs(ex - 1) > 0.01)
                {
                    ClipMeans m = Means(clip);
                    Vec r0 = smp.Joints[(int)Joint.Root];
                    // 左右走位保持原样（放大走位只会让小人为了不出画而缩得很小），放大的是下蹲起伏和手脚动作
                    var r1 = new Vec(r0.X, m.Root.Y + (r0.Y - m.Root.Y) * ex);
                    Vec b1 = r1 + m.Ball + (smp.Ball - r0 - m.Ball) * ex;
                    for (int j = 0; j < smp.Joints.Length; j++)
                        smp.Joints[j] = r1 + m.Offs[j] + (smp.Joints[j] - r0 - m.Offs[j]) * ex;
                    smp.Ball = b1;
                }
                if (travel < 1)
                {
                    double shift = smp.Joints[(int)Joint.Root].X * (travel - 1);
                    for (int i = 0; i < smp.Joints.Length; i++) smp.Joints[i] = new Vec(smp.Joints[i].X + shift, smp.Joints[i].Y);
                    smp.Ball = new Vec(smp.Ball.X + shift, smp.Ball.Y);
                }
                sk = FromSample(smp);
                H = h;
                lift = Math.Max(0, (s.H * 0.98 - topY * h) / 2);              // 垂直居中
                spin = -clipTime * 5;
            }
            else
            {
                sk = ProceduralSkeleton(T);
                H = Math.Min(s.H * 0.96, s.W * 1.45);
                spin = -figPhase * 2.4;
            }
            // 这个模式不跟拍：动作、位置、图标大小完全照动作数据走。
            // 试过在鼓点上让图标鼓一下、歪一下，瞬间的跳变看起来像抽搐，所以去掉了（和 Mac 版一致）。
            // 跟音乐的方式是「快慢」：歌快动作就快，由外面的总控通过 ClipRate 慢慢调。
            var origin = new Vec(s.MidX, s.MinY + s.H * 0.02 + lift);
            Vec Scr(Vec p) => origin + p * H;
            double wob = clip == null ? 0.06 * Math.Sin(Math.PI * figPhase) : 0;

            // 分配图标：0 号当篮球，其余按各部位长度分给 头/躯干/两臂/两腿
            const double headR = 0.085;
            double th = sk.Lean;
            Vec head = sk.Head, neck = sk.Neck, hip = sk.Hip, ball = sk.Ball;
            double[] weights = { 2 * Math.PI * headR, 0.30, 0.40, 0.40, 0.43, 0.43 };
            int[] counts = Allocate(n - 1, weights);
            double wsum = 0;
            foreach (double w in weights) wsum += w;
            double spacing = wsum * H / (n - 1);
            double k = M.Clamp(spacing / IconSize * 1.25, 0.34, 1.0);

            var list = new List<Pose>(n + 4);
            double ballScale = Math.Min(k * 1.7, 1.25);
            list.Add(new Pose(Scr(ball), spin, ballScale, ballScale, sk.BallAlpha, 2000));
            // 头：一圈
            int hc = counts[0];
            for (int j = 0; j < hc; j++)
            {
                double a = 2 * Math.PI * j / hc + Math.PI / 2;
                Vec p = hc < 3 ? head : head + new Vec(Math.Cos(a), Math.Sin(a)) * headR;
                list.Add(new Pose(Scr(p), th * 0.6, k, k, 1, 900 + j));
            }
            void AddLine(Vec[] pts, int count, double z, double rot)
            {
                for (int j = 0; j < count; j++)
                {
                    Vec p = SamplePolyline(pts, (j + 0.5) / count);
                    list.Add(new Pose(Scr(p), rot, k, k, 1, z + j));
                }
            }
            AddLine(new[] { neck, hip }, counts[1], 500, th * 0.6);
            AddLine(new[] { neck, sk.ShP, sk.ElbP, sk.HandP }, counts[2], 1000, wob);
            AddLine(new[] { neck, sk.ShM, sk.ElbM, sk.HandM }, counts[3], 1100, -wob);
            AddLine(new[] { sk.HipP, sk.KneeP, sk.FootP }, counts[4], 300, 0);
            AddLine(new[] { sk.HipM, sk.KneeM, sk.FootM }, counts[5], 200, 0);
            while (list.Count < n) list.Add(new Pose(Scr(hip), 0, k, k));

            // list[0] 是球，其余按身体顺序；把球换给 BallIndex 指定的图标
            int bi = Math.Min(Math.Max(BallIndex, 0), n - 1);
            var result = new Pose[n];
            int src = 1;
            for (int i = 0; i < n; i++)
            {
                if (i == bi) result[i] = list[0];
                else { result[i] = list[src]; src++; }
            }
            return result;
        }

        /// <summary>没有动作数据时的手写动作：运球 → 过头抛球 → 左右靠，再换另一只手。</summary>
        Skeleton ProceduralSkeleton(Tempo T)
        {
            double e = T.Energy;
            // 音乐强时按原速，轻时放慢到一半
            figPhase += T.DBeat * M.Lerp(0.5, 1.0, M.Smooth((e - 0.15) / 0.45));
            double psi = figPhase % 16;
            bool second = psi >= 8;                     // 后 8 拍：整套动作左右镜像
            double q = second ? psi - 8 : psi;
            double sg = second ? -1 : 1;
            double f = q - Math.Floor(q);

            // 以下坐标都以「身高」为单位，地面 y=0，持球的一侧为 +x
            const double rb = 0.060;                    // 球半径
            const double yTop = 0.385;                  // 球在手里时球心高度
            double theta = 0.04 * Math.Sin(Math.PI * q);            // 上身倾斜
            double hx = 0.012 * Math.Sin(Math.PI * q);              // 胯部横移
            double dip = 0.03 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * q));
            var hA = new Vec(0.26, 0.40);               // 持球手
            var hP = new Vec(-0.20, 0.33);              // 另一只手
            var ball = new Vec(0.26, yTop);
            if (q < 4)
            {
                // 运球：每拍一下，球在拍子正中落地
                hA = new Vec(0.26, 0.40 + 0.045 * Math.Cos(2 * Math.PI * f));
                ball = new Vec(0.262, rb + (yTop - rb) * Math.Abs(Math.Cos(Math.PI * f)));
                hP = new Vec(-0.20 - 0.02 * Math.Sin(Math.PI * q), 0.33 + 0.025 * Math.Sin(Math.PI * q));
                theta += 0.06;
            }
            else if (q < 6)
            {
                // 过头抛球：从这只手划一道弧线到另一只手
                double st = (q - 4) / 2;
                ball = new Vec(M.Lerp(0.26, -0.26, st), yTop + (0.97 - yTop) * 4 * st * (1 - st));
                var under = new Vec(ball.X, ball.Y - rb * 0.9);
                hA = M.Lerp(new Vec(0.21, 0.35), under, 1 - M.Smooth((st - 0.22) / 0.2));
                var ready = new Vec(-0.25, 0.36 + 0.22 * M.Smooth((st - 0.25) / 0.4));
                hP = M.Lerp(ready, under, M.Smooth((st - 0.62) / 0.25));
                theta = 0.10 * (1 - 2 * st);
                dip = 0.02;
            }
            else
            {
                // 左右靠：抱着球，先靠向一边再靠向另一边
                double w = Math.Sin(Math.PI * (q - 6));
                double shift = -0.07 * w;
                ball = new Vec(-0.26 + shift, yTop);
                hP = new Vec(-0.26 + shift, yTop - rb * 0.9);
                hA = new Vec(0.22 + 0.07 * Math.Abs(w) + shift, 0.35 + 0.12 * Math.Abs(w));
                theta = -0.24 * w;
                hx = 0.045 * w;
                dip = 0.035 * Math.Abs(w);
            }
            // 镜像
            hA = new Vec(hA.X * sg, hA.Y); hP = new Vec(hP.X * sg, hP.Y); ball = new Vec(ball.X * sg, ball.Y);
            theta *= sg; hx *= sg;
            // 手和身体做一点平滑，动作衔接处不突兀；球不平滑，弹地才脆
            double kS = figInit ? 1 - Math.Exp(-T.Dt / 0.06) : 1;
            figInit = true;
            handA = M.Lerp(handA, hA, kS); handP = M.Lerp(handP, hP, kS);
            leanS = M.Lerp(leanS, theta, kS); hipS = M.Lerp(hipS, hx, kS);
            double th = leanS;

            var hip = new Vec(hipS, 0.40 - dip);
            var up = new Vec(Math.Sin(th), Math.Cos(th));
            var right = new Vec(Math.Cos(th), -Math.Sin(th));
            Vec neck = hip + up * 0.30;
            Vec head = neck + new Vec(Math.Sin(th * 1.3), Math.Cos(th * 1.3)) * 0.115;
            Vec shPlus = neck + right * 0.09 - up * 0.015;
            Vec shMinus = neck - right * 0.09 - up * 0.015;
            Vec tPlus = sg > 0 ? handA : handP;
            Vec tMinus = sg > 0 ? handP : handA;
            Ik(shPlus, tPlus, 0.155, 0.155, 1, out Vec elbPlus, out Vec endPlus);
            Ik(shMinus, tMinus, 0.155, 0.155, -1, out Vec elbMinus, out Vec endMinus);
            Vec hipPlus = hip + new Vec(0.05, 0), hipMinus = hip + new Vec(-0.05, 0);
            Vec footPlus = new Vec(0.12, 0), footMinus = new Vec(-0.12, 0);
            Ik(hipPlus, footPlus, 0.215, 0.215, 1, out Vec kneePlus, out _);
            Ik(hipMinus, footMinus, 0.215, 0.215, -1, out Vec kneeMinus, out _);

            return new Skeleton
            {
                Head = head, Neck = neck, Hip = hip,
                ShP = shPlus, ElbP = elbPlus, HandP = endPlus,
                ShM = shMinus, ElbM = elbMinus, HandM = endMinus,
                HipP = hipPlus, KneeP = kneePlus, FootP = footPlus,
                HipM = hipMinus, KneeM = kneeMinus, FootM = footMinus,
                Ball = ball, Lean = th, BallAlpha = 1,
            };
        }

        // ---------- 律动：原地压扁、回弹、左右摇摆 ----------

        Vec[] GrooveSpots(int n, out int cols)
        {
            if (GrooveUsesHomes && Homes.Length >= n)
            {
                cols = 0;
                var h = new Vec[n];
                Array.Copy(Homes, h, n);
                return h;
            }
            RectD s = Stage;
            double k = BaseScale;
            cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n * s.W / Math.Max(s.H, 1))));
            int rows = (int)Math.Ceiling((double)n / cols);
            double cw = Math.Min(s.W / cols, IconSize * 1.8 * k);
            double ch = Math.Min(s.H * 0.86 / Math.Max(rows, 1), IconSize * 1.8 * k);
            var pts = new Vec[n];
            for (int i = 0; i < n; i++)
            {
                int c = i % cols, r = i / cols;
                pts[i] = new Vec(s.MidX + (c - (cols - 1) / 2.0) * cw, s.MidY + ((rows - 1) / 2.0 - r) * ch);
            }
            return pts;
        }

        Pose[] Groove(int n, Tempo T)
        {
            Vec[] pts = GrooveSpots(n, out int cols);
            double k = BaseScale;
            double bf = T.Beat, e = T.Energy, pulse = T.Pulse;
            double a = Math.Min(Amp, 1.6);
            var result = new Pose[n];
            for (int i = 0; i < n; i++)
            {
                int col = cols > 0 ? i % cols : (int)(pts[i].X / Math.Max(GridW, 1));
                int row = cols > 0 ? i / cols : (int)(pts[i].Y / Math.Max(GridH, 1));
                double alt = (col + row) % 2 == 0 ? 0 : Math.PI;
                double hop = IconSize * k * 0.24 * Amp * (0.4 + e) * Math.Abs(Math.Sin(Math.PI * bf));   // 拍子中间腾空，重拍落地
                double sy = 1 - 0.26 * pulse * a;                                                         // 落地压扁
                double sx = 1 + 0.20 * pulse * a;
                double px = pts[i].X + IconSize * k * 0.10 * Amp * Math.Sin(Math.PI * bf / 2 + row * 0.7);
                double py = pts[i].Y + hop - IconSize * k * 0.5 * (1 - sy);                               // 压扁时底边贴地
                double rot = 0.17 * Amp * (0.5 + e) * Math.Sin(Math.PI * bf + alt);
                result[i] = new Pose(new Vec(px, py), rot, k * sx, k * sy, 1, i);
            }
            return result;
        }

        // ---------- 自由：聚在中央顺时针流动，鼓点一响向外弹开，再弹性收回 ----------

        Pose[] Free(int n, Tempo T)
        {
            RectD s = Stage;
            double k = BaseScale;
            double bf = T.Beat, e = T.Energy, pulse = T.Pulse;
            if (freePos.Length != n)
            {
                freePos = new Vec[n];
                for (int i = 0; i < n; i++) freePos[i] = i < Homes.Length ? Homes[i] : new Vec(s.MidX, s.MidY);
                freeVel = new Vec[n];
            }
            double R0 = Math.Min(s.W, s.H) * 0.26;
            double squashY = s.H > s.W ? 1.25 : 0.85;
            double dt = Math.Min(Math.Max(T.Dt, 0), 1.0 / 30);
            var result = new Pose[n];
            for (int i = 0; i < n; i++)
            {
                double r = R0 * (0.30 + 0.70 * Math.Sqrt(M.Hash01(i, 11)));
                double omega = -(0.22 + 0.55 * M.Hash01(i, 23));                                // 负号 = 顺时针
                double ang = 2 * Math.PI * M.Hash01(i, 37) + omega * bf;
                double burst = 1 + pulse * (0.7 + 1.0 * M.Hash01(i, 53)) * Amp * (0.5 + 0.8 * e);
                var target = new Vec(s.MidX + r * burst * Math.Cos(ang), s.MidY + r * burst * Math.Sin(ang) * squashY);
                if (dt > 0)
                {
                    // 欠阻尼弹簧：弹开后会带一点回弹
                    Vec acc = (target - freePos[i]) * 95 - freeVel[i] * 9.5;
                    freeVel[i] = freeVel[i] + acc * dt;
                    freePos[i] = freePos[i] + freeVel[i] * dt;
                }
                else
                {
                    freePos[i] = target;
                }
                double rot = M.Clamp(freeVel[i].X / 1800, -0.35, 0.35);
                double sc = k * (0.9 + 0.14 * pulse);
                result[i] = new Pose(freePos[i], rot, sc, sc, 1, i);
            }
            return result;
        }
    }
}
