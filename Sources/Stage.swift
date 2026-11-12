import AppKit
import QuartzCore

/// 舞台的图层树：遮罩（可选）+ 图标副本 + 9:16 参考框 + 歌词。
/// 与窗口解耦，所以也能离屏渲染成 PNG 做自测。
final class StageLayers {
    let root = CALayer()
    let curtain = CALayer()
    let icons = CALayer()
    let guide = CAShapeLayer()
    let lyric = CATextLayer()
    private(set) var iconLayers: [CALayer] = []
    private(set) var labelLayers: [CALayer?] = []
    private var lyricText = ""
    private let noActions: [String: CAAction] = [
        "position": NSNull(), "bounds": NSNull(), "transform": NSNull(), "opacity": NSNull(),
        "contents": NSNull(), "hidden": NSNull(), "zPosition": NSNull(), "path": NSNull(),
    ]

    init(size: CGSize, scale: F, items: [DancerItem], bystanders: [DancerItem] = []) {
        root.frame = CGRect(origin: .zero, size: size)
        root.actions = noActions

        curtain.frame = root.bounds
        curtain.contentsGravity = .resizeAspectFill
        curtain.masksToBounds = true
        curtain.isHidden = true
        curtain.actions = noActions
        root.addSublayer(curtain)

        icons.frame = root.bounds
        icons.actions = noActions
        root.addSublayer(icons)

        let s = ItemLoader.iconPoints
        // 不跳舞的桌面图标：真实图标被遮罩盖住了，所以在原位画一份不动的副本
        for it in bystanders {
            let l = CALayer()
            l.bounds = CGRect(x: 0, y: 0, width: s, height: s)
            l.contents = it.icon
            l.contentsGravity = .resizeAspect
            l.contentsScale = scale
            l.actions = noActions
            l.position = it.home
            l.zPosition = -1
            if let lab = it.label {
                let t = CALayer()
                t.bounds = CGRect(origin: .zero, size: it.labelSize)
                t.anchorPoint = CGPoint(x: 0.5, y: 1)
                t.position = CGPoint(x: s / 2, y: -1)
                t.contents = lab
                t.contentsScale = scale
                t.actions = noActions
                l.addSublayer(t)
            }
            icons.addSublayer(l)
        }
        for it in items {
            let l = CALayer()
            l.bounds = CGRect(x: 0, y: 0, width: s, height: s)
            l.contents = it.icon
            l.contentsGravity = .resizeAspect
            l.contentsScale = scale
            l.actions = noActions
            l.position = it.home
            l.opacity = 0
            if let lab = it.label {
                let t = CALayer()
                t.bounds = CGRect(origin: .zero, size: it.labelSize)
                t.anchorPoint = CGPoint(x: 0.5, y: 1)
                t.position = CGPoint(x: s / 2, y: -1)
                t.contents = lab
                t.contentsScale = scale
                t.actions = noActions
                l.addSublayer(t)
                labelLayers.append(t)
            } else {
                labelLayers.append(nil)
            }
            icons.addSublayer(l)
            iconLayers.append(l)
        }

        guide.frame = root.bounds
        guide.fillColor = nil
        guide.strokeColor = NSColor.white.withAlphaComponent(0.55).cgColor
        guide.lineWidth = 2
        guide.isHidden = true
        guide.actions = noActions
        root.addSublayer(guide)

        lyric.isWrapped = true
        lyric.alignmentMode = .center
        lyric.contentsScale = scale
        lyric.shadowColor = NSColor.black.cgColor
        lyric.shadowOpacity = 0.9
        lyric.shadowRadius = 5
        lyric.shadowOffset = CGSize(width: 0, height: -1)
        lyric.actions = noActions
        lyric.zPosition = 10_000
        root.addSublayer(lyric)
    }

