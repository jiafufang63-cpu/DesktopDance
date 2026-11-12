import AppKit
import QuartzCore
import Carbon.HIToolbox

/// 总控：开始 / 暂停 / 恢复，驱动每一帧。
/// 安全模型：真实桌面图标从头到尾不被移动、不被隐藏设置、不被改名；
/// 我们只是在它们上方盖一个属于本进程的窗口。窗口没了，桌面就是原样。
final class DanceController: NSObject {
    enum State { case idle, preparing, running, paused, stopping }

    private(set) var state: State = .idle { didSet { onChange?() } }
    var onChange: (() -> Void)?

    // 设置（控制窗口直接改这些）
    var mode: DanceMode = .figure { didSet { if oldValue != mode { modeChanged() } } }
    var portrait = false { didSet { layoutStage(animated: true) } }
    var showGuide = true { didSet { layoutStage(animated: false) } }
    var showLabels = true { didSet { refreshLabels() } }
    var showLyrics = false { didSet { layoutStage(animated: false) } }
    var autoSwitch = false { didSet { if oldValue != autoSwitch { modeStartBeat = clock.beat } } }
    var speed: Double = 1
    var amp: Double = 1 { didSet { choreo.amp = F(amp) } }
    var targetCount = 48 { didSet { if oldValue != targetCount { scheduleReload() } } }
    var ballName = UserDefaults.standard.string(forKey: "ballName") ?? "" { didSet {
        updateBall()
        // 选来当篮球的那个现在没在跳（留在原位的桌面图标）：重新排一次，把它换上场
        if isActive, !ballName.isEmpty, ballIndex(in: items) == nil { reloadForced = true; scheduleReload() }
    } }   // 当篮球的图标名；空 = 自动（第一个应用图标）
    private var autoBall = 0          // 自动挑出来的那个图标的序号
    var audioURL: URL? { didSet { if isActive { restartAudio() } } }
    var lyrics: Lyrics = .placeholder
    var useSystemAudio = (UserDefaults.standard.object(forKey: "followSystemAudio") as? Bool) ?? true { didSet { if oldValue != useSystemAudio, isActive, !suppressAudioSwitch { restartAudio() } } }
    var forceCurtain = false          // 自测用：桌面为空时也盖遮罩
    var motionURL: URL?               // 指定动作数据文件（不指定就找默认位置）
    var holdHome = false              // 自测用：副本停在原位不跳，用来和真实图标位置对比

    private(set) var note = ""        // 最近一条提示
    private(set) var fps = 0
    private(set) var desktopCount = 0
    private(set) var curtainSource: Wallpaper.Source?
    /// 声音来源的状态。存状态而不是存文字，这样换语言后状态行能立刻跟着变。
    private enum AudioState { case builtIn, connecting, following, stopped(String), file(String) }
    private var audioState: AudioState = .builtIn
    var audioNote: String {
        switch audioState {
        case .builtIn: return T("内置节拍", "Built-in beat")
        case .connecting: return T("正在连接系统声音…", "Connecting to system audio…")
        case .following: return T("跟随系统声音", "Following system audio")
        case .stopped(let msg): return T("系统声音中断（\(msg)），改用内置节拍", "System audio stopped (\(msg)); using the built-in beat")
        case .file(let name): return "♪ " + name
        }
    }

    let hotKeys = HotKeys()
    private var stageWC: StageWindowController?
    private var link: CADisplayLink?
    private var items: [DancerItem] = []
    private let choreo = Choreographer()
    private let analyzer = BeatAnalyzer()
    private let clock = BeatClock()
    private let player = FilePlayer()
    private lazy var sysAudio = SystemAudioCapture(analyzer: analyzer)
    private var suppressAudioSwitch = false
    private var prepareToken = 0

