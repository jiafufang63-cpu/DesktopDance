import AppKit

/// 左下角的小控制窗口。
final class PanelController: NSObject, NSWindowDelegate {
    let window: NSWindow
    private let c: DanceController
    private let startBtn = NSButton(title: "开始蹦迪", target: nil, action: nil)
    private let pauseBtn = NSButton(title: "暂停", target: nil, action: nil)
    private let restoreBtn = NSButton(title: "恢复桌面", target: nil, action: nil)
    private let modePop = NSPopUpButton()
    private let autoChk = NSButton(checkboxWithTitle: "自动切换", target: nil, action: nil)
    private let ballPop = NSPopUpButton()
    private let speedSlider = NSSlider(value: 1, minValue: 0.5, maxValue: 2, target: nil, action: nil)
    private let ampSlider = NSSlider(value: 1, minValue: 0.3, maxValue: 2, target: nil, action: nil)
    private let countSlider = NSSlider(value: 48, minValue: 12, maxValue: 96, target: nil, action: nil)
    private let countLabel = NSTextField(labelWithString: "48")
    private let labelChk = NSButton(checkboxWithTitle: "图标名称", target: nil, action: nil)
    private let sysChk = NSButton(checkboxWithTitle: "跟着电脑正在播放的音乐跳", target: nil, action: nil)
    private let hideChk = NSButton(checkboxWithTitle: "截屏 / 录屏时隐藏本窗口", target: nil, action: nil)
    private static let hideKey = "hidePanelFromCapture"
    private let status = NSTextField(wrappingLabelWithString: "")
    private let langPop = NSPopUpButton()
    /// 换了语言之后要做的事（菜单栏也要跟着换）
    var onLanguageChange: (() -> Void)?

