import AppKit

enum DanceMode: Int, CaseIterable {
    case snake, figure, groove, free

    var title: String {
        switch self {
        case .snake: return T("列队", "Line")
        case .figure: return T("打篮球", "Basketball")
        case .groove: return T("律动", "Groove")
        case .free: return T("自由", "Free")
        }
    }

    /// 自动切换时每个模式停留多少拍。
    var autoBeats: Double {
        switch self {
        case .snake: return 44
        case .figure: return 32
        case .groove: return 16
        case .free: return 16
        }
    }

    static func named(_ s: String) -> DanceMode? {
        switch s.lowercased() {
        case "snake", "列队": return .snake
        case "figure", "打篮球", "basketball": return .figure
        case "groove", "律动": return .groove
        case "free", "自由": return .free
        default: return nil
        }
    }
}

/// 每一帧传给编舞的节拍信息。
struct Tempo {
    var beat: Double      // 连续拍数（整数处是重拍）
    var dBeat: Double     // 这一帧前进了多少拍
    var dt: Double
    var energy: Double    // 0...1 音乐强度
    var pulse: Double     // 鼓点脉冲，击中时为 1，随后衰减
}

/// 编舞：给定节拍，算出每个图标该在哪、转多少、压多扁。
/// 想改队形就改这个文件——每个模式是一个独立的函数。
final class Choreographer {
    var stage: CGRect = .zero          // 可以跳舞的区域（窗口坐标）
    var iconSize: F = 64
    var amp: F = 1                     // 幅度滑块
    var homes: [CGPoint] = []          // 每个图标的原位
    var grooveUsesHomes = true         // 律动模式：全屏时在原位跳，竖屏时排进舞台
    var ballIndex = 0                  // 打篮球模式里哪个图标当篮球
    private(set) var mode: DanceMode = .snake
    private var modeBeat0: Double = 0

    // 自由模式的弹簧状态
    private var freePos: [CGPoint] = []
    private var freeVel: [CGPoint] = []
    // 打篮球的状态
    private var figPhase: Double = 0
    private var figInit = false
    private var handA = CGPoint.zero, handP = CGPoint.zero
    private var leanS: F = 0, hipS: F = 0

    var baseScale: F { stage.height > stage.width ? 0.78 : 1.0 }

    func setMode(_ m: DanceMode, beat: Double, current: [Pose]) {
        mode = m
        modeBeat0 = beat
        freePos = current.map { $0.p }
        freeVel = Array(repeating: .zero, count: current.count)
        figPhase = 0
        figInit = false
        clipTime = 0
    }

    func poses(n: Int, tempo: Tempo) -> [Pose] {
        guard n > 0 else { return [] }
        switch mode {
        case .snake: return snake(n, tempo)
        case .figure: return figure(n, tempo)
        case .groove: return groove(n, tempo)
        case .free: return free(n, tempo)
        }
    }

    // MARK: - 列队：一小列 → 拉长 → S 形 ⇄ Y 形，波浪沿队伍传递

