import AppKit
import Carbon.HIToolbox

final class AppDelegate: NSObject, NSApplicationDelegate {
    let controller = DanceController()
    var panel: PanelController!
    private var signalSources: [DispatchSourceSignal] = []

    private func arg(_ name: String) -> String? {
        let a = CommandLine.arguments
        guard let i = a.firstIndex(of: name), i + 1 < a.count else { return nil }
        return a[i + 1]
    }
    private func flag(_ name: String) -> Bool { CommandLine.arguments.contains(name) }

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMenu()

        // 上次是不是没正常退出？真实图标从未被动过，所以只需要清掉标记并告知。
        if FileManager.default.fileExists(atPath: AppPaths.sessionMarker.path) {
            try? FileManager.default.removeItem(at: AppPaths.sessionMarker)
            dlog("检测到上次未正常退出。本程序不移动、不隐藏真实图标，桌面无需修复。")
        }

        panel = PanelController(controller: controller)
        controller.onChange = { [weak self] in self?.panel.refresh() }
        panel.onLanguageChange = { [weak self] in self?.buildMenu() }

        // 紧急热键：Esc，只在跳舞期间注册
        HotKeys.handler = { [weak self] id in
            guard id == HotKeyID.restore || id == HotKeyID.escape else { return }
            dlog("热键触发：\(id == HotKeyID.escape ? "Esc" : "自测信号")")
            self?.controller.emergencyRestore()
        }

        // 被要求退出时先撤舞台。即使来不及（kill -9 / 崩溃），舞台窗口也会随进程一起消失。
        for sig in [SIGTERM, SIGINT, SIGHUP, SIGQUIT] {
            signal(sig, SIG_IGN)
            let src = DispatchSource.makeSignalSource(signal: sig, queue: .main)
            src.setEventHandler { [weak self] in
                dlog("收到信号 \(sig)，恢复桌面后退出")
                self?.controller.emergencyRestore()
                exit(0)
            }
            src.resume()
            signalSources.append(src)
        }
        // 自测用：SIGUSR2 = 点「恢复桌面」（带飞回原位的动画）
        signal(SIGUSR2, SIG_IGN)
        let usr2 = DispatchSource.makeSignalSource(signal: SIGUSR2, queue: .main)
        usr2.setEventHandler { [weak self] in dlog("SIGUSR2：正常停止"); self?.controller.stop(animated: true) }
        usr2.resume()
        signalSources.append(usr2)
        // 自测用：SIGUSR1 走和紧急热键完全相同的代码路径
        signal(SIGUSR1, SIG_IGN)
        let usr = DispatchSource.makeSignalSource(signal: SIGUSR1, queue: .main)
        usr.setEventHandler { HotKeys.handler?(HotKeyID.restore) }
        usr.resume()
        signalSources.append(usr)

        DesktopSnapshot.shared.refresh { [weak self] _ in self?.panel.refresh() }

        // 命令行参数（主要给自测用）
        if let m = arg("--mode").flatMap(DanceMode.named) { controller.mode = m }
        if flag("--portrait") { controller.portrait = true }
        if flag("--no-labels") { controller.showLabels = false }
        if flag("--auto-switch") { controller.autoSwitch = true }
        if flag("--force-curtain") { controller.forceCurtain = true }
        if flag("--hold-home") { controller.holdHome = true }
        if flag("--screenshot-desktop") { Wallpaper.screenshotIncludesDesktopIcons = true }
        if flag("--system-audio") { controller.useSystemAudio = true }
        if let a = arg("--audio") { controller.audioURL = URL(fileURLWithPath: a) }
        if let l = arg("--lyrics"), let ly = Lyrics.load(url: URL(fileURLWithPath: l)) { controller.lyrics = ly; controller.showLyrics = true }
        if flag("--no-system-audio") { controller.useSystemAudio = false }
        if let n = arg("--count").flatMap(Int.init) { controller.targetCount = n }
        if let b = arg("--ball") { controller.ballName = b }
        if let m = arg("--motion") { controller.motionURL = URL(fileURLWithPath: m) }

        panel.window.makeKeyAndOrderFront(nil)
        if !flag("--background") { NSApp.activate(ignoringOtherApps: true) }
        panel.refresh()

