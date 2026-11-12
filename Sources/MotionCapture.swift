import AppKit
import AVFoundation
import Vision
import CoreImage

/// 动作数据文件：每帧 17 个关节的位置。坐标是画面归一化坐标（0...1，原点左下，y 向上）。
struct MotionFile: Codable {
    struct Frame: Codable {
        var t: Double
        var j: [String: [Double]]     // 关节名 -> [x, y, 置信度]
        var ball: [Double]?           // [x, y, 半径, 置信度]
    }
    var fps: Double
    var aspect: Double                // 画面宽 / 高
    var cuts: [Double]? = nil         // 镜头切换的时刻（新镜头第一帧的 t）
    var frames: [Frame]
}

enum Joint: Int, CaseIterable {
    case nose, neck, lsho, rsho, lelb, relb, lwri, rwri, root, lhip, rhip, lkne, rkne, lank, rank, lear, rear

    var key: String { String(describing: self) }

    var vision: VNHumanBodyPoseObservation.JointName {
        switch self {
        case .nose: return .nose
        case .neck: return .neck
        case .lsho: return .leftShoulder
        case .rsho: return .rightShoulder
        case .lelb: return .leftElbow
        case .relb: return .rightElbow
        case .lwri: return .leftWrist
        case .rwri: return .rightWrist
        case .root: return .root
        case .lhip: return .leftHip
        case .rhip: return .rightHip
        case .lkne: return .leftKnee
        case .rkne: return .rightKnee
        case .lank: return .leftAnkle
        case .rank: return .rightAnkle
        case .lear: return .leftEar
        case .rear: return .rightEar
        }
    }
}

/// 从视频里逐帧提取人体关节（系统自带的 Vision 人体姿态识别），外加一个按颜色找篮球的简单检测。
/// 用法：DesktopDance --extract-pose <视频> <输出.json> [--sheet <检查图.png>] [--from 秒] [--to 秒]
enum MotionExtractor {
    static func orientation(for t: CGAffineTransform) -> CGImagePropertyOrientation {
        if t.a == 0, t.b == 1, t.c == -1, t.d == 0 { return .right }
        if t.a == 0, t.b == -1, t.c == 1, t.d == 0 { return .left }
        if t.a == -1, t.d == -1 { return .down }
        return .up
    }

    /// 在缩小后的 RGBA 画面里找「篮球橙」色块。只接受半径在 minR...maxR（像素）之间、形状接近圆的；
    /// 返回归一化的 [x, y(向上), 半径(以画面高为 1), 置信度]。
    static func findBall(rgba: [UInt8], w: Int, h: Int, minR: Double, maxR: Double) -> [Double]? {
        var mask = [Bool](repeating: false, count: w * h)
        for y in 0..<h {
            for x in 0..<w {
                let i = (y * w + x) * 4
                let r = Double(rgba[i]), g = Double(rgba[i + 1]), b = Double(rgba[i + 2])
                // 篮球是偏红的橙棕色：红明显高于绿和蓝。肤色的红/绿比低得多，不会被选中。
                if r > 80, r > g * 1.45, r > b * 1.5, r - g > 35 { mask[y * w + x] = true }
            }
        }
        var seen = [Bool](repeating: false, count: w * h)
        var best: [Double]?
        var bestScore = 0.0
        var stack: [Int] = []
        for start in 0..<(w * h) where mask[start] && !seen[start] {
            stack.removeAll(keepingCapacity: true)
            stack.append(start)
            seen[start] = true
            var area = 0, sx = 0, sy = 0, minX = w, maxX = 0, minY = h, maxY = 0
            while let p = stack.popLast() {
                let px = p % w, py = p / w
                area += 1; sx += px; sy += py
                minX = min(minX, px); maxX = max(maxX, px); minY = min(minY, py); maxY = max(maxY, py)
                for (dx, dy) in [(1, 0), (-1, 0), (0, 1), (0, -1)] {
                    let nx = px + dx, ny = py + dy
                    guard nx >= 0, nx < w, ny >= 0, ny < h else { continue }
                    let q = ny * w + nx
                    if mask[q] && !seen[q] { seen[q] = true; stack.append(q) }
                }
            }
            guard area >= 10 else { continue }
            let bw = Double(maxX - minX + 1), bh = Double(maxY - minY + 1)
            let radius = max(bw, bh) / 2
            guard radius >= minR, radius <= maxR else { continue }
            let fill = Double(area) / (bw * bh)                     // 圆形约 0.785
            let squareness = min(bw, bh) / max(bw, bh)
            let conf = max(0, min(1, (fill - 0.4) / 0.3)) * max(0, min(1, (squareness - 0.45) / 0.3))
            let score = Double(area) * conf
            if score > bestScore {
                bestScore = score
                best = [(Double(minX) + bw / 2) / Double(w), 1 - (Double(minY) + bh / 2) / Double(h), radius / Double(h), conf]
            }
        }
        return best
    }