    private func snake(_ n: Int, _ T: Tempo) -> [Pose] {
        let s = stage
        let b = T.beat - modeBeat0
        let bf = F(b)
        let e = F(T.energy), pulse = F(T.pulse)
        let cx = s.midX, cy = s.midY
        let portrait = s.height > s.width
        let k = baseScale
        let margin = iconSize * 0.55 * k
        let A = amp * iconSize * k * (0.45 + 0.9 * e)                 // 横向波浪幅度（点）

        let Lshort = min(s.height * 0.28, F(n) * iconSize * 0.15)     // 开场：中间一小列
        let Llong = s.height - margin * 2
        let grow = smooth(F((b - 4) / 4))                             // 第 4~8 拍：拉长
        let toS = smooth(F((b - 8) / 3))                              // 第 8~11 拍：弯成 S
        var wY: F = 0                                                 // Y 形的权重
        var mirror: F = 1
        let c = b - 11
        if c > 0 {
            // 48 拍一轮：S(14) →Y(2) Y(6) →S(2) 镜像 S(14) →Y(2) Y(6) →S(2)
            let m = c.truncatingRemainder(dividingBy: 48)
            func bump(_ a: Double, _ b1: Double, _ c1: Double, _ d: Double) -> F {
                smooth(F((m - a) / (b1 - a))) * (1 - smooth(F((m - c1) / (d - c1))))
            }
            wY = bump(14, 16, 22, 24) + bump(38, 40, 46, 48)
            if m >= 19 && m < 43 { mirror = -1 }                      // 趁着是 Y 形时翻面，看不出跳变
        }
        let L = lerp(Lshort, Llong, grow)
        let stemCount = max(1, Int(F(n) * 0.42))
        let armTotal = n - stemCount
        let perArm = max(1, (armTotal + 1) / 2)
        let junction = CGPoint(x: cx, y: cy - s.height * 0.06)
        let armDX = min(s.width * 0.40, s.height * 0.46) * (1 + 0.06 * sin(.pi * bf))
        let drift = bf * 0.10                                         // S 形自己慢慢蠕动

        var out: [Pose] = []
        out.reserveCapacity(n)
        for i in 0..<n {
            let u0: F = n > 1 ? F(i) / F(n - 1) : 0.5
            let u = u0 + 0.010 * amp * sin(2 * .pi * (bf / 4 - u0 * 2))            // 间距一松一紧
            let ph = .pi * bf - 2 * .pi * 1.6 * u                                   // 行波：两拍一个来回，沿队伍往后传
            let d = A * (0.45 * sin(.pi * bf) + 0.75 * sin(ph))                     // 整队摆 + 波浪

            // 竖列
            let pLine = CGPoint(x: cx + d * lerp(0.55, 1, grow), y: cy + (0.5 - u) * L)
            // S 形：横屏沿宽度铺开，竖屏沿高度铺开
            var pS: CGPoint
            if portrait {
                pS = CGPoint(x: cx + mirror * s.width * 0.30 * sin(2 * .pi * 1.5 * u + drift) + d * 0.8,
                             y: s.maxY - margin - u * (s.height - margin * 2))
            } else {
                pS = CGPoint(x: s.minX + margin + u * (s.width - margin * 2),
                             y: cy + mirror * s.height * 0.30 * sin(2 * .pi * 1.25 * u + drift) + d * 0.8)
            }
            // Y 形：队尾当竖杆，队头左右交替分成两臂
            var pY: CGPoint
            if i >= armTotal {
                let v = F(i - armTotal + 1) / F(stemCount)
                pY = lerpP(junction, CGPoint(x: cx, y: s.minY + margin), v)
                pY.x += d * 0.6
            } else {
                let side: F = i % 2 == 0 ? -1 : 1
                let v = 1 - F(i / 2) / F(perArm)
                pY = lerpP(junction, CGPoint(x: cx + side * armDX, y: s.maxY - margin), v)
                pY.x += d * 0.6
                pY.y += d * 0.25 * side
            }
            var p = lerpP(lerpP(pLine, pS, toS), pY, wY)
            p.y += 5 * pulse * amp
            let tilt = -0.20 * amp * cos(ph) * (0.6 + 0.6 * e)
            let sc = k * (1 + 0.10 * pulse + 0.04 * sin(ph))
            out.append(Pose(p: p, rot: tilt, sx: sc, sy: sc, alpha: 1, z: F(n - i)))
        }
        return out
    }

    // MARK: - 打篮球：图标拼成小人，运球 → 过头抛球 → 左右靠，再换另一只手

    private func ik(_ a: CGPoint, _ target: CGPoint, _ l1: F, _ l2: F, bend: F) -> (joint: CGPoint, end: CGPoint) {
        var d = target - a
        var dist = hypot(d.x, d.y)
        let maxD = (l1 + l2) * 0.999
        if dist > maxD { d = d * (maxD / dist); dist = maxD }
        if dist < 1e-4 { return (CGPoint(x: a.x + l1, y: a.y), a) }
        let aa = (l1 * l1 - l2 * l2 + dist * dist) / (2 * dist)
        let h = sqrt(max(0, l1 * l1 - aa * aa))
        let ux = d.x / dist, uy = d.y / dist
        return (CGPoint(x: a.x + ux * aa - uy * h * bend, y: a.y + uy * aa + ux * h * bend), a + d)
    }