    private var visibleLocal: CGRect = .zero
    private var lastTick: Double = 0
    private var danceTime: Double = 0
    private var modeStartBeat: Double = 0
    private var tempoRate: Double = 1          // 「打篮球」随音乐快慢的播放倍率（平滑后的）
    private var lastPoses: [Pose] = []
    private var transFrom: [Pose]?
    private var transStart: Double = 0
    private var transDur: Double = 0.6
    private var transStagger: Double = 0
    private var frames = 0
    private var fpsStamp: Double = 0

    var isActive: Bool { state == .running || state == .paused }
    var audioDebug: String { analyzer.debugSummary(now: CACurrentMediaTime()) + " locked=\(clock.locked)" + String(format: " tempoRate=%.3f", tempoRate) }
    var bpm: Int { Int(clock.bpm.rounded()) }
    var guestCount: Int { items.filter { $0.isGuest }.count }
    private(set) var bystanders: [DancerItem] = []     // 桌面图标比「数量」多时，多出来的：画在原位不动

    // MARK: - 开始

    func start() {
        if state == .paused { togglePause(); return }
        guard state == .idle, let screen = NSScreen.screens.first else { return }
        state = .preparing
        prepareToken += 1
        let token = prepareToken
        // 第一步：现读一次 Finder 里的图标坐标（桌面为空时瞬间返回）
        DesktopSnapshot.shared.refresh(timeout: 20) { [weak self] _ in
            guard let self, self.state == .preparing, self.prepareToken == token else { return }
            self.loadAndBegin(screen: screen, token: token)
        }
    }

    private func loadAndBegin(screen: NSScreen, token: Int) {
        visibleLocal = screen.visibleFrame.offsetBy(dx: -screen.frame.minX, dy: -screen.frame.minY)
        let scale = screen.backingScaleFactor
        let loaded = ItemLoader.load(target: targetCount, scale: scale)
        loadedTarget = targetCount
        items = loaded.items
        desktopCount = loaded.desktopCount
        // 多显示器：Finder 坐标落在主屏之外的图标在别的屏幕上，舞台盖不到它们，所以不让它们上场
        let snap = DesktopSnapshot.shared.positions
        let before = items.count
        items.removeAll { it in
            guard !it.isGuest, let p = snap[it.fileName] else { return false }
            return !ItemLoader.isOnMainScreen(p, size: screen.frame.size)
        }
        desktopCount -= before - items.count
        guard !items.isEmpty else {
            teardown()
            note = T("没有可用的图标", "No icons available")
            onChange?()
            return
        }
        // 篮球：选中的那个应用不在这批图标里（比如桌面文件已经够数）时，把它单独补进来
        if !ballName.isEmpty, ballIndex(in: items) == nil, let b = ItemLoader.appItem(named: ballName, scale: scale) { items.append(b) }
        // 原位：真实图标用 Finder 里的坐标；拿不到坐标的和补位图标排进默认网格里还空着的格子
        var taken: [CGPoint] = []
        for i in items.indices where !items[i].isGuest {
            if let p = snap[items[i].fileName] {
                items[i].home = CGPoint(x: p.x, y: screen.frame.height - p.y)
                taken.append(items[i].home)
            }
        }
        var slot = 0
        for i in items.indices where items[i].isGuest || snap[items[i].fileName] == nil {
            var p = ItemLoader.gridHome(slot: slot, visible: visibleLocal)
            var guardCount = 0
            while taken.contains(where: { abs($0.x - p.x) < 104 && abs($0.y - p.y) < 100 }) && guardCount < 400 {
                slot += 1; guardCount += 1
                p = ItemLoader.gridHome(slot: slot, visible: visibleLocal)
            }
            items[i].home = p
            slot += 1
        }
        // 「数量」是多少，就有多少个图标跳舞。桌面图标比它多时，排在前面的跳，其余的留在原位不动。
        // 当篮球的那个必须在跳舞的那一批里。
        let dancers = min(items.count, max(1, targetCount))
        if let bi = ballIndex(in: items), bi >= dancers { items.swapAt(bi, dancers - 1) }
        // 第二步：给真实桌面文件换上和 Finder 一样的内容缩略图
        ItemLoader.thumbnails(for: items, scale: scale, timeout: 1.5) { [weak self] thumbs in
            guard let self, self.state == .preparing, self.prepareToken == token else { return }
            for (i, img) in thumbs where i < self.items.count { self.items[i].icon = img }
            self.bystanders = self.items[dancers...].filter { !$0.isGuest }
            self.items = Array(self.items[..<dancers])
            self.autoBall = self.pickAutoBall()
            let wc = StageWindowController(screen: screen, items: self.items, bystanders: self.bystanders)
            let needCurtain = self.desktopCount > 0 || self.forceCurtain
            guard needCurtain else {
                self.curtainSource = nil
                self.begin(wc)
                return
            }
            // 第三步：要盖住真实图标就需要一张壁纸。默认用系统壁纸文件，不需要任何权限。
            // 本程序从不主动申请「屏幕录制」；只有用户自己在系统设置里给过，才改用实时截取的壁纸。
            Wallpaper.obtain(for: screen) { [weak self] img, src in
                guard let self, self.state == .preparing, self.prepareToken == token else { return }
                wc.layers.setCurtain(image: img, visible: true)
                wc.setInteractive(true)
                self.curtainSource = src
                self.begin(wc)
            }
        }
    }