    static func extract(video: String, out: String, sheet: String?, from: Double, to: Double, maxFPS: Double = 30) -> Bool {
        let asset = AVURLAsset(url: URL(fileURLWithPath: video))
        guard let track = asset.tracks(withMediaType: .video).first,
              let reader = try? AVAssetReader(asset: asset) else {
            print("打不开视频：\(video)")
            return false
        }
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
        ])
        output.alwaysCopiesSampleData = false
        reader.add(output)
        let duration = asset.duration.seconds
        let end = min(to, duration)
        if from > 0 || end < duration {
            reader.timeRange = CMTimeRange(start: CMTime(seconds: from, preferredTimescale: 600),
                                           end: CMTime(seconds: end, preferredTimescale: 600))
        }
        guard reader.startReading() else { print("读不了视频：\(reader.error?.localizedDescription ?? "?")"); return false }

        let orient = orientation(for: track.preferredTransform)
        let nat = track.naturalSize
        let rotated = orient == .left || orient == .right
        let aspect = rotated ? Double(nat.height / nat.width) : Double(nat.width / nat.height)
        let ci = CIContext()
        let smallH = 180
        let smallW = max(2, Int((Double(smallH) * aspect).rounded()))
        let expected = max(1, Int((end - from) * min(maxFPS, Double(track.nominalFrameRate > 0 ? track.nominalFrameRate : 30))))
        let tilesWanted = 28
        let tileEvery = max(1, expected / tilesWanted)
        var tiles: [CGImage] = []

        var file = MotionFile(fps: maxFPS, aspect: aspect, frames: [])
        var nextT = -1.0
        var index = 0
        var detected = 0, ballSeen = 0
        var prevGrid: [Double] = []
        var diffs: [(t: Double, d: Double)] = []
        while let sb = output.copyNextSampleBuffer() {
            let t = CMSampleBufferGetPresentationTimeStamp(sb).seconds
            guard t >= nextT, let pb = CMSampleBufferGetImageBuffer(sb) else { continue }
            nextT = t + 1 / maxFPS - 0.002

            var frame = MotionFile.Frame(t: t - from, j: [:], ball: nil)
            let req = VNDetectHumanBodyPoseRequest()
            let handler = VNImageRequestHandler(cvPixelBuffer: pb, orientation: orient, options: [:])
            try? handler.perform([req])
            // 画面里可能不止一个人：取关节最全、个头最大的那个
            var bestObs: [VNHumanBodyPoseObservation.JointName: VNRecognizedPoint]?
            var bestScore = 0.0
            for obs in req.results ?? [] {
                guard let pts = try? obs.recognizedPoints(.all) else { continue }
                let good = pts.values.filter { $0.confidence > 0.3 }
                guard good.count >= 6 else { continue }
                let ys = good.map { Double($0.location.y) }
                let score = Double(good.count) + ((ys.max() ?? 0) - (ys.min() ?? 0)) * 20
                if score > bestScore { bestScore = score; bestObs = pts }
            }
            if let pts = bestObs {
                detected += 1
                for jn in Joint.allCases {
                    if let p = pts[jn.vision] { frame.j[jn.key] = [Double(p.location.x), Double(p.location.y), Double(p.confidence)] }
                }
            }

            // 缩小成 RGBA 找球，同时给检查图留底
            let img = CIImage(cvPixelBuffer: pb).oriented(orient)
            let sx = F(smallW) / img.extent.width, sy = F(smallH) / img.extent.height
            let scaled = img.transformed(by: CGAffineTransform(translationX: -img.extent.minX, y: -img.extent.minY))
                .transformed(by: CGAffineTransform(scaleX: sx, y: sy))
            var small: CGImage?
            if let cg = ci.createCGImage(scaled, from: CGRect(x: 0, y: 0, width: smallW, height: smallH)) {
                small = cg
                var buf = [UInt8](repeating: 0, count: smallW * smallH * 4)
                if let ctx = CGContext(data: &buf, width: smallW, height: smallH, bitsPerComponent: 8, bytesPerRow: smallW * 4,
                                       space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) {
                    ctx.draw(cg, in: CGRect(x: 0, y: 0, width: smallW, height: smallH))
                    // 用躯干长度估计球该有多大（篮球半径约为躯干长的 1/4），过滤掉大小不对的色块
                    var torsoPx = Double(smallH) * 0.2
                    if let nk = frame.j["neck"], let rt = frame.j["root"], nk[2] > 0.3, rt[2] > 0.3 {
                        torsoPx = hypot((nk[0] - rt[0]) * Double(smallW), (nk[1] - rt[1]) * Double(smallH))
                    }
                    if let b = findBall(rgba: buf, w: smallW, h: smallH, minR: torsoPx * 0.13, maxR: torsoPx * 0.50) {
                        frame.ball = b
                        if b[3] > 0.3 { ballSeen += 1 }
                    }
                    // 镜头切换检测：把画面分成 16×9 格，比较相邻两帧每格的平均亮度
                    var grid = [Double](repeating: 0, count: 16 * 9)
                    let cw = smallW / 16, chh = smallH / 9
                    if cw > 0, chh > 0 {
                        for gy in 0..<9 { for gx in 0..<16 {
                            var sum = 0.0
                            for y in stride(from: gy * chh, to: (gy + 1) * chh, by: 2) { for x in stride(from: gx * cw, to: (gx + 1) * cw, by: 2) {
                                let k = (y * smallW + x) * 4
                                sum += Double(buf[k]) * 0.3 + Double(buf[k + 1]) * 0.6 + Double(buf[k + 2]) * 0.1
                            } }
                            grid[gy * 16 + gx] = sum / Double(max(1, (chh / 2) * (cw / 2)))
                        } }
                        if prevGrid.count == grid.count {
                            var dsum = 0.0
                            for k in 0..<grid.count { dsum += abs(grid[k] - prevGrid[k]) }
                            diffs.append((frame.t, dsum / Double(grid.count) / 255))
                        }
                        prevGrid = grid
                    }
                }
            }
            if sheet != nil, index % tileEvery == 0, tiles.count < 40, let small {
                tiles.append(drawTile(image: small, frame: frame, w: smallW, h: smallH))
            }
            file.frames.append(frame)
            index += 1
        }
        guard !file.frames.isEmpty else { print("视频里没有读到画面"); return false }
        // 画面突变明显大于平时（人在动但机位不动时差别很小）就算镜头切换
        let sortedD = diffs.map { $0.d }.sorted()
        let med = sortedD.isEmpty ? 0 : sortedD[sortedD.count / 2]
        let cutThreshold = max(0.035, med * 6)
        file.cuts = diffs.filter { $0.d > cutThreshold }.map { $0.t }
        print("镜头切换（片内秒）：\(file.cuts!.map { String(format: "%.2f", $0) }.joined(separator: ", "))  阈值 \(String(format: "%.3f", cutThreshold))  平时 \(String(format: "%.3f", med))")
        do {
            try JSONEncoder().encode(file).write(to: URL(fileURLWithPath: out), options: .atomic)
        } catch {
            print("写不了 \(out)：\(error.localizedDescription)")
            return false
        }
        print("帧数 \(file.frames.count)，识别到人体 \(detected) 帧，找到篮球 \(ballSeen) 帧，时长 \(String(format: "%.2f", (file.frames.last?.t ?? 0)))s，已写入 \(out)")
        if let sheet, !tiles.isEmpty {
            let cols = 7, rows = (tiles.count + cols - 1) / cols
            if let ctx = CGContext(data: nil, width: cols * smallW, height: rows * smallH, bitsPerComponent: 8, bytesPerRow: 0,
                                   space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) {
                for (i, tile) in tiles.enumerated() {
                    ctx.draw(tile, in: CGRect(x: (i % cols) * smallW, y: (rows - 1 - i / cols) * smallH, width: smallW, height: smallH))
                }
                if let img = ctx.makeImage() { print(writePNG(img, to: sheet) ? "检查图 \(sheet)" : "检查图写入失败") }
            }
        }
        return true
    }

    /// 在缩略图上画出识别到的骨架和篮球，用来人工核对识别得准不准。
    private static func drawTile(image: CGImage, frame: MotionFile.Frame, w: Int, h: Int) -> CGImage {
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return image }
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        func pt(_ j: Joint) -> CGPoint? {
            guard let v = frame.j[j.key], v[2] > 0.2 else { return nil }
            return CGPoint(x: v[0] * Double(w), y: v[1] * Double(h))
        }
        let bones: [(Joint, Joint)] = [(.nose, .neck), (.neck, .lsho), (.lsho, .lelb), (.lelb, .lwri), (.neck, .rsho), (.rsho, .relb),
                                       (.relb, .rwri), (.neck, .root), (.root, .lhip), (.lhip, .lkne), (.lkne, .lank),
                                       (.root, .rhip), (.rhip, .rkne), (.rkne, .rank)]
        ctx.setLineWidth(2)
        for (a, b) in bones {
            guard let p = pt(a), let q = pt(b) else { continue }
            let left = a.key.hasPrefix("l") || b.key.hasPrefix("l")
            ctx.setStrokeColor(left ? CGColor(red: 0.2, green: 1, blue: 0.3, alpha: 1) : CGColor(red: 1, green: 0.9, blue: 0.1, alpha: 1))
            ctx.move(to: p); ctx.addLine(to: q); ctx.strokePath()
        }
        if let b = frame.ball, b[3] > 0.3 {
            let r = b[2] * Double(h)
            ctx.setStrokeColor(CGColor(red: 0, green: 0.8, blue: 1, alpha: 1))
            ctx.strokeEllipse(in: CGRect(x: b[0] * Double(w) - r, y: b[1] * Double(h) - r, width: r * 2, height: r * 2))
        }
        return ctx.makeImage() ?? image
    }
}