    private func samplePolyline(_ pts: [CGPoint], _ t: F) -> CGPoint {
        guard pts.count > 1 else { return pts.first ?? .zero }
        var lens: [F] = []
        var total: F = 0
        for i in 1..<pts.count { let l = hypot(pts[i].x - pts[i - 1].x, pts[i].y - pts[i - 1].y); lens.append(l); total += l }
        var want = clamp(t, 0, 1) * total
        for i in 0..<lens.count {
            if want <= lens[i] || i == lens.count - 1 {
                return lerpP(pts[i], pts[i + 1], lens[i] > 0 ? want / lens[i] : 0)
            }
            want -= lens[i]
        }
        return pts[pts.count - 1]
    }

    private func allocate(_ total: Int, _ weights: [F]) -> [Int] {
        let sum = weights.reduce(0, +)
        var counts = weights.map { max(1, Int((F(total) * $0 / sum).rounded())) }
        var guardN = 0
        while counts.reduce(0, +) > total, guardN < 500 {
            if let i = counts.indices.filter({ counts[$0] > 1 }).max(by: { F(counts[$0]) / weights[$0] < F(counts[$1]) / weights[$1] }) {
                counts[i] -= 1
            } else { break }
            guardN += 1
        }
        while counts.reduce(0, +) < total, guardN < 1000 {
            let i = counts.indices.min(by: { F(counts[$0]) / weights[$0] < F(counts[$1]) / weights[$1] })!
            counts[i] += 1
            guardN += 1
        }
        return counts
    }

    private func ring(_ n: Int, _ T: Tempo) -> [Pose] {
        let s = stage
        let r = min(s.width, s.height) * 0.3
        return (0..<n).map { i in
            let a = 2 * F.pi * F(i) / F(n) - F(T.beat) * 0.4
            let sc = baseScale * (1 + 0.15 * F(T.pulse))
            return Pose(p: CGPoint(x: s.midX + r * cos(a), y: s.midY + r * sin(a)), rot: 0, sx: sc, sy: sc)
        }
    }

    /// 小人的一帧骨架。坐标以「身高」为单位，脚下为原点。P / M 是身体的两侧。
    struct Skeleton {
        var head, neck, hip: CGPoint
        var shP, elbP, handP: CGPoint
        var shM, elbM, handM: CGPoint
        var hipP, kneeP, footP: CGPoint
        var hipM, kneeM, footM: CGPoint
        var ball: CGPoint
        var lean: F
        var ballAlpha: F = 1
    }

    /// 动作数据（从参考视频里逐帧提取的关节位置）。有它就照着它跳；没有就用手写动作。
    var clip: MotionClip? { didSet { clipMeans = nil; fitCache = [:] } }
    private var fitCache: [Int: (y: F, x: F)] = [:]

    /// 幅度放大到 ex 倍后，整段动作最高会到哪、肢体最宽会伸到哪，相对原样各是多少倍（≥1）。
    /// 排版时用它把「最高点」「最宽处」换成放大后的数，这样既不出画，也只在真的顶到边时才缩小。
    private func growth(_ clip: MotionClip, ex: F) -> (y: F, x: F) {
        let key = Int((ex * 50).rounded())
        if let v = fitCache[key] { return v }
        let m = means(of: clip)
        func extent(_ e: F) -> (y: F, x: F) {
            var my: F = 0.001, mx: F = 0.001
            let n = max(2, Int(clip.duration * 20))
            for i in 0..<n {
                let smp = clip.sample(clip.duration * Double(i) / Double(n))
                let r0 = smp.joints[Joint.root.rawValue]
                let r1 = CGPoint(x: r0.x, y: m.root.y + (r0.y - m.root.y) * e)
                for j in smp.joints.indices {
                    let p = r1 + m.offs[j] + (smp.joints[j] - r0 - m.offs[j]) * e
                    my = max(my, p.y); mx = max(mx, abs(p.x - r1.x))      // 横向只看肢体伸多开，走位不变
                }
            }
            return (my, mx)
        }
        let a = extent(1), b = extent(ex)
        let v = (y: max(1, b.y / a.y), x: max(1, b.x / a.x))
        fitCache[key] = v
        return v
    }
    /// 整段动作的「平均姿势」：胯的平均位置、各关节相对胯的平均偏移、球相对胯的平均偏移。
    /// 「幅度」就是把每一刻的姿势相对这个平均姿势放大或缩小。
    private var clipMeans: (root: CGPoint, offs: [CGPoint], ball: CGPoint)?

