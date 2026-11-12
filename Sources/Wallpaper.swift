import AppKit
import ScreenCaptureKit

/// 遮罩用的壁纸画面。只有桌面上真的有图标需要盖住时才会用到。
enum Wallpaper {
    enum Source: String { case capture = "实时壁纸", file = "壁纸文件", none = "暗色舞台" }

    static func displayID(of screen: NSScreen) -> CGDirectDisplayID {
        (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? CGMainDisplayID()
    }

    /// 取壁纸：已有「屏幕录制」权限就精确截取壁纸层（不含图标）；否则读壁纸文件；都不行返回 nil。
    /// 这里绝不主动申请权限。
    static func obtain(for screen: NSScreen, completion: @escaping (CGImage?, Source) -> Void) {
        var finished = false
        func finish(_ img: CGImage?, _ src: Source) {
            DispatchQueue.main.async {
                guard !finished else { return }
                finished = true
                completion(img, src)
            }
        }
        func fallback() {
            if let img = fromFile(screen: screen) { finish(img, .file) } else { finish(nil, .none) }
        }
        guard CGPreflightScreenCaptureAccess() else { fallback(); return }
        // 截图 2 秒内没回来就用文件
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.0) { if !finished { fallback() } }
        let did = displayID(of: screen)
        let scale = screen.backingScaleFactor
        let size = screen.frame.size
        SCShareableContent.getExcludingDesktopWindows(false, onScreenWindowsOnly: true) { content, _ in
            guard let content, let display = content.displays.first(where: { $0.displayID == did }) else { fallback(); return }
            let wallLevel = Int(CGWindowLevelForKey(.desktopWindow))
            let walls = content.windows.filter { $0.windowLayer <= wallLevel && $0.frame.width >= size.width * 0.9 }
            guard !walls.isEmpty else { fallback(); return }
            let filter = SCContentFilter(display: display, including: walls)
            let cfg = SCStreamConfiguration()
            cfg.width = Int(size.width * scale)
            cfg.height = Int(size.height * scale)
            cfg.showsCursor = false
            SCScreenshotManager.captureImage(contentFilter: filter, configuration: cfg) { img, _ in
                if let img { finish(img, .capture) } else { fallback() }
            }
        }
    }

    /// 读壁纸图片文件并按「填充屏幕」裁切。动态壁纸只能拿到其中一帧，可能与实际显示略有出入。
    static func fromFile(screen: NSScreen) -> CGImage? {
        guard let url = NSWorkspace.shared.desktopImageURL(for: screen),
              let src = CGImageSourceCreateWithURL(url as CFURL, nil),
              let img = CGImageSourceCreateImageAtIndex(src, 0, nil) else { return nil }
        let scale = screen.backingScaleFactor
        let w = Int(screen.frame.width * scale), h = Int(screen.frame.height * scale)
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else { return nil }
        ctx.interpolationQuality = .high
        let k = max(F(w) / F(img.width), F(h) / F(img.height))
        let dw = F(img.width) * k, dh = F(img.height) * k
        ctx.draw(img, in: CGRect(x: (F(w) - dw) / 2, y: (F(h) - dh) / 2, width: dw, height: dh))
        return ctx.makeImage()
    }

    /// 自测用：true 时截图额外带上 Finder 的桌面图标层（仍不含任何普通应用窗口）。
    static var screenshotIncludesDesktopIcons = false

    /// 自测用：只截「壁纸 + 本程序的窗口」，不会拍到别的应用（需要已有屏幕录制权限）。
    static func screenshot(screen: NSScreen, to path: String, completion: @escaping (Bool) -> Void) {
        guard CGPreflightScreenCaptureAccess() else { completion(false); return }
        let did = displayID(of: screen)
        SCShareableContent.getExcludingDesktopWindows(false, onScreenWindowsOnly: true) { content, _ in
            guard let content, let display = content.displays.first(where: { $0.displayID == did }) else { completion(false); return }
            let pid = ProcessInfo.processInfo.processIdentifier
            let wallLevel = Int(CGWindowLevelForKey(.desktopWindow))
            let iconLevel = Int(CGWindowLevelForKey(.desktopIconWindow))
            let mine = content.windows.filter {
                $0.owningApplication?.processID == pid || $0.windowLayer <= wallLevel
                    || (screenshotIncludesDesktopIcons && $0.windowLayer == iconLevel)
            }
            let filter = SCContentFilter(display: display, including: mine)
            let cfg = SCStreamConfiguration()
            cfg.width = Int(screen.frame.width)
            cfg.height = Int(screen.frame.height)
            cfg.showsCursor = false
            SCScreenshotManager.captureImage(contentFilter: filter, configuration: cfg) { img, _ in
                completion(img.map { writePNG($0, to: path) } ?? false)
            }
        }
    }
}