/// 处理好的动作片段：按镜头分段补洞、平滑，换算成「以身高为单位、脚下为原点」的坐标，供小人回放。
final class MotionClip {
    let duration: Double
    private let dt: Double
    private let count: Int
    private var tracks: [[CGPoint]] = []          // [关节][帧]
    private var ballTrack: [CGPoint] = []
    private var ballVisible: [F] = []             // 0...1
    private var cutFrames: [Int] = []             // 每个镜头的第一帧（不含第 0 帧）
    private(set) var maxAbsX: F = 0.5
    private(set) var maxY: F = 1.0
    private(set) var maxRootX: F = 0          // 整个人左右走位的最大幅度
    private(set) var maxLocalX: F = 0.3       // 肢体（和球）离身体中线最远多少

    static var defaultURLs: [URL] {
        var urls = [AppPaths.supportDir.appendingPathComponent("basketball-motion.json")]
        if let u = Bundle.main.url(forResource: "basketball-motion", withExtension: "json") { urls.append(u) }
        return urls
    }

    static func loadDefault() -> MotionClip? {
        for u in defaultURLs { if let c = MotionClip(url: u) { return c } }
        return nil
    }

    init?(url: URL) {
        guard let data = try? Data(contentsOf: url), let file = try? JSONDecoder().decode(MotionFile.self, from: data),
              file.frames.count >= 8 else { return nil }
        let n = file.frames.count
        count = n
        let total = file.frames.last!.t - file.frames.first!.t
        guard total > 0.05 else { return nil }          // 时间戳全一样或乱序的文件：当作没有动作数据
        dt = total / Double(n - 1)
        duration = total + dt
        let aspect = F(file.aspect)

        // 镜头分段
        var starts = [0]
        for c in (file.cuts ?? []).sorted() {
            if let i = file.frames.firstIndex(where: { $0.t >= c - 1e-6 }), i - starts.last! >= 5, n - i >= 5 { starts.append(i) }
        }
        cutFrames = Array(starts.dropFirst())
        let segs: [Range<Int>] = starts.indices.map { starts[$0]..<(($0 + 1 < starts.count) ? starts[$0 + 1] : n) }

        // 在一个镜头内，把置信度低的帧用前后好帧线性补上；整段都没有好帧就返回 nil
        func fill(_ pts: [CGPoint?], _ seg: Range<Int>, maxGap: Int = .max) -> [CGPoint?] {
            var out = pts
            let good = seg.filter { pts[$0] != nil }
            guard !good.isEmpty else { return out }
            for i in seg where pts[i] == nil {
                let before = good.last(where: { $0 < i }), after = good.first(where: { $0 > i })
                if let a = before, let b = after {
                    if b - a - 1 <= maxGap { out[i] = lerpP(pts[a]!, pts[b]!, F(i - a) / F(b - a)) }
                } else if maxGap == .max {
                    out[i] = pts[(before ?? after)!]
                }
            }
            return out
        }
        func smoothed(_ t: [CGPoint], _ seg: Range<Int>, radius: Int) -> [CGPoint] {
            var out = t
            guard radius > 0 else { return out }
            for i in seg {
                var sx: F = 0, sy: F = 0, wsum: F = 0
                for k in -radius...radius {
                    let j = min(max(i + k, seg.lowerBound), seg.upperBound - 1)
                    let w = F(radius + 1 - abs(k))
                    sx += t[j].x * w; sy += t[j].y * w; wsum += w
                }
                out[i] = CGPoint(x: sx / wsum, y: sy / wsum)
            }
            return out
        }
        func raw(_ jn: Joint) -> [CGPoint?] {
            var pts: [CGPoint?] = file.frames.map { f in
                guard let v = f.j[jn.key], v.count >= 3, v[2] > 0.25 else { return nil }
                return CGPoint(x: v[0] * Double(aspect), y: v[1])
            }
            for seg in segs { pts = fill(pts, seg) }
            return pts
        }
        var J: [Joint: [CGPoint?]] = [:]
        for jn in Joint.allCases { J[jn] = raw(jn) }
        guard let neckO = J[.neck], let rootO = J[.root], !neckO.contains(where: { $0 == nil }), !rootO.contains(where: { $0 == nil }) else { return nil }
        let neck = neckO.map { $0! }, root = rootO.map { $0! }

        // 人离镜头的远近会变（他会朝镜头走过来）。逐帧用骨骼长度估计「他在画面里有多高」：
        // 骨头正对镜头时会显得短、侧过来才是真实长度，所以取一小段时间里偏大的值，再平滑。
        // 这样小人大小保持恒定，不会忽大忽小。
        let boneRefs: [(Joint, Joint, F)] = [
            (.neck, .root, 0.29), (.lhip, .lkne, 0.245), (.rhip, .rkne, 0.245), (.lkne, .lank, 0.246), (.rkne, .rank, 0.246),
            (.lsho, .lelb, 0.172), (.rsho, .relb, 0.172), (.lelb, .lwri, 0.157), (.relb, .rwri, 0.157), (.lsho, .rsho, 0.20),
        ]
        var inst = [F](repeating: 0, count: n)
        for i in 0..<n {
            var ratios: [F] = []
            for (ja, jb, ref) in boneRefs {
                if let p = J[ja]![i], let q = J[jb]![i] { ratios.append(hypot(p.x - q.x, p.y - q.y) / ref) }
            }
            ratios.sort(by: >)
            inst[i] = ratios.count >= 3 ? ratios[2] : (ratios.last ?? 0.5)      // 取第三大的，避开个别识别错的骨头
        }
        let frameDT = total / Double(n - 1)
        let win = max(1, Int((1.0 / frameDT).rounded()))
        func boxSmooth(_ v: [F], _ r: Int) -> [F] {
            v.indices.map { i in
                let w = v[max(0, i - r)...min(v.count - 1, i + r)]
                return w.reduce(0, +) / F(w.count)
            }
        }
        var unit: [F] = (0..<n).map { i in
            let w = inst[max(0, i - win)...min(n - 1, i + win)].sorted()
            return max(w[min(w.count - 1, w.count * 8 / 10)], 0.02)
        }
        unit = boxSmooth(unit, win / 2)
        // 缺失的关节用相邻关节推出来
        func or(_ j: Joint, _ fb: (Int) -> CGPoint) -> [CGPoint] { (0..<n).map { J[j]![$0] ?? fb($0) } }
        let lsho = or(.lsho) { CGPoint(x: neck[$0].x + unit[$0] * 0.09, y: neck[$0].y) }
        let rsho = or(.rsho) { CGPoint(x: neck[$0].x - unit[$0] * 0.09, y: neck[$0].y) }
        let lelb = or(.lelb) { CGPoint(x: lsho[$0].x, y: lsho[$0].y - unit[$0] * 0.15) }
        let relb = or(.relb) { CGPoint(x: rsho[$0].x, y: rsho[$0].y - unit[$0] * 0.15) }
        let lwri = or(.lwri) { CGPoint(x: lelb[$0].x, y: lelb[$0].y - unit[$0] * 0.15) }
        let rwri = or(.rwri) { CGPoint(x: relb[$0].x, y: relb[$0].y - unit[$0] * 0.15) }
        let lhip = or(.lhip) { CGPoint(x: root[$0].x + unit[$0] * 0.05, y: root[$0].y) }
        let rhip = or(.rhip) { CGPoint(x: root[$0].x - unit[$0] * 0.05, y: root[$0].y) }
        let lkne = or(.lkne) { CGPoint(x: lhip[$0].x, y: lhip[$0].y - unit[$0] * 0.21) }
        let rkne = or(.rkne) { CGPoint(x: rhip[$0].x, y: rhip[$0].y - unit[$0] * 0.21) }
        let lank = or(.lank) { CGPoint(x: lkne[$0].x, y: lkne[$0].y - unit[$0] * 0.21) }
        let rank = or(.rank) { CGPoint(x: rkne[$0].x, y: rkne[$0].y - unit[$0] * 0.21) }
        let nose = or(.nose) { CGPoint(x: neck[$0].x, y: neck[$0].y + unit[$0] * 0.11) }
        let lear = or(.lear) { nose[$0] }
        let rear = or(.rear) { nose[$0] }
        var all: [Joint: [CGPoint]] = [.nose: nose, .neck: neck, .lsho: lsho, .rsho: rsho, .lelb: lelb, .relb: relb, .lwri: lwri,
                                       .rwri: rwri, .root: root, .lhip: lhip, .rhip: rhip, .lkne: lkne, .rkne: rkne,
                                       .lank: lank, .rank: rank, .lear: lear, .rear: rear]

        // 篮球：短暂丢失（≤ 8 帧）插值补上；长时间没有就算「球不在了」
        var ballRaw: [CGPoint?] = file.frames.map { f in
            guard let b = f.ball, b.count >= 4, b[3] > 0.3 else { return nil }
            return CGPoint(x: b[0] * Double(aspect), y: b[1])
        }
        for seg in segs { ballRaw = fill(ballRaw, seg, maxGap: 8) }
        var vis = ballRaw.map { $0 == nil ? F(0) : F(1) }
        var ball: [CGPoint] = (0..<n).map { ballRaw[$0] ?? CGPoint(x: rwri[$0].x, y: rwri[$0].y - unit[$0] * 0.07) }
        // 球不在时的占位：沿用最后一次看到的位置，淡出时不会瞬移
        var lastSeen: CGPoint?
        for i in 0..<n { if ballRaw[i] != nil { lastSeen = ball[i] } else if let l = lastSeen { ball[i] = l } }
        // 可见性做 4 帧的淡入淡出
        vis = vis.indices.map { i in
            var s: F = 0
            for k in -2...2 { s += vis[min(max(i + k, 0), n - 1)] }
            return s / 5
        }

        // 平滑（手腕脚踝动得快，少平滑一点，保住动作的脆劲），然后每个镜头各自换算坐标
        for jn in Joint.allCases {
            let quick = [.lwri, .rwri, .lank, .rank].contains(jn)
            for seg in segs { all[jn] = smoothed(all[jn]!, seg, radius: quick ? 1 : 2) }
        }
        for seg in segs { ball = smoothed(ball, seg, radius: 1) }
        // 地面：一小段时间里脚踝的最低处（跳起来时地面不跟着上去），再平滑
        let ankleMin: [F] = (0..<n).map { min(all[.lank]![$0].y, all[.rank]![$0].y) }
        var ground: [F] = (0..<n).map { i in ankleMin[max(0, i - win)...min(n - 1, i + win)].min()! }
        ground = boxSmooth(ground, win / 2)
        let centerX = root.map { $0.x }.sorted()[n / 2]
        var out = Joint.allCases.map { all[$0]! }
        for i in 0..<n {
            let u = unit[i], gy = ground[i] - u * 0.03
            for k in out.indices { out[k][i] = CGPoint(x: (out[k][i].x - centerX) / u, y: (out[k][i].y - gy) / u) }
            ball[i] = CGPoint(x: (ball[i].x - centerX) / u, y: (ball[i].y - gy) / u)
        }
        // 校准：让「站直时的头顶」正好是 1 个身高（骨骼估计会有系统偏差）
        func pct(_ v: [F], _ p: Double) -> F { let srt = v.sorted(); return srt[min(srt.count - 1, Int(Double(srt.count) * p))] }
        let headY: [F] = (0..<n).map { (out[Joint.nose.rawValue][$0].y + out[Joint.lear.rawValue][$0].y + out[Joint.rear.rawValue][$0].y) / 3 }
        let standing = pct(headY, 0.9) + 0.085
        if standing > 0.3 {
            let c = 1 / standing
            for k in out.indices { for i in 0..<n { out[k][i] = out[k][i] * c } }
            for i in 0..<n { ball[i] = ball[i] * c }
        }
        tracks = out
        ballTrack = ball
        ballVisible = vis

        // 舞台缩放用的幅度。身体的最高点取真实最大值（跳起来不能出框）；
        // 横向和球取常态幅度（偶尔甩出去一点由外层的安全边界兜住），这样小人能画得更大。
        let rootX = tracks[Joint.root.rawValue]
        var topY: [F] = [], localX: [F] = [], absX: [F] = []
        var rx: F = 0
        for i in 0..<n {
            var ty: F = 0, lx: F = 0, ax: F = 0
            for t in tracks { ty = max(ty, t[i].y); lx = max(lx, abs(t[i].x - rootX[i].x)); ax = max(ax, abs(t[i].x)) }
            if vis[i] > 0.5 { lx = max(lx, abs(ball[i].x - rootX[i].x)); ax = max(ax, abs(ball[i].x)) }
            topY.append(ty); localX.append(lx); absX.append(ax)
            rx = max(rx, abs(rootX[i].x))
        }
        maxY = max(topY.max() ?? 1, 1.0) + 0.10
        maxAbsX = pct(absX, 0.97) + 0.08
        maxLocalX = pct(localX, 0.97) + 0.08
        maxRootX = rx
    }