    private func means(of clip: MotionClip) -> (root: CGPoint, offs: [CGPoint], ball: CGPoint) {
        if let m = clipMeans { return m }
        let n = max(2, Int(clip.duration * 20))
        var root = CGPoint.zero, ball = CGPoint.zero
        var offs: [CGPoint] = []
        var ballN: F = 0
        for i in 0..<n {
            let smp = clip.sample(clip.duration * Double(i) / Double(n))
            let r = smp.joints[Joint.root.rawValue]
            if offs.isEmpty { offs = [CGPoint](repeating: .zero, count: smp.joints.count) }
            root = root + r
            for j in smp.joints.indices { offs[j] = offs[j] + (smp.joints[j] - r) }
            if smp.ballAlpha > 0.5 { ball = ball + (smp.ball - r); ballN += 1 }
        }
        let inv = 1 / F(n)
        let m = (root: root * inv, offs: offs.map { $0 * inv }, ball: ballN > 0 ? ball * (1 / ballN) : .zero)
        clipMeans = m
        return m
    }
    var clipRate: Double = 1
    private var clipTime: Double = 0

    private func skeleton(from s: MotionClip.Sample) -> Skeleton {
        func j(_ k: Joint) -> CGPoint { s.joints[k.rawValue] }
        let neck = j(.neck), hip = j(.root)
        // 头心：鼻子和两只耳朵的平均
        let head = (j(.nose) + j(.lear) + j(.rear)) * (1.0 / 3.0)
        let lean = atan2(neck.x - hip.x, max(neck.y - hip.y, 0.01))
        return Skeleton(head: head, neck: neck, hip: hip,
                        shP: j(.lsho), elbP: j(.lelb), handP: j(.lwri),
                        shM: j(.rsho), elbM: j(.relb), handM: j(.rwri),
                        hipP: j(.lhip), kneeP: j(.lkne), footP: j(.lank),
                        hipM: j(.rhip), kneeM: j(.rkne), footM: j(.rank),
                        ball: s.ball, lean: lean, ballAlpha: s.ballAlpha)
    }