        if flag("--autostart") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { self.controller.start() }
        }
        if let spec = arg("--screenshot") {
            // 形如 /path/a.png@3,/path/b.png@6
            for part in spec.split(separator: ",") {
                let kv = part.split(separator: "@")
                guard kv.count == 2, let t = Double(kv[1]), let screen = NSScreen.screens.first else { continue }
                DispatchQueue.main.asyncAfter(deadline: .now() + t) {
                    Wallpaper.screenshot(screen: screen, to: String(kv[0])) { ok in dlog("截图 \(kv[0]) \(ok ? "成功" : "失败")") }
                }
            }
        }
        if let spec = arg("--dump-stage") {
            for part in spec.split(separator: ",") {
                let kv = part.split(separator: "@")
                guard kv.count == 2, let t = Double(kv[1]) else { continue }
                DispatchQueue.main.asyncAfter(deadline: .now() + t) { [weak self] in
                    dlog("舞台导出 \(kv[0]) \(self?.controller.dumpStage(to: String(kv[0])) == true ? "成功" : "失败")")
                }
            }
        }
        if let s = arg("--status-every").flatMap(Double.init) {
            Timer.scheduledTimer(withTimeInterval: s, repeats: true) { [weak self] _ in
                guard let c = self?.controller else { return }
                dlog("状态: \(c.state) \(c.statusText.replacingOccurrences(of: "\n", with: " | "))")
                if c.isActive { dlog("音频调试: \(c.audioDebug)") }
            }
        }
        // 自测用：--script "6:pause,7:mode=snake,9:resume,11:count=20,14:stop"
        // 按「秒:动作」依次执行，等同于在控制窗口里点。动作：pause / resume / stop / portrait / full /
        // mode=<名字> / count=<数> / amp=<数> / speed=<数> / sys=on|off / ball=<名字>
        if let spec = arg("--script") {
            for step in spec.split(separator: ",") {
                let p = step.split(separator: ":", maxSplits: 1)
                guard p.count == 2, let t = Double(p[0]) else { continue }
                let act = String(p[1])
                DispatchQueue.main.asyncAfter(deadline: .now() + t) { [weak self] in
                    guard let c = self?.controller else { return }
                    let kv = act.split(separator: "=", maxSplits: 1).map(String.init)
                    dlog("脚本：\(act)（当前状态 \(c.state)）")
                    switch kv[0] {
                    case "pause": if c.state == .running { c.togglePause() }
                    case "resume": if c.state == .paused { c.togglePause() }
                    case "stop": c.stop(animated: true)
                    case "portrait": c.portrait = true
                    case "full": c.portrait = false
                    case "mode": if kv.count > 1, let m = DanceMode.named(kv[1]) { c.mode = m }
                    case "count": if kv.count > 1, let n = Int(kv[1]) { c.targetCount = n }
                    case "amp": if kv.count > 1, let v = Double(kv[1]) { c.amp = v }
                    case "speed": if kv.count > 1, let v = Double(kv[1]) { c.speed = v }
                    case "sys": if kv.count > 1 { c.useSystemAudio = kv[1] == "on" }
                    case "ball": c.ballName = kv.count > 1 ? kv[1] : ""
                    case "lang": if kv.count > 1, let l = Lang(rawValue: kv[1]) { self?.panel.setLanguage(l) }
                    default: break
                    }
                    self?.panel.refresh()
                }
            }
        }
        if let q = arg("--quit-after").flatMap(Double.init) {
            DispatchQueue.main.asyncAfter(deadline: .now() + q) { NSApp.terminate(nil) }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        controller.emergencyRestore()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    private func buildMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem()
        main.addItem(appItem)
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: T("恢复桌面", "Restore Desktop"), action: #selector(restoreNow), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: T("退出桌面图标跳舞", "Quit DesktopDance"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        NSApp.mainMenu = main
    }

    @objc private func restoreNow() { controller.emergencyRestore() }
}

if let i = CommandLine.arguments.firstIndex(of: "--extract-pose"), i + 2 < CommandLine.arguments.count {
    let a = CommandLine.arguments
    func opt(_ name: String) -> String? { a.firstIndex(of: name).flatMap { $0 + 1 < a.count ? a[$0 + 1] : nil } }
    let ok = MotionExtractor.extract(video: a[i + 1], out: a[i + 2], sheet: opt("--sheet"),
                                     from: opt("--from").flatMap(Double.init) ?? 0,
                                     to: opt("--to").flatMap(Double.init) ?? .greatestFiniteMagnitude)
    exit(ok ? 0 : 1)
}

if let i = CommandLine.arguments.firstIndex(of: "--render-motion"), i + 2 < CommandLine.arguments.count {
    let a = CommandLine.arguments
    func opt(_ name: String) -> Double? { a.firstIndex(of: name).flatMap { $0 + 1 < a.count ? Double(a[$0 + 1]) : nil } }
    SelfTest.renderMotion(json: a[i + 1], out: a[i + 2], first: opt("--first") ?? 0.6, step: opt("--step") ?? 1.2)
    exit(0)
}

if CommandLine.arguments.contains("--self-check") {
    exit(SelfTest.selfCheck() ? 0 : 1)
}

if let i = CommandLine.arguments.firstIndex(of: "--render-frames"), i + 1 < CommandLine.arguments.count {
    let a = CommandLine.arguments
    func opt(_ name: String) -> String? { a.firstIndex(of: name).flatMap { $0 + 1 < a.count ? a[$0 + 1] : nil } }
    SelfTest.renderFrames(outDir: a[i + 1], mode: opt("--mode").flatMap(DanceMode.named) ?? .figure,
                          seconds: opt("--seconds").flatMap(Double.init) ?? 8, fps: opt("--fps").flatMap(Double.init) ?? 15)
    exit(0)
}

if let i = CommandLine.arguments.firstIndex(of: "--render-test"), i + 1 < CommandLine.arguments.count {
    SelfTest.render(outDir: CommandLine.arguments[i + 1])
    exit(0)
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