    typealias Sample = (joints: [CGPoint], ball: CGPoint, ballAlpha: F)

    private func frame(_ f: Double) -> Sample {
        let ff = min(max(f, 0), Double(count - 1))
        let i = Int(ff)
        var j = min(i + 1, count - 1)
        if cutFrames.contains(j) { j = i }                  // 不跨镜头插值
        let k = F(ff - Double(i))
        return (tracks.map { lerpP($0[i], $0[j], k) }, lerpP(ballTrack[i], ballTrack[j], k), lerp(ballVisible[i], ballVisible[j], k))
    }

    private func blend(_ a: Sample, _ b: Sample, _ w: F) -> Sample {
        // 球：哪一边看不见，就直接用看得见那一边的位置（否则球会从被抛走的地方飘回来）
        let ball = a.ballAlpha < 0.05 ? b.ball : (b.ballAlpha < 0.05 ? a.ball : lerpP(a.ball, b.ball, w))
        return (zip(a.joints, b.joints).map { lerpP($0, $1, w) }, ball, lerp(a.ballAlpha, b.ballAlpha, w))
    }

    /// 取 t 秒时的姿态。循环播放；镜头切换处和首尾相接处用 0.2 秒过渡，看起来是一个快动作而不是瞬移。
    func sample(_ time: Double) -> Sample {
        let t = time.truncatingRemainder(dividingBy: duration)
        let f = t / dt
        let cur = frame(f)
        let blendFrames = 0.2 / dt
        if let c = cutFrames.last(where: { Double($0) <= f }), f - Double(c) < blendFrames {
            return blend(frame(Double(c - 1)), cur, smooth(F((f - Double(c)) / blendFrames)))
        }
        if t > duration - 0.2 {
            return blend(cur, frame(0), smooth(F((t - (duration - 0.2)) / 0.2)))
        }
        return cur
    }
}