    private func figure(_ n: Int, _ T: Tempo) -> [Pose] {
        guard n >= 8 else { return ring(n, T) }
        let s = stage
        let sk: Skeleton
        let H: F
        let spin: F
        var lift: F = 0
        if let clip {
            clipTime += T.dt * clipRate
            // 幅度：1 = 原样；越大动作越夸张（手脚挥得更开、蹲得更低），越小越收着。
            // 做法是把这一刻的姿势相对「平均姿势」放大 ex 倍；脚相对地面的位置不受影响。
            let ex: F = amp <= 1 ? max(amp, 0.3) : 1 + (min(amp, 2) - 1) * 0.7
            let g = ex > 1.01 ? growth(clip, ex: ex) : (y: F(1), x: F(1))
            let topY = clip.maxY * g.y, reachX = clip.maxLocalX * g.x
            // 先按高度撑满舞台；宽度不够时只压缩「整个人左右走位」，肢体动作保持原样
            var h = s.height * 0.98 / topY
            let halfW = s.width * 0.5
            var travel: F = 1
            if (clip.maxRootX + reachX) * h > halfW {
                travel = clip.maxRootX > 0.001 ? clamp((halfW / h - reachX) / clip.maxRootX, 0, 1) : 0
                if reachX * h > halfW { h = halfW / reachX }
            }
            var smp = clip.sample(clipTime)
            if abs(ex - 1) > 0.01 {
                let m = means(of: clip)
                let r0 = smp.joints[Joint.root.rawValue]
                // 左右走位保持原样（放大走位只会让小人为了不出画而缩得很小），放大的是下蹲起伏和手脚动作
                let r1 = CGPoint(x: r0.x, y: m.root.y + (r0.y - m.root.y) * ex)
                let b1 = r1 + m.ball + (smp.ball - r0 - m.ball) * ex
                smp.joints = smp.joints.indices.map { j in r1 + m.offs[j] + (smp.joints[j] - r0 - m.offs[j]) * ex }
                smp.ball = b1
            }
            if travel < 1 {
                let shift = smp.joints[Joint.root.rawValue].x * (travel - 1)
                smp.joints = smp.joints.map { CGPoint(x: $0.x + shift, y: $0.y) }
                smp.ball.x += shift
            }
            sk = skeleton(from: smp)
            H = h
            lift = max(0, (s.height * 0.98 - topY * h) / 2)           // 垂直居中
            spin = -F(clipTime) * 5
        } else {
            sk = proceduralSkeleton(T)
            H = min(s.height * 0.96, s.width * 1.45)
            spin = -F(figPhase) * 2.4
        }
        // 这个模式不跟拍：动作、位置、图标大小完全照动作数据走。
        // 试过在鼓点上让图标鼓一下、歪一下，瞬间的跳变看起来像抽搐，所以去掉了。
        let origin = CGPoint(x: s.midX, y: s.minY + s.height * 0.02 + lift)
        func scr(_ p: CGPoint) -> CGPoint { origin + p * H }
        let wob: F = clip == nil ? 0.06 * sin(.pi * F(figPhase)) : 0

        // 分配图标：0 号图标当篮球，其余按各部位长度分给 头/躯干/两臂/两腿
        let headR: F = 0.085
        let th = sk.lean
        let head = sk.head, neck = sk.neck, hip = sk.hip, ball = sk.ball
        let weights: [F] = [2 * .pi * headR, 0.30, 0.40, 0.40, 0.43, 0.43]
        let counts = allocate(n - 1, weights)
        let spacing = weights.reduce(0, +) * H / F(n - 1)
        let k = clamp(spacing / iconSize * 1.25, 0.34, 1.0) 
        let sway: F = 0

        var out: [Pose] = []
        out.reserveCapacity(n)
        let ballScale = min(k * 1.7, 1.25)
        out.append(Pose(p: scr(ball), rot: spin, sx: ballScale, sy: ballScale, alpha: sk.ballAlpha, z: 2000))
        // 头：一圈
        let hc = counts[0]
        for j in 0..<hc {
            let a = 2 * F.pi * F(j) / F(hc) + .pi / 2
            let p = hc < 3 ? head : head + CGPoint(x: cos(a), y: sin(a)) * headR
            out.append(Pose(p: scr(p), rot: th * 0.6 + sway, sx: k, sy: k, alpha: 1, z: 900 + F(j)))
        }
        func addLine(_ pts: [CGPoint], _ count: Int, z: F, rot: F) {
            for j in 0..<count {
                let p = samplePolyline(pts, (F(j) + 0.5) / F(count))
                out.append(Pose(p: scr(p), rot: rot + sway, sx: k, sy: k, alpha: 1, z: z + F(j)))
            }
        }
        addLine([neck, hip], counts[1], z: 500, rot: th * 0.6)
        addLine([neck, sk.shP, sk.elbP, sk.handP], counts[2], z: 1000, rot: wob)
        addLine([neck, sk.shM, sk.elbM, sk.handM], counts[3], z: 1100, rot: -wob)
        addLine([sk.hipP, sk.kneeP, sk.footP], counts[4], z: 300, rot: 0)
        addLine([sk.hipM, sk.kneeM, sk.footM], counts[5], z: 200, rot: 0)
        while out.count < n { out.append(Pose(p: scr(hip), sx: k, sy: k)) }
        // out[0] 是球，其余按身体顺序；把球换给 ballIndex 指定的图标
        let bi = min(max(ballIndex, 0), n - 1)
        var result = out
        var src = 1
        for i in 0..<n {
            if i == bi { result[i] = out[0] } else { result[i] = out[src]; src += 1 }
        }
        return result
    }


