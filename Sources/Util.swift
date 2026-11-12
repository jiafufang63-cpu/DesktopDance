import AppKit

typealias F = CGFloat

@inline(__always) func lerp(_ a: F, _ b: F, _ t: F) -> F { a + (b - a) * t }
@inline(__always) func lerpP(_ a: CGPoint, _ b: CGPoint, _ t: F) -> CGPoint {
    CGPoint(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t)
}
@inline(__always) func clamp(_ v: F, _ lo: F, _ hi: F) -> F { min(max(v, lo), hi) }
@inline(__always) func smooth(_ t: F) -> F { let x = clamp(t, 0, 1); return x * x * (3 - 2 * x) }

func + (a: CGPoint, b: CGPoint) -> CGPoint { CGPoint(x: a.x + b.x, y: a.y + b.y) }
func - (a: CGPoint, b: CGPoint) -> CGPoint { CGPoint(x: a.x - b.x, y: a.y - b.y) }
func * (a: CGPoint, k: F) -> CGPoint { CGPoint(x: a.x * k, y: a.y * k) }

/// 稳定的伪随机数（0..<1），同样的输入永远得到同样的结果。
func hash01(_ a: Int, _ b: Int) -> F {
    var x = UInt64(bitPattern: Int64(a)) &* 0x9E3779B97F4A7C15 &+ UInt64(bitPattern: Int64(b)) &* 0xBF58476D1CE4E5B9
    x ^= x >> 30; x = x &* 0xBF58476D1CE4E5B9
    x ^= x >> 27; x = x &* 0x94D049BB133111EB
    x ^= x >> 31
    return F(x >> 11) / F(UInt64(1) << 53)
}

/// 一个图标在某一帧的姿态。坐标为舞台窗口坐标（原点左下，y 向上）。
struct Pose {
    var p: CGPoint
    var rot: F = 0
    var sx: F = 1
    var sy: F = 1
    var alpha: F = 1
    var z: F = 0
}

func mix(_ a: Pose, _ b: Pose, _ t: F) -> Pose {
    Pose(p: lerpP(a.p, b.p, t), rot: lerp(a.rot, b.rot, t), sx: lerp(a.sx, b.sx, t),
         sy: lerp(a.sy, b.sy, t), alpha: lerp(a.alpha, b.alpha, t), z: t < 0.5 ? a.z : b.z)
}

/// 界面语言：中文或英文。第一次按系统语言定，之后记住用户在控制窗口里选的。
enum Lang: String {
    case zh, en
    static var current: Lang = {
        // 自测用：--lang zh|en 只对这一次启动生效
        if let i = CommandLine.arguments.firstIndex(of: "--lang"), i + 1 < CommandLine.arguments.count, let l = Lang(rawValue: CommandLine.arguments[i + 1]) { return l }
        if let saved = UserDefaults.standard.string(forKey: "uiLanguage"), let l = Lang(rawValue: saved) { return l }
        return (Locale.preferredLanguages.first ?? "en").hasPrefix("zh") ? .zh : .en
    }()
}

/// 按当前界面语言二选一。所有给用户看的文字都走这里。
func T(_ zh: String, _ en: String) -> String { Lang.current == .zh ? zh : en }

enum AppPaths {
    static let supportDir: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let dir = base.appendingPathComponent("DesktopDance", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()
    static var sessionMarker: URL { supportDir.appendingPathComponent("session.json") }
    static var snapshotFile: URL { supportDir.appendingPathComponent("desktop-snapshot.json") }
    static var logFile: URL { supportDir.appendingPathComponent("desktop-dance.log") }
}

private let logQueue = DispatchQueue(label: "desktopdance.log")
private let logStamp: DateFormatter = {
    let f = DateFormatter(); f.dateFormat = "HH:mm:ss.SSS"; return f
}()

func dlog(_ s: String) {
    let line = "[\(logStamp.string(from: Date()))] \(s)\n"
    FileHandle.standardError.write(line.data(using: .utf8)!)
    logQueue.async {
        let url = AppPaths.logFile
        if let h = try? FileHandle(forWritingTo: url) {
            h.seekToEndOfFile(); h.write(line.data(using: .utf8)!); try? h.close()
        } else {
            try? line.data(using: .utf8)!.write(to: url)
        }
    }
}

func writePNG(_ image: CGImage, to path: String) -> Bool {
    let url = URL(fileURLWithPath: path) as CFURL
    guard let dest = CGImageDestinationCreateWithURL(url, "public.png" as CFString, 1, nil) else { return false }
    CGImageDestinationAddImage(dest, image, nil)
    return CGImageDestinationFinalize(dest)
}
