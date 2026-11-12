import AppKit

/// 离屏自测：不开窗口，把每个模式在若干拍上的队形画成 PNG（每张 2×2 四格）。
/// 用法：DesktopDance --render-test <输出目录>
enum SelfTest {
    /// 把动作数据按固定间隔渲染成一排小人，用来和原视频同一时刻的画面逐格对比。
    /// 用法：DesktopDance --render-motion <动作.json> <输出.png> [--step 秒] [--first 秒]
    static func renderMotion(json: String, out: String, first: Double, step: Double, cols: Int = 7, rows: Int = 2) {
        guard let clip = MotionClip(url: URL(fileURLWithPath: json)) else { print("动作数据加载失败"); return }
        var loaded = ItemLoader.load(target: 48, scale: 1)
        let tile = CGSize(width: 700, height: 600)
        for i in loaded.items.indices { loaded.items[i].home = CGPoint(x: tile.width / 2, y: tile.height / 2) }
        let items = loaded.items
        let layers = StageLayers(size: tile, scale: 1, items: items)
        layers.setLabelsVisible(false)
        let ch = Choreographer()
        ch.homes = items.map { $0.home }
        ch.stage = CGRect(x: 20, y: 20, width: tile.width - 40, height: tile.height - 40)
        ch.clip = clip
        ch.ballIndex = items.firstIndex { $0.displayName.lowercased().contains("tailscale") } ?? 0
        ch.setMode(.figure, beat: 0, current: items.map { Pose(p: $0.home) })
        let w = Int(tile.width) * cols / 2, h = Int(tile.height) * rows / 2
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
        ctx.setFillColor(CGColor(gray: 0.16, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        let dt = 1.0 / 100
        var t = 0.0
        for k in 0..<(cols * rows) {
            let target = first + step * Double(k)
            var poses: [Pose] = []
            repeat {
                t += dt
                poses = ch.poses(n: items.count, tempo: Tempo(beat: t * 2, dBeat: dt * 2, dt: dt, energy: 0.6, pulse: 0))
            } while t < target
            layers.apply(poses)
            ctx.saveGState()
            ctx.translateBy(x: F(k % cols) * tile.width / 2, y: F(rows - 1 - k / cols) * tile.height / 2)
            ctx.scaleBy(x: 0.5, y: 0.5)
            ctx.setStrokeColor(CGColor(gray: 0.4, alpha: 1))
            ctx.stroke(CGRect(origin: .zero, size: tile), width: 2)
            layers.render(into: ctx)
            ctx.restoreGState()
        }
        print(String(format: "clip %.2fs  maxAbsX %.2f  maxY %.2f  maxRootX %.2f  maxLocalX %.2f", clip.duration, Double(clip.maxAbsX), Double(clip.maxY), Double(clip.maxRootX), Double(clip.maxLocalX)))
        if let img = ctx.makeImage() { print(writePNG(img, to: out) ? "wrote \(out)" : "FAILED") }
    }

    /// 不开窗口的逻辑自检：用造出来的数据检查那些在单台测试机上碰不到的情况。
    /// 用法：DesktopDance --self-check   （全部通过时退出码为 0）
    static func selfCheck() -> Bool {
        var failed = 0
        func check(_ name: String, _ ok: Bool) { print((ok ? "通过  " : "失败  ") + name); if !ok { failed += 1 } }

        // 多显示器：主屏 1920×1080，别的屏幕在它的左、右、上、下
        let main = CGSize(width: 1920, height: 1080)
        check("主屏上的图标上场", ItemLoader.isOnMainScreen(CGPoint(x: 1606, y: 630), size: main))
        check("主屏左上角的图标上场", ItemLoader.isOnMainScreen(CGPoint(x: 40, y: 40), size: main))
        check("左边屏幕上的图标不上场", !ItemLoader.isOnMainScreen(CGPoint(x: -300, y: 500), size: main))
        check("右边屏幕上的图标不上场", !ItemLoader.isOnMainScreen(CGPoint(x: 2500, y: 500), size: main))
        check("上方屏幕上的图标不上场", !ItemLoader.isOnMainScreen(CGPoint(x: 900, y: -400), size: main))
        check("下方屏幕上的图标不上场", !ItemLoader.isOnMainScreen(CGPoint(x: 900, y: 1500), size: main))

        // 选篮球：完全同名优先
        let names = ["网易云音乐", "QQ音乐", "音乐", "Airmail", "Mail"]
        check("选「音乐」不会选中「网易云音乐」", ItemLoader.matchName(names, key: "音乐") == 2)
        check("选「Mail」不会选中「Airmail」", ItemLoader.matchName(names, key: "mail") == 4)
        check("只记得一部分名字也能找到", ItemLoader.matchName(names, key: "网易") == 0)
        check("没选时返回空", ItemLoader.matchName(names, key: "") == nil)
        check("选了没装的应用返回空", ItemLoader.matchName(names, key: "Tailscale") == nil)

        // 动作数据：内置的能读；时间戳全一样的坏文件不会让程序崩
        let clip = MotionClip.loadDefault()
        check("内置动作数据能读出来", clip != nil && (clip?.duration ?? 0) > 5)
        if let clip {
            var finite = true
            for i in 0..<200 {
                let s = clip.sample(clip.duration * 3 * Double(i) / 200)        // 跨过三次循环衔接
                for p in s.joints + [s.ball] where !p.x.isFinite || !p.y.isFinite { finite = false }
            }
            check("动作数据循环三轮，坐标始终是正常数字", finite)
        }
        let bad = FileManager.default.temporaryDirectory.appendingPathComponent("dd-bad-motion-\(ProcessInfo.processInfo.processIdentifier).json")
        let joint = "{\"nose\":[0.5,0.8],\"neck\":[0.5,0.7],\"root\":[0.5,0.4]}"
        let frames = (0..<12).map { _ in "{\"t\":1.0,\"j\":\(joint)}" }.joined(separator: ",")
        try? "{\"fps\":30,\"aspect\":1.0,\"frames\":[\(frames)]}".write(to: bad, atomically: true, encoding: .utf8)
        check("时间戳全相同的动作文件被拒绝而不是崩溃", MotionClip(url: bad) == nil)
        try? FileManager.default.removeItem(at: bad)

        // 四个模式在图标很少、很多时都能排出位置，且都是正常数字
        for n in [1, 5, 8, 20, 48, 96, 140] {
            for mode in DanceMode.allCases {
                let ch = Choreographer()
                ch.homes = (0..<n).map { CGPoint(x: 100 + $0 * 7, y: 100) }
                ch.stage = CGRect(x: 60, y: 60, width: 1200, height: 700)
                ch.clip = clip
                ch.amp = 2
                ch.setMode(mode, beat: 0, current: ch.homes.map { Pose(p: $0) })
                var ok = true
                var beat = 0.0
                for _ in 0..<240 {
                    beat += 1.0 / 30
                    let poses = ch.poses(n: n, tempo: Tempo(beat: beat, dBeat: 1.0 / 30, dt: 1.0 / 60, energy: 0.6, pulse: 0.5))
                    if poses.count != n { ok = false }
                    for p in poses where !p.p.x.isFinite || !p.p.y.isFinite || !p.sx.isFinite || !p.rot.isFinite { ok = false }
                }
                if !ok { check("\(mode.title) 模式，\(n) 个图标", false) }
            }
        }
        check("四个模式 × 1 到 140 个图标：都能排出位置", failed == 0)
        print(failed == 0 ? "全部通过" : "有 \(failed) 项失败")
        return failed == 0
    }

    /// 把一个模式离屏渲染成连续帧（做 README 里的演示动图用）。
    /// 用法：DesktopDance --render-frames <目录> [--mode figure] [--seconds 8] [--fps 15] [--system-apps-only]
    static func renderFrames(outDir: String, mode: DanceMode, seconds: Double, fps: Double) {
        try? FileManager.default.createDirectory(atPath: outDir, withIntermediateDirectories: true)
        let size = CGSize(width: 1280, height: 720)
        let visible = CGRect(origin: .zero, size: size)
        var loaded = ItemLoader.load(target: 48, scale: 1)
        let ballName = CommandLine.arguments.firstIndex(of: "--ball").flatMap { $0 + 1 < CommandLine.arguments.count ? CommandLine.arguments[$0 + 1] : nil } ?? ""
        if !loaded.items.contains(where: { $0.displayName.lowercased().contains(ballName.lowercased()) }),
           let b = ItemLoader.appItem(named: ballName, scale: 1) { loaded.items.append(b) }
        for i in loaded.items.indices { loaded.items[i].home = ItemLoader.gridHome(slot: i, visible: visible) }
        let items = loaded.items
        let layers = StageLayers(size: size, scale: 1, items: items)
        let ch = Choreographer()
        ch.homes = items.map { $0.home }
        ch.clip = MotionClip.loadDefault()
        if let i = CommandLine.arguments.firstIndex(of: "--amp"), i + 1 < CommandLine.arguments.count, let a = Double(CommandLine.arguments[i + 1]) { ch.amp = F(a) }
        ch.ballIndex = (ballName.isEmpty ? nil : items.firstIndex { $0.displayName.lowercased().contains(ballName.lowercased()) })
            ?? items.firstIndex { $0.isGuest } ?? 0
        ch.stage = visible.insetBy(dx: 60, dy: 30)
        ch.setMode(mode, beat: 0, current: items.map { Pose(p: $0.home) })
        layers.setLabelsVisible(false)
        guard let ctx = CGContext(data: nil, width: Int(size.width), height: Int(size.height), bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue),
              let bg = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                                  colors: [CGColor(red: 0.17, green: 0.23, blue: 0.55, alpha: 1), CGColor(red: 0.04, green: 0.05, blue: 0.16, alpha: 1)] as CFArray,
                                  locations: [0, 1]) else { return }
        let dt = 1.0 / 120, dBeat = dt * 2
        var beat = 0.0, pulse = 0.0, t = 0.0, frame = 0
        let warm = mode == .figure ? 0.0 : 4.0          // 列队等模式先跑几秒，跳过开场的飞入
        while t < warm + seconds {
            let prev = beat
            beat += dBeat; t += dt
            if floor(beat) > floor(prev) { pulse = 1 }
            pulse *= exp(-dt * 7)
            let poses = ch.poses(n: items.count, tempo: Tempo(beat: beat, dBeat: dBeat, dt: dt, energy: 0.6, pulse: pulse))
            guard t >= warm, (t - warm) * fps >= Double(frame) else { continue }
            layers.apply(poses)
            ctx.drawLinearGradient(bg, start: CGPoint(x: 0, y: size.height), end: .zero, options: [])
            layers.render(into: ctx)
            if let img = ctx.makeImage() { _ = writePNG(img, to: String(format: "%@/f%04d.png", outDir, frame)) }
            frame += 1
        }
        print("wrote \(frame) frames to \(outDir)")
    }

    static func render(outDir: String) {
        try? FileManager.default.createDirectory(atPath: outDir, withIntermediateDirectories: true)
        let size = CGSize(width: 1920, height: 1080)
        let visible = CGRect(x: 0, y: 67, width: 1920, height: 983)
        var loaded = ItemLoader.load(target: 48, scale: 1)
        for i in loaded.items.indices { loaded.items[i].home = ItemLoader.gridHome(slot: i, visible: visible) }
        let items = loaded.items
        print("items: \(items.count) (desktop \(loaded.desktopCount))")

        let plans: [(DanceMode, [Double])] = [
            (.snake, [2, 7, 14, 30]),
            (.figure, [0.5, 5.0, 6.5, 9.0]),
            (.groove, [0.5, 1.02, 2.5, 3.02]),
            (.free, [0.03, 0.5, 2.03, 3.6]),
        ]
        for portrait in [false, true] {
            for (mode, beats) in plans {
                let layers = StageLayers(size: size, scale: 1, items: items)
                let ch = Choreographer()
                ch.homes = items.map { $0.home }
                ch.grooveUsesHomes = !portrait
                if mode == .figure, let i = CommandLine.arguments.firstIndex(of: "--motion"), i + 1 < CommandLine.arguments.count {
                    ch.clip = MotionClip(url: URL(fileURLWithPath: CommandLine.arguments[i + 1]))
                    print("figure clip: \(ch.clip == nil ? "加载失败" : "ok")")
                }
                var area = visible
                if portrait {
                    let w = (visible.height * 9 / 16).rounded()
                    area = CGRect(x: visible.midX - w / 2, y: visible.minY, width: w, height: visible.height)
                    layers.setGuide(rect: area.insetBy(dx: 1, dy: 1))
                }
                let bandH: F = portrait ? 92 : 60
                layers.layoutLyric(in: CGRect(x: area.minX + 14, y: area.maxY - bandH - 12, width: area.width - 28, height: bandH))
                layers.setLyric(Lyrics.placeholder.text(at: 5), fontSize: portrait ? 30 : 38, animated: false)
                var dance = area.insetBy(dx: portrait ? 30 : 56, dy: 0)
                dance.origin.y += 40
                dance.size.height -= 40 + bandH + 30
                ch.stage = dance
                let home = items.map { Pose(p: $0.home) }
                ch.setMode(mode, beat: 0, current: home)

                let w = Int(size.width), h = Int(size.height)
                guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                          space: CGColorSpaceCreateDeviceRGB(),
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { continue }
                ctx.setFillColor(CGColor(gray: 0.16, alpha: 1))
                ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))

                // 以 120 BPM、60fps 模拟
                let dt = 1.0 / 60, dBeat = dt * 2
                var beat = 0.0
                var pulse = 0.0
                var shot = 0
                let safe = visible.insetBy(dx: 22, dy: 22)
                while shot < beats.count {
                    let prev = beat
                    beat += dBeat
                    if floor(beat) > floor(prev) { pulse = 1 }
                    pulse *= exp(-dt * 7)
                    var poses = ch.poses(n: items.count, tempo: Tempo(beat: beat, dBeat: dBeat, dt: dt, energy: 0.6, pulse: pulse))
                    if beat >= beats[shot] {
                        for i in poses.indices {
                            poses[i].p.x = clamp(poses[i].p.x, safe.minX, safe.maxX)
                            poses[i].p.y = clamp(poses[i].p.y, safe.minY, safe.maxY)
                        }
                        layers.apply(poses)
                        ctx.saveGState()
                        ctx.translateBy(x: F(shot % 2) * size.width / 2, y: F(1 - shot / 2) * size.height / 2)
                        ctx.scaleBy(x: 0.5, y: 0.5)
                        ctx.setStrokeColor(CGColor(gray: 0.4, alpha: 1))
                        ctx.stroke(CGRect(origin: .zero, size: size), width: 4)
                        layers.render(into: ctx)
                        ctx.restoreGState()
                        shot += 1
                    }
                }
                if let img = ctx.makeImage() {
                    let path = "\(outDir)/\(portrait ? "portrait" : "full")-\(mode)-beats.png"
                    print(writePNG(img, to: path) ? "wrote \(path)" : "FAILED \(path)")
                }
            }
        }
    }
}