    /// 没有动作数据时的手写动作：运球 → 过头抛球 → 左右靠，再换另一只手。
    private func proceduralSkeleton(_ T: Tempo) -> Skeleton {
        let e = F(T.energy)
        // 音乐强时按原速，轻时放慢到一半
        figPhase += T.dBeat * Double(lerp(0.5, 1.0, smooth((e - 0.15) / 0.45)))
        let psi = figPhase.truncatingRemainder(dividingBy: 16)
        let second = psi >= 8                       // 后 8 拍：整套动作左右镜像
        let q = F(second ? psi - 8 : psi)
        let sg: F = second ? -1 : 1
        let f = q - floor(q)

        // 以下坐标都以「身高」为单位，地面 y=0，持球的一侧为 +x
        let rb: F = 0.060                           // 球半径
        let yTop: F = 0.385                         // 球在手里时球心高度
        var theta: F = 0.04 * sin(.pi * q)          // 上身倾斜
        var hx: F = 0.012 * sin(.pi * q)            // 胯部横移
        var dip: F = 0.03 * (0.5 - 0.5 * cos(2 * .pi * q))
        var hA = CGPoint(x: 0.26, y: 0.40)          // 持球手
        var hP = CGPoint(x: -0.20, y: 0.33)         // 另一只手
        var ball = CGPoint(x: 0.26, y: yTop)
        if q < 4 {
            // 运球：每拍一下，球在拍子正中落地
            hA = CGPoint(x: 0.26, y: 0.40 + 0.045 * cos(2 * .pi * f))
            ball = CGPoint(x: 0.262, y: rb + (yTop - rb) * abs(cos(.pi * f)))
            hP = CGPoint(x: -0.20 - 0.02 * sin(.pi * q), y: 0.33 + 0.025 * sin(.pi * q))
            theta += 0.06
        } else if q < 6 {
            // 过头抛球：从这只手划一道弧线到另一只手
            let st = (q - 4) / 2
            ball = CGPoint(x: lerp(0.26, -0.26, st), y: yTop + (0.97 - yTop) * 4 * st * (1 - st))
            let under = CGPoint(x: ball.x, y: ball.y - rb * 0.9)
            hA = lerpP(CGPoint(x: 0.21, y: 0.35), under, 1 - smooth((st - 0.22) / 0.2))
            let ready = CGPoint(x: -0.25, y: 0.36 + 0.22 * smooth((st - 0.25) / 0.4))
            hP = lerpP(ready, under, smooth((st - 0.62) / 0.25))
            theta = 0.10 * (1 - 2 * st)
            dip = 0.02
        } else {
            // 左右靠：抱着球，先靠向一边再靠向另一边
            let w = sin(.pi * (q - 6))
            let shift = -0.07 * w
            ball = CGPoint(x: -0.26 + shift, y: yTop)
            hP = CGPoint(x: -0.26 + shift, y: yTop - rb * 0.9)
            hA = CGPoint(x: 0.22 + 0.07 * abs(w) + shift, y: 0.35 + 0.12 * abs(w))
            theta = -0.24 * w
            hx = 0.045 * w
            dip = 0.035 * abs(w)
        }
        // 镜像
        hA.x *= sg; hP.x *= sg; ball.x *= sg; theta *= sg; hx *= sg
        // 手和身体做一点平滑，动作衔接处不突兀；球不平滑，弹地才脆
        let kS: F = figInit ? 1 - exp(-F(T.dt) / 0.06) : 1
        figInit = true
        handA = lerpP(handA, hA, kS); handP = lerpP(handP, hP, kS)
        leanS = lerp(leanS, theta, kS); hipS = lerp(hipS, hx, kS)
        let th = leanS

        let hip = CGPoint(x: hipS, y: 0.40 - dip)
        let up = CGPoint(x: sin(th), y: cos(th))
        let right = CGPoint(x: cos(th), y: -sin(th))
        let neck = hip + up * 0.30
        let head = neck + CGPoint(x: sin(th * 1.3), y: cos(th * 1.3)) * 0.115
        let shPlus = neck + right * 0.09 - up * 0.015
        let shMinus = neck - right * 0.09 - up * 0.015
        let tPlus = sg > 0 ? handA : handP
        let tMinus = sg > 0 ? handP : handA
        let armPlus = ik(shPlus, tPlus, 0.155, 0.155, bend: 1)
        let armMinus = ik(shMinus, tMinus, 0.155, 0.155, bend: -1)
        let hipPlus = hip + CGPoint(x: 0.05, y: 0), hipMinus = hip + CGPoint(x: -0.05, y: 0)
        let footPlus = CGPoint(x: 0.12, y: 0), footMinus = CGPoint(x: -0.12, y: 0)
        let legPlus = ik(hipPlus, footPlus, 0.215, 0.215, bend: 1)
        let legMinus = ik(hipMinus, footMinus, 0.215, 0.215, bend: -1)

        return Skeleton(head: head, neck: neck, hip: hip,
                        shP: shPlus, elbP: armPlus.joint, handP: armPlus.end,
                        shM: shMinus, elbM: armMinus.joint, handM: armMinus.end,
                        hipP: hipPlus, kneeP: legPlus.joint, footP: footPlus,
                        hipM: hipMinus, kneeM: legMinus.joint, footM: footMinus,
                        ball: ball, lean: th)
    }

    // MARK: - 律动：原地压扁、回弹、左右摇摆

