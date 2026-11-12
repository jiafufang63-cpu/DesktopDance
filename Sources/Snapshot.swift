import AppKit

/// 桌面状态快照：每个图标在 Finder 里的坐标 + 叠放/排列设置。
/// 本程序跳舞时只画「副本」，从不移动真实图标；快照用来 (1) 让副本从真实位置出发，
/// (2) 结束后核对桌面是否原样（只核对，不改动）。
final class DesktopSnapshot {
    static let shared = DesktopSnapshot()

    /// 文件名 -> Finder 桌面坐标（原点在主屏左上角，y 向下）
    private(set) var positions: [String: CGPoint] = [:]
    private(set) var stacksGroupBy: String = "None"
    private(set) var arrangeBy: String = "none"
    private(set) var lastError: String?
    private let queue = DispatchQueue(label: "desktopdance.snapshot")

    var stacksOn: Bool { stacksGroupBy.lowercased() != "none" && !stacksGroupBy.isEmpty }

    private static let readScript = """
    tell application "Finder"
        set out to ""
        repeat with f in (get every item of desktop)
            try
                set p to desktop position of f
                set out to out & (name of f) & tab & (item 1 of p) & tab & (item 2 of p) & linefeed
            end try
        end repeat
        return out
    end tell
    """

    private func runOSA(_ script: String, args: [String] = [], timeout: TimeInterval) -> (out: String, ok: Bool) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        p.arguments = ["-e", script] + args
        let outPipe = Pipe()
        p.standardOutput = outPipe
        p.standardError = Pipe()
        do { try p.run() } catch { return ("", false) }
        let killer = DispatchWorkItem { if p.isRunning { p.terminate() } }
        DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: killer)
        let data = outPipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        killer.cancel()
        return (String(data: data, encoding: .utf8) ?? "", p.terminationStatus == 0)
    }

    private func readPositions(timeout: TimeInterval) -> [String: CGPoint]? {
        let r = runOSA(Self.readScript, timeout: timeout)
        guard r.ok else { return nil }
        var map: [String: CGPoint] = [:]
        for line in r.out.split(whereSeparator: { $0 == "\n" || $0 == "\r" }) {
            let parts = line.split(separator: "\t", omittingEmptySubsequences: false)
            guard parts.count >= 3, let x = Double(parts[parts.count - 2]), let y = Double(parts[parts.count - 1]) else { continue }
            let name = parts[0..<(parts.count - 2)].joined(separator: "\t")
            map[name] = CGPoint(x: x, y: y)
        }
        return map
    }

    private func readFinderPrefs() {
        let dvs = CFPreferencesCopyAppValue("DesktopViewSettings" as CFString, "com.apple.finder" as CFString) as? [String: Any]
        stacksGroupBy = (dvs?["GroupBy"] as? String) ?? "None"
        arrangeBy = ((dvs?["IconViewSettings"] as? [String: Any])?["arrangeBy"] as? String) ?? "none"
    }

    /// 后台读取一次快照并存盘。桌面为空时不打扰 Finder（也就不会触发「自动化」授权弹窗）。
    func refresh(timeout: TimeInterval = 60, completion: ((Bool) -> Void)? = nil) {
        queue.async {
            self.readFinderPrefs()
            var ok = true
            if ItemLoader.desktopEntries().isEmpty {
                self.positions = [:]
                self.lastError = nil
            } else if let map = self.readPositions(timeout: timeout) {
                self.positions = map
                self.lastError = nil
                self.save()
            } else {
                ok = false
                self.positions = [:]          // 没读到就别留着上一次的旧坐标
                self.lastError = T("没拿到 Finder 图标坐标（未授权「自动化」或超时），副本将从默认网格位置出发", "Could not read icon positions from Finder (Automation not allowed, or it timed out); copies will start from a default grid")
            }
            DispatchQueue.main.async { completion?(ok) }
        }
    }

    private func save() {
        let obj: [String: Any] = [
            "savedAt": ISO8601DateFormatter().string(from: Date()),
            "stacksGroupBy": stacksGroupBy,
            "arrangeBy": arrangeBy,
            "positions": positions.mapValues { [Double($0.x), Double($0.y)] },
        ]
        if let data = try? JSONSerialization.data(withJSONObject: obj, options: [.prettyPrinted, .sortedKeys]) {
            try? data.write(to: AppPaths.snapshotFile, options: .atomic)
        }
    }

    /// 结束后核对：只看、不改。本程序从不移动真实图标，所以即使发现有图标位置和开始时不同
    /// （比如跳舞期间你自己拖过、或新文件落到桌面后 Finder 重新排了），也不会去「写回」——
    /// 写回反而可能把你刚整理好的位置弄乱。
    func verify(completion: @escaping (String) -> Void) {
        queue.async {
            let before = self.positions
            guard !before.isEmpty else {
                DispatchQueue.main.async { completion("") }
                return
            }
            guard let now = self.readPositions(timeout: 15) else {
                DispatchQueue.main.async { completion(T("（未能复核图标坐标）", "(Could not re-check icon positions)")) }
                return
            }
            var moved = 0
            for (name, p) in before {
                guard let q = now[name] else { continue }   // 文件已被用户自己移走/删除：不管
                if abs(p.x - q.x) > 1.5 || abs(p.y - q.y) > 1.5 { moved += 1 }
            }
            let msg = moved == 0 ? T("桌面已复核：\(before.count) 个图标位置未变", "Desktop checked: all \(before.count) icons are where they were")
                                 : T("桌面已复核：有 \(moved) 个图标的位置和开始时不一样。本程序没有移动过它们，也没有去改动", "Desktop checked: \(moved) icons are not where they were at the start. This app did not move them and has not changed anything")
            dlog(msg)
            // 位置没变是正常情况，只写日志；有变化才告诉用户
            DispatchQueue.main.async { completion(moved == 0 ? "" : msg) }
        }
    }
}