    // MARK: - 跳舞途中改「数量」

    private var reloadWork: DispatchWorkItem?
    private var staleStage: StageWindowController?     // 换图标时旧舞台先留着，新舞台出来再撤，避免闪一下

    private var loadedTarget = 0          // 现在这批图标是按哪个「数量」取的
    private var reloadPending = false     // 暂停或准备期间改了数量：先记着，回到跳舞状态再换
    private var reloadForced = false      // 数量没变也要重排（换了一个没在跳的图标当篮球）

    /// 拖「数量」滑块时会连续触发；停手 0.35 秒后才真的换一批图标。
    /// 暂停时不打断暂停、准备期间不丢这次改动：都先记下，等回到跳舞状态再换。
    private func scheduleReload() {
        reloadWork?.cancel()
        switch state {
        case .running:
            let w = DispatchWorkItem { [weak self] in self?.reloadItems() }
            reloadWork = w
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.35, execute: w)
        case .paused, .preparing:
            reloadPending = true
        default:
            break
        }
    }

    /// 按新的数量重新取图标并重新开场（图标位置快照沿用开始时读到的，不再打扰 Finder）。
    /// 换图标期间 Esc 热键一直保持注册，随时可以恢复。
    private func reloadItems() {
        guard state == .running, let screen = NSScreen.screens.first else { return }
        reloadPending = false
        guard reloadForced || loadedTarget != targetCount else { return }
        reloadForced = false
        link?.invalidate()
        link = nil
        stopAudio()
        staleStage = stageWC
        stageWC = nil
        state = .preparing
        prepareToken += 1
        dlog("重新排队：数量 \(targetCount)")
        loadAndBegin(screen: screen, token: prepareToken)
    }

    private func begin(_ wc: StageWindowController) {
        stageWC = wc
        wc.view.onClick = { [weak self] in dlog("点击桌面，停止"); self?.stop(animated: true) }
        choreo.homes = items.map { $0.home }
        choreo.amp = F(amp)
        choreo.iconSize = ItemLoader.iconPoints
        choreo.clip = motionURL.flatMap { MotionClip(url: $0) } ?? MotionClip.loadDefault()
        updateBall()
        layoutStage(animated: false)
        clock.reset()
        analyzer.reset()
        let home = homePoses(guestAlpha: 0)
        choreo.setMode(mode, beat: 0, current: home)
        wc.layers.apply(home)
        lastPoses = home
        wc.show()
        staleStage?.close()
        staleStage = nil
        beginTransition(from: home, dur: 1.1, stagger: 0.5)

        hotKeys.register(id: HotKeyID.escape, keyCode: kVK_Escape, modifiers: 0)
        let marker: [String: Any] = ["pid": Int(ProcessInfo.processInfo.processIdentifier),
                                     "startedAt": ISO8601DateFormatter().string(from: Date())]
        try? JSONSerialization.data(withJSONObject: marker).write(to: AppPaths.sessionMarker, options: .atomic)

        danceTime = 0
        modeStartBeat = 0
        frames = 0
        lastTick = CACurrentMediaTime()
        fpsStamp = lastTick
        let l = wc.view.displayLink(target: self, selector: #selector(tick(_:)))
        l.add(to: .main, forMode: .common)
        link = l
        note = ""
        state = .running
        startAudio()
        if reloadPending || reloadForced || loadedTarget != targetCount { reloadPending = false; scheduleReload() }
        dlog("打篮球动作：\(choreo.clip == nil ? "手写动作（没有动作数据）" : "动作数据 \(String(format: "%.1f", choreo.clip!.duration)) 秒")")
        dlog("开始：跳舞 \(items.count) 个，留在原位 \(bystanders.count) 个；桌面图标 \(desktopCount) 个，补位 \(guestCount) 个，模式 \(mode.title)，遮罩 \(curtainSource?.rawValue ?? "无（桌面为空，直接透明）")")
    }

    // MARK: - 暂停 / 停止 / 紧急恢复

    func togglePause() {
        if state == .running {
            player.pause()
            pausedAt = CACurrentMediaTime()
            transRestartOnResume = false
            state = .paused
        } else if state == .paused {
            let now = CACurrentMediaTime()
            // 暂停期间时间不走：暂停前没播完的过渡接着播，暂停期间才发起的过渡（换模式、换舞台）从现在开始播
            if transFrom != nil { transStart = transRestartOnResume ? now : transStart + (now - pausedAt) }
            lastTick = now
            player.resume()
            state = .running
            if reloadPending { reloadPending = false; scheduleReload() }
        }
    }

    /// 正常停止：图标飞回原位后撤掉舞台。animated=false 时立刻撤。
    func stop(animated: Bool) {
        switch state {
        case .idle:
            return
        case .running where animated:
            stopAudio()
            state = .stopping
            beginTransition(from: lastPoses, dur: 0.45, stagger: 0.1)
            // 回原位的动画靠屏幕刷新驱动。舞台不在当前桌面（比如你在全屏的 app 里）时刷新可能暂停，
            // 动画永远播不完；所以 1 秒后不管播没播完都把舞台撤掉。
            let token = prepareToken
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.0) { [weak self] in
                guard let self, self.state == .stopping, self.prepareToken == token else { return }
                dlog("回原位动画没播完，直接撤掉舞台")
                self.teardown()
            }
        default:
            teardown()
        }
    }

    /// 紧急恢复（Esc / 收到退出信号）：不做动画，立刻撤掉舞台。
    func emergencyRestore() { teardown() }

    private func teardown() {
        prepareToken += 1
        guard state != .idle || stageWC != nil || staleStage != nil else { return }
        link?.invalidate()
        link = nil
        stopAudio()
        reloadWork?.cancel()
        reloadPending = false
        reloadForced = false
        staleStage?.close()
        staleStage = nil
        stageWC?.close()
        stageWC = nil
        hotKeys.unregister(id: HotKeyID.escape)
        try? FileManager.default.removeItem(at: AppPaths.sessionMarker)
        let hadReal = desktopCount > 0
        items = []
        bystanders = []
        lastPoses = []
        transFrom = nil
        fps = 0
        note = ""
        state = .idle
        dlog("已恢复桌面")
        if hadReal {
            DesktopSnapshot.shared.verify { [weak self] msg in
                guard let self, !msg.isEmpty else { return }
                self.note = msg
                self.onChange?()
            }
        }
    }

    // MARK: - 音频

    /// 默认跟着电脑正在播放的声音跳（网易云、抖音、浏览器…）。系统没放行时用内置节拍，下次开始再试。
    /// 命令行给了 --audio 时改播那个本地文件（自测用）。
    private func startAudio() {
        analyzer.reset()
        if useSystemAudio && audioURL == nil {
            audioState = .connecting
            sysAudio.onStopped = { [weak self] msg in
                self?.audioState = .stopped(msg)
                self?.onChange?()
            }
            sysAudio.start { [weak self] err in
                guard let self else { return }
                guard self.isActive else { self.sysAudio.stop(); return }
                if let err {
                    dlog("系统声音不可用：\(err)")
                    self.audioState = .builtIn
                    self.note = T("接不上系统声音（\(err)），先用内置节拍", "Could not connect to system audio (\(err)); using the built-in beat")
                } else {
                    self.audioState = .following
                    dlog("系统声音已接通")
                }
                self.onChange?()
            }
        } else {
            startFileAudio()
        }
    }

    private func startFileAudio() {
        guard let url = audioURL else { audioState = .builtIn; return }
        do {
            try player.start(url: url, analyzer: analyzer)
            audioState = .file(url.lastPathComponent)
        } catch {
            audioState = .builtIn
            note = T("音频打不开：\(error.localizedDescription)", "Could not open the audio file: \(error.localizedDescription)")
            dlog("音频打不开：\(error)")
        }
    }

    private func stopAudio() {
        player.stop()
        sysAudio.stop()       // 即使还在连接中也要撤，否则取消勾选后监听可能还留着
    }

    private func restartAudio() {
        stopAudio()
        startAudio()
        if state == .paused { player.pause() }
        onChange?()
    }

    private func updateBall() {
        choreo.ballIndex = ballIndex(in: items) ?? autoBall
        refreshLabels()
    }

    /// 没指定篮球时不去猜：用第一个应用图标，没有应用图标就用第一个图标。想换就在「篮球」菜单里选。
    private func pickAutoBall() -> Int { items.firstIndex { $0.isGuest } ?? 0 }

    /// 找当篮球的图标：名字完全相同的优先（选「音乐」不会选中「网易云音乐」），找不到再按「包含」匹配。
    private func ballIndex(in list: [DancerItem]) -> Int? {
        let key = ballName.lowercased()
        guard !key.isEmpty else { return nil }
        return list.firstIndex { $0.displayName.lowercased() == key }
            ?? list.firstIndex { $0.displayName.lowercased().contains(key) || $0.fileName.lowercased().contains(key) }
    }

    private func refreshLabels() {
        stageWC?.layers.setLabelsVisible(showLabels, hiddenIndex: mode == .figure ? choreo.ballIndex : nil)
    }

    /// 可以当篮球的图标名字（给控制窗口的下拉菜单用）。
    func candidateNames() -> [String] {
        let fm = FileManager.default
        let desk = ItemLoader.desktopEntries()
        let all = desk + ItemLoader.guestEntries(limit: max(0, targetCount - desk.count))
        var names = all.map { fm.displayName(atPath: $0.path) }
        // 选中的应用不在这批里时，开始跳舞会把它单独补进来；菜单里也要有它，免得显示成「自动」
        let key = ballName.lowercased()
        if !key.isEmpty, !names.contains(where: { $0.lowercased() == key || $0.lowercased().contains(key) }),
           let app = ItemLoader.appItem(named: ballName, scale: 1) { names.append(app.displayName) }
        return names
    }

    // MARK: - 布局与过渡

    private func homePoses(guestAlpha: F) -> [Pose] {
        items.enumerated().map { i, it in
            Pose(p: it.home, rot: 0, sx: 1, sy: 1, alpha: it.isGuest ? guestAlpha : 1, z: F(i))
        }
    }

    private var pausedAt: Double = 0
    private var transRestartOnResume = false

    private func beginTransition(from: [Pose], dur: Double, stagger: Double) {
        if state == .paused { transRestartOnResume = true }
        transFrom = from
        transStart = CACurrentMediaTime()
        transDur = dur
        transStagger = stagger
    }

    private func modeChanged() {
        guard isActive else { return }
        choreo.setMode(mode, beat: clock.beat, current: lastPoses)
        modeStartBeat = clock.beat
        beginTransition(from: lastPoses, dur: 0.7, stagger: 0.25)
        refreshLabels()
        onChange?()
    }

    /// 算出舞台范围：全屏 = 菜单栏和 Dock 之间的可见区域；竖屏 = 其中居中的 9:16。
    private func layoutStage(animated: Bool) {
        guard let wc = stageWC else { return }
        let v = visibleLocal
        var area = v
        var guideRect: CGRect?
        if portrait {
            let w = min(v.width, (v.height * 9 / 16).rounded())
            area = CGRect(x: (v.midX - w / 2).rounded(), y: v.minY, width: w, height: v.height)
            guideRect = area.insetBy(dx: 1, dy: 1)
        }
        let bandH: F = showLyrics ? (portrait ? 92 : 60) : 0
        wc.layers.lyric.isHidden = !showLyrics
        wc.layers.layoutLyric(in: CGRect(x: area.minX + 14, y: area.maxY - bandH - 12, width: area.width - 28, height: bandH))
        var dance = area.insetBy(dx: portrait ? 30 : 56, dy: 0)
        dance.origin.y += 40
        dance.size.height -= 40 + bandH + 30
        choreo.stage = dance
        choreo.grooveUsesHomes = !portrait
        wc.layers.setGuide(rect: showGuide && portrait ? guideRect : nil)
        if animated, isActive { beginTransition(from: lastPoses, dur: 0.6, stagger: 0.2) }
    }

    // MARK: - 每一帧

    @objc private func tick(_ l: CADisplayLink) {
        let now = CACurrentMediaTime()
        let dt = min(max(now - lastTick, 0), 0.05)
        lastTick = now
        guard state == .running || state == .stopping, let wc = stageWC else { return }

        frames += 1
        if now - fpsStamp >= 1 {
            let newFps = Int((Double(frames) / (now - fpsStamp)).rounded())
            frames = 0
            fpsStamp = now
            if newFps != fps { fps = newFps; onChange?() }
        }

        var poses: [Pose]
        if state == .stopping {
            poses = homePoses(guestAlpha: 0)
        } else {
            let feat = analyzer.drain(now: now)
            let dBeat = clock.update(dt: dt, now: now, features: feat, speed: speed)
            // 「打篮球」跟音乐的方式：歌快动作就快、歌慢动作就慢（100 拍 = 原速，范围 0.75–1.4 倍）。
            // 速度是慢慢滑过去的（约 2 秒），不在鼓点上做任何瞬间变化，所以不会抖。
            // 没在听音乐、没听到声音或还没锁定节拍时回到原速。
            let following = sysAudio.running && sysAudio.hearing && clock.locked
            let tempoTarget = following ? min(max(clock.bpm / 100, 0.75), 1.4) : 1
            tempoRate += (tempoTarget - tempoRate) * min(1, dt / 2.0)
            choreo.clipRate = speed * tempoRate
            danceTime += dt
            if autoSwitch, clock.beat - modeStartBeat >= mode.autoBeats {
                let all = DanceMode.allCases
                mode = all[(all.firstIndex(of: mode)! + 1) % all.count]
            }
            poses = choreo.poses(n: items.count, tempo: Tempo(beat: clock.beat, dBeat: dBeat, dt: dt,
                                                              energy: clock.energy, pulse: clock.pulse))
            if showLyrics {
                let t = player.currentTime ?? danceTime.truncatingRemainder(dividingBy: max(lyrics.loopLength, 1))
                wc.layers.setLyric(lyrics.text(at: t), fontSize: portrait ? 30 : 38, animated: true)
            }
        }

        if holdHome, state == .running {
            poses = homePoses(guestAlpha: 1)
            transFrom = nil
        }
        if let from = transFrom, from.count == poses.count {
            let el = now - transStart
            let n = max(poses.count, 1)
            for i in poses.indices {
                let ti = (el - transStagger * Double(i) / Double(n)) / transDur
                poses[i] = mix(from[i], poses[i], smooth(F(ti)))
            }
            if el >= transDur + transStagger {
                transFrom = nil
                if state == .stopping { teardown(); return }
            }
        } else if state == .stopping {
            teardown()
            return
        }

        // 不让任何图标跑到菜单栏、Dock 或屏幕外面
        let safe = visibleLocal.insetBy(dx: 22, dy: 22)
        for i in poses.indices {
            poses[i].p.x = clamp(poses[i].p.x, safe.minX, safe.maxX)
            poses[i].p.y = clamp(poses[i].p.y, safe.minY, safe.maxY)
        }
        wc.layers.apply(poses)
        lastPoses = poses
    }

    /// 自测用：把舞台图层当前的内容画成 PNG（不依赖屏幕截图，舞台不在当前空间时也能用）。
    @discardableResult
    func dumpStage(to path: String) -> Bool {
        guard let wc = stageWC else { return false }
        let size = wc.screen.frame.size
        guard let ctx = CGContext(data: nil, width: Int(size.width), height: Int(size.height), bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return false }
        ctx.setFillColor(CGColor(gray: 0.16, alpha: 1))
        ctx.fill(CGRect(origin: .zero, size: size))
        wc.layers.render(into: ctx)
        return ctx.makeImage().map { writePNG($0, to: path) } ?? false
    }

    // MARK: - 状态文字

    var statusText: String {
        var parts: [String] = []
        switch state {
        case .idle:
            let n = ItemLoader.desktopEntries().count
            parts.append(T("就绪 · 桌面上有 \(n) 个图标", "Ready · \(n) icons on the desktop"))
            if n < targetCount { parts.append(T("不足 \(targetCount) 个，开始时用「应用程序」里的图标补位（只显示，不建文件）", "Fewer than \(targetCount): app icons from Applications will fill in (display only, no files are created)")) }
            if DesktopSnapshot.shared.stacksOn { parts.append(T("桌面开着「叠放」：不影响跳舞，但副本会从默认网格出发", "Stacks are on: dancing still works, but copies start from a default grid")) }
            if let e = DesktopSnapshot.shared.lastError, n > 0 { parts.append(e) }
        case .preparing:
            parts.append(T("准备中…", "Preparing…"))
        case .running, .paused, .stopping:
            parts.append(state == .paused ? T("已暂停", "Paused") : (state == .stopping ? T("正在回到原位…", "Returning to place…") : T("跳舞中", "Dancing")))
            parts.append(bystanders.isEmpty
                ? T("桌面图标 \(desktopCount) + 补位 \(guestCount)", "Desktop icons \(desktopCount) + borrowed \(guestCount)")
                : T("\(items.count) 个图标在跳，其余 \(bystanders.count) 个留在原位", "\(items.count) icons dancing, the other \(bystanders.count) stay in place"))
            parts.append("\(audioNote) · \(bpm) BPM · \(fps) fps")
            if mode == .figure, abs(tempoRate - 1) > 0.02 { parts.append(String(format: T("跟着音乐：动作速度 %.2f 倍", "Following the music: speed ×%.2f"), tempoRate)) }
            if sysAudio.running && !sysAudio.hearing {
                parts.append(T("现在没听到声音，先按内置节拍跳。电脑一放歌就会自动跟上", "No sound right now, so it is dancing to the built-in beat. It will follow as soon as music plays"))
            }
            // 壁纸正常铺上了就不用说；铺不上才说明背景为什么变了
            if curtainSource == Wallpaper.Source.none { parts.append(T("取不到壁纸文件，用暗色舞台盖住真实图标", "No wallpaper file found; a dark stage covers the real icons")) }
            if !hotKeys.isRegistered(HotKeyID.escape) { parts.append(T("Esc 被别的软件占用了，恢复请点「恢复桌面」", "Esc is taken by another app; click Restore instead")) }
        }
        if !note.isEmpty { parts.append(note) }
        return parts.joined(separator: "\n")
    }
}