    private func grooveSpots(_ n: Int) -> (pts: [CGPoint], cols: Int) {
        if grooveUsesHomes && homes.count >= n { return (Array(homes.prefix(n)), 0) }
        let s = stage
        let k = baseScale
        let cols = max(1, Int((sqrt(F(n) * s.width / max(s.height, 1))).rounded(.up)))
        let rows = Int((F(n) / F(cols)).rounded(.up))
        let cw = min(s.width / F(cols), iconSize * 1.8 * k)
        let ch = min(s.height * 0.86 / F(max(rows, 1)), iconSize * 1.8 * k)
        var pts: [CGPoint] = []
        for i in 0..<n {
            let c = i % cols, r = i / cols
            pts.append(CGPoint(x: s.midX + (F(c) - F(cols - 1) / 2) * cw,
                               y: s.midY + (F(rows - 1) / 2 - F(r)) * ch))
        }
        return (pts, cols)
    }

    private func groove(_ n: Int, _ T: Tempo) -> [Pose] {
        let (pts, cols) = grooveSpots(n)
        let k = baseScale
        let bf = F(T.beat), e = F(T.energy), pulse = F(T.pulse)
        let a = min(amp, 1.6)
        var out: [Pose] = []
        out.reserveCapacity(n)
        for i in 0..<n {
            let col = cols > 0 ? i % cols : Int(pts[i].x / 112)
            let row = cols > 0 ? i / cols : Int(pts[i].y / 108)
            let alt: F = (col + row) % 2 == 0 ? 0 : .pi
            let hop = iconSize * k * 0.24 * amp * (0.4 + e) * abs(sin(.pi * bf))      // 拍子中间腾空，重拍落地
            let sy = 1 - 0.26 * pulse * a                                              // 落地压扁
            let sx = 1 + 0.20 * pulse * a
            var p = pts[i]
            p.y += hop - iconSize * k * 0.5 * (1 - sy)                                  // 压扁时底边贴地
            p.x += iconSize * k * 0.10 * amp * sin(.pi * bf / 2 + F(row) * 0.7)
            let rot = 0.17 * amp * (0.5 + e) * sin(.pi * bf + alt)
            out.append(Pose(p: p, rot: rot, sx: k * sx, sy: k * sy, alpha: 1, z: F(i)))
        }
        return out
    }

    // MARK: - 自由：聚在中央顺时针流动，鼓点一响向外弹开，再弹性收回

    private func free(_ n: Int, _ T: Tempo) -> [Pose] {
        let s = stage
        let k = baseScale
        let bf = F(T.beat), e = F(T.energy), pulse = F(T.pulse)
        if freePos.count != n {
            freePos = (0..<n).map { i in i < homes.count ? homes[i] : CGPoint(x: s.midX, y: s.midY) }
            freeVel = Array(repeating: .zero, count: n)
        }
        let R0 = min(s.width, s.height) * 0.26
        let squashY: F = s.height > s.width ? 1.25 : 0.85
        let dt = F(min(max(T.dt, 0), 1.0 / 30))
        var out: [Pose] = []
        out.reserveCapacity(n)
        for i in 0..<n {
            let r = R0 * (0.30 + 0.70 * sqrt(hash01(i, 11)))
            let omega = -(0.22 + 0.55 * hash01(i, 23))                                  // 负号 = 顺时针
            let ang = 2 * F.pi * hash01(i, 37) + omega * bf
            let burst = 1 + pulse * (0.7 + 1.0 * hash01(i, 53)) * amp * (0.5 + 0.8 * e)
            let target = CGPoint(x: s.midX + r * burst * cos(ang), y: s.midY + r * burst * sin(ang) * squashY)
            if dt > 0 {
                // 欠阻尼弹簧：弹开后会带一点回弹
                let acc = (target - freePos[i]) * 95 - freeVel[i] * 9.5
                freeVel[i] = freeVel[i] + acc * dt
                freePos[i] = freePos[i] + freeVel[i] * dt
            } else {
                freePos[i] = target
            }
            let rot = clamp(freeVel[i].x / 1800, -0.35, 0.35)
            let sc = k * (0.9 + 0.14 * pulse)
            out.append(Pose(p: freePos[i], rot: rot, sx: sc, sy: sc, alpha: 1, z: F(i)))
        }
        return out
    }
}