    init(controller: DanceController) {
        c = controller
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 330, height: 400),
                          styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
        super.init()
        window.isReleasedWhenClosed = false
        window.level = .floating
        window.delegate = self
        // 默认可以被截屏；勾上「截屏 / 录屏时隐藏本窗口」后，全屏录制时它不会入镜。
        applyCaptureVisibility()
        build()
        placeBottomLeft()
        refresh()
    }

    private func row(_ views: [NSView], spacing: CGFloat = 8) -> NSStackView {
        let s = NSStackView(views: views)
        s.orientation = .horizontal
        s.spacing = spacing
        s.alignment = .centerY
        return s
    }

    private func caption(_ t: String) -> NSTextField {
        let l = NSTextField(labelWithString: t)
        l.font = .systemFont(ofSize: 12)
        l.textColor = .secondaryLabelColor
        l.setContentHuggingPriority(.required, for: .horizontal)
        l.widthAnchor.constraint(equalToConstant: Lang.current == .zh ? 36 : 60).isActive = true
        return l
    }

    /// 搭出整个面板。换语言时会重新调用一次，所以这里所有文字都按当前语言重新设置。
    private func build() {
        window.title = T("桌面图标跳舞", "DesktopDance")
        pauseBtn.title = T("暂停", "Pause")
        restoreBtn.title = T("恢复桌面", "Restore")
        autoChk.title = T("自动切换", "Auto switch")
        labelChk.title = T("图标名称", "Names")
        sysChk.title = T("跟着电脑正在播放的音乐跳", "Follow the music playing on this Mac")
        hideChk.title = T("截屏 / 录屏时隐藏本窗口", "Hide this window from screenshots / recordings")
        langPop.removeAllItems()
        langPop.addItems(withTitles: ["中文", "English"])
        langPop.selectItem(at: Lang.current == .zh ? 0 : 1)
        langPop.target = self; langPop.action = #selector(onLanguage)
        langPop.controlSize = .small
        langPop.font = .systemFont(ofSize: 11)
        modePop.removeAllItems()

        for (b, sel) in [(startBtn, #selector(onStart)), (pauseBtn, #selector(onPause)), (restoreBtn, #selector(onRestore))] {
            b.target = self; b.action = sel; b.bezelStyle = .rounded
        }
        startBtn.keyEquivalent = "\r"

        for m in DanceMode.allCases { modePop.addItem(withTitle: m.title) }
        modePop.target = self; modePop.action = #selector(onMode)
        autoChk.target = self; autoChk.action = #selector(onToggles)
        ballPop.target = self; ballPop.action = #selector(onBall)
        ballPop.toolTip = T("「打篮球」模式里用哪个图标当篮球", "Which icon plays the ball in Basketball mode")
        reloadBallMenu()
        labelChk.target = self; labelChk.action = #selector(onToggles)
        hideChk.target = self; hideChk.action = #selector(onHideFromCapture)
        hideChk.font = .systemFont(ofSize: 11)
        hideChk.toolTip = T("勾上后，本窗口不会出现在截图和录屏里（跳舞的图标不受影响）", "When ticked, this window is left out of screenshots and recordings (the dancing icons are not affected)")
        hideChk.state = UserDefaults.standard.bool(forKey: Self.hideKey) ? .on : .off
        for s in [speedSlider, ampSlider, countSlider] { s.target = self; s.action = #selector(onSliders); s.isContinuous = true }

        sysChk.target = self; sysChk.action = #selector(onSystemAudio)
        sysChk.toolTip = T("勾上：听电脑正在播的声音（网易云、抖音、浏览器都行）打拍子。不勾或没声音：用内置 120 拍", "Ticked: follows the beat of whatever this Mac is playing (music app, video, browser). Unticked or silent: uses a built-in 120 BPM beat")
        countLabel.font = .monospacedDigitSystemFont(ofSize: 11, weight: .regular)
        status.font = .systemFont(ofSize: 11)
        status.textColor = .secondaryLabelColor
        status.preferredMaxLayoutWidth = 298
        let hint = NSTextField(labelWithString: T("按 Esc 立刻恢复桌面", "Press Esc to restore instantly"))
        hint.font = .systemFont(ofSize: 11, weight: .medium)

        let stack = NSStackView(views: [
            row([startBtn, pauseBtn, restoreBtn]),
            row([caption(T("模式", "Mode")), modePop, autoChk]),
            row([caption(T("篮球", "Ball")), ballPop]),
            sysChk,
            row([caption(T("速度", "Speed")), speedSlider]),
            row([caption(T("幅度", "Size")), ampSlider]),
            row([caption(T("数量", "Number")), countSlider, countLabel, labelChk]),
            hideChk,
            row([caption(T("语言", "Language")), langPop]),
            hint,
            status,
        ])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 9
        stack.edgeInsets = NSEdgeInsets(top: 14, left: 16, bottom: 14, right: 16)
        stack.translatesAutoresizingMaskIntoConstraints = false
        for v in stack.arrangedSubviews where v is NSStackView {
            v.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -32).isActive = true
        }
        let content = NSView()
        content.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            stack.topAnchor.constraint(equalTo: content.topAnchor),
            stack.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            content.widthAnchor.constraint(equalToConstant: 330),
        ])
        window.contentView = content
        window.layoutIfNeeded()
        window.setContentSize(content.fittingSize)
    }

    /// 放在主屏左下角（Dock 上方），不挡队形。
    func placeBottomLeft() {
        guard let screen = NSScreen.screens.first else { return }
        let v = screen.visibleFrame
        window.setFrameOrigin(NSPoint(x: v.minX + 16, y: v.minY + 16))
    }

    func refresh() {
        let st = c.state
        startBtn.title = st == .paused ? T("继续", "Resume") : T("开始蹦迪", "Start")
        startBtn.isEnabled = st == .idle || st == .paused
        pauseBtn.isEnabled = st == .running
        restoreBtn.isEnabled = st != .idle
        modePop.selectItem(at: c.mode.rawValue)
        autoChk.state = c.autoSwitch ? .on : .off
        labelChk.state = c.showLabels ? .on : .off
        sysChk.state = c.useSystemAudio ? .on : .off
        // 滑块以控制器里的值为准（命令行参数可能改过它们）
        if abs(speedSlider.doubleValue - c.speed) > 0.001 { speedSlider.doubleValue = c.speed }
        if abs(ampSlider.doubleValue - c.amp) > 0.001 { ampSlider.doubleValue = c.amp }
        if Int(countSlider.doubleValue.rounded()) != c.targetCount { countSlider.doubleValue = Double(c.targetCount) }
        countLabel.stringValue = "\(c.targetCount)"
        status.stringValue = c.statusText
    }

    private func reloadBallMenu() {
        ballPop.removeAllItems()
        ballPop.addItem(withTitle: T("自动（第一个应用图标）", "Auto (first app icon)"))
        let names = c.candidateNames()
        for n in names { ballPop.addItem(withTitle: n) }
        let key = c.ballName.lowercased()
        if !key.isEmpty, let i = names.firstIndex(where: { $0.lowercased() == key }) ?? names.firstIndex(where: { $0.lowercased().contains(key) }) {
            ballPop.selectItem(at: i + 1)
        } else {
            ballPop.selectItem(at: 0)
        }
    }

    @objc private func onBall() {
        c.ballName = ballPop.indexOfSelectedItem <= 0 ? "" : (ballPop.titleOfSelectedItem ?? "")
        UserDefaults.standard.set(c.ballName, forKey: "ballName")
    }

    private func applyCaptureVisibility() {
        let hide = UserDefaults.standard.bool(forKey: Self.hideKey) && !CommandLine.arguments.contains("--capturable-panel")
        window.sharingType = hide ? .none : .readOnly
    }

    @objc private func onHideFromCapture() {
        UserDefaults.standard.set(hideChk.state == .on, forKey: Self.hideKey)
        applyCaptureVisibility()
    }

    @objc private func onSystemAudio() {
        c.useSystemAudio = sysChk.state == .on
        UserDefaults.standard.set(c.useSystemAudio, forKey: "followSystemAudio")
        refresh()
    }

    /// 换语言：记住选择，把面板按新语言重搭一遍（窗口位置不变），菜单栏也跟着换。
    @objc private func onLanguage() { setLanguage(langPop.indexOfSelectedItem == 0 ? .zh : .en) }

    func setLanguage(_ new: Lang) {
        guard new != Lang.current else { return }
        Lang.current = new
        UserDefaults.standard.set(new.rawValue, forKey: "uiLanguage")
        let topLeft = NSPoint(x: window.frame.minX, y: window.frame.minY)
        build()
        window.setFrameOrigin(topLeft)
        refresh()
        onLanguageChange?()
    }

    @objc private func onStart() { c.start() }
    @objc private func onPause() { c.togglePause() }
    @objc private func onRestore() { c.stop(animated: true) }
    @objc private func onMode() { if let m = DanceMode(rawValue: modePop.indexOfSelectedItem) { c.mode = m }; refresh() }

    @objc private func onToggles() {
        c.autoSwitch = autoChk.state == .on
        c.showLabels = labelChk.state == .on
        refresh()
    }

    @objc private func onSliders() {
        c.speed = speedSlider.doubleValue
        c.amp = ampSlider.doubleValue
        let newCount = Int(countSlider.doubleValue.rounded())
        if newCount != c.targetCount { c.targetCount = newCount; reloadBallMenu() }
        countLabel.stringValue = "\(c.targetCount)"
    }

    func windowWillClose(_ notification: Notification) {
        dlog("控制窗口被关闭，恢复桌面并退出")
        c.emergencyRestore()
        NSApp.terminate(nil)
    }
}