    func apply(_ poses: [Pose]) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for (i, l) in iconLayers.enumerated() where i < poses.count {
            let q = poses[i]
            l.position = q.p
            l.setAffineTransform(CGAffineTransform(rotationAngle: q.rot).scaledBy(x: q.sx, y: q.sy))
            l.opacity = Float(q.alpha)
            l.zPosition = q.z
        }
        CATransaction.commit()
    }

    /// hiddenIndex：即使显示名称，也把这个图标的名字藏起来（当篮球的那个，不然名字跟着球一起转）。
    func setLabelsVisible(_ on: Bool, hiddenIndex: Int? = nil) {
        CATransaction.begin(); CATransaction.setDisableActions(true)
        for (i, t) in labelLayers.enumerated() { t?.isHidden = !on || i == hiddenIndex }
        CATransaction.commit()
    }

    /// 有图片就盖壁纸；没有图片但必须盖住真实图标时，退回暗色舞台。
    func setCurtain(image: CGImage?, visible: Bool) {
        CATransaction.begin(); CATransaction.setDisableActions(true)
        curtain.isHidden = !visible
        curtain.contents = image
        curtain.backgroundColor = image == nil ? NSColor(calibratedWhite: 0.07, alpha: 1).cgColor : nil
        CATransaction.commit()
    }

    /// 画 9:16 取景框的四个角。
    func setGuide(rect: CGRect?) {
        CATransaction.begin(); CATransaction.setDisableActions(true)
        if let r = rect {
            let p = CGMutablePath()
            let k: F = 26
            for (cx, cy, dx, dy) in [(r.minX, r.minY, F(1), F(1)), (r.maxX, r.minY, F(-1), F(1)),
                                     (r.minX, r.maxY, F(1), F(-1)), (r.maxX, r.maxY, F(-1), F(-1))] {
                p.move(to: CGPoint(x: cx + dx * k, y: cy))
                p.addLine(to: CGPoint(x: cx, y: cy))
                p.addLine(to: CGPoint(x: cx, y: cy + dy * k))
            }
            guide.path = p
            guide.isHidden = false
        } else {
            guide.isHidden = true
        }
        CATransaction.commit()
    }

    func layoutLyric(in band: CGRect) {
        CATransaction.begin(); CATransaction.setDisableActions(true)
        lyric.frame = band
        CATransaction.commit()
    }

    func setLyric(_ text: String, fontSize: F, animated: Bool) {
        guard text != lyricText else { return }
        lyricText = text
        let para = NSMutableParagraphStyle()
        para.alignment = .center
        para.lineBreakMode = .byWordWrapping
        let font = NSFont(name: "PingFangSC-Semibold", size: fontSize) ?? NSFont.systemFont(ofSize: fontSize, weight: .semibold)
        let attr = NSAttributedString(string: text, attributes: [
            .font: font, .foregroundColor: NSColor.white, .paragraphStyle: para,
        ])
        CATransaction.begin(); CATransaction.setDisableActions(true)
        if animated {
            let tr = CATransition()
            tr.type = .fade
            tr.duration = 0.18
            lyric.add(tr, forKey: "lyricFade")
        }
        lyric.string = attr
        CATransaction.commit()
    }

    /// 离屏渲染当前图层树（自测用）。
    func render(into ctx: CGContext) {
        root.render(in: ctx)
    }
}

final class StageWindow: NSWindow {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

final class StageView: NSView {
    var onClick: (() -> Void)?
    override func mouseDown(with event: NSEvent) { onClick?() }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

/// 贴在桌面图标层正上方的全屏窗口。它只存在于本进程里：
/// 进程一结束（正常退出、崩溃、被强杀都一样）窗口立刻消失，真实桌面原样露出。
final class StageWindowController {
    let screen: NSScreen
    let window: StageWindow
    let view: StageView
    let layers: StageLayers

    init(screen: NSScreen, items: [DancerItem], bystanders: [DancerItem] = []) {
        self.screen = screen
        layers = StageLayers(size: screen.frame.size, scale: screen.backingScaleFactor, items: items, bystanders: bystanders)
        window = StageWindow(contentRect: screen.frame, styleMask: .borderless, backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = false
        // 桌面图标层 +3：盖住 Finder 图标和桌面小组件，但仍在所有普通窗口之下，看起来就是桌面本身。
        window.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopIconWindow)) + 3)
        window.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenNone]
        window.ignoresMouseEvents = true
        window.animationBehavior = .none
        view = StageView(frame: NSRect(origin: .zero, size: screen.frame.size))
        view.layer = layers.root
        view.wantsLayer = true
        window.contentView = view
    }

    /// 有遮罩时拦截鼠标（防止隔着遮罩误拖看不见的真实图标），点一下即停止。
    func setInteractive(_ on: Bool) { window.ignoresMouseEvents = !on }

    func show() {
        window.setFrame(screen.frame, display: true)
        window.orderFrontRegardless()
    }

    func close() {
        window.orderOut(nil)
        window.close()
    }
}
