import AppKit
import QuickLookThumbnailing

/// 一个参加跳舞的图标。只读取文件的图标和名字，绝不改动文件本身。
struct DancerItem {
    let url: URL
    let fileName: String      // 磁盘上的真实文件名（用于和 Finder 坐标对应）
    let displayName: String
    let isGuest: Bool         // true = 不在桌面上、来自「应用程序」的补位图标
    var home: CGPoint         // 原位（舞台窗口坐标，图标中心）
    var icon: CGImage
    let label: CGImage?
    let labelSize: CGSize
}

enum ItemLoader {
    static let iconPoints: F = 64
    static let maxDesktopItems = 140

    static var desktopURL: URL {
        FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask)[0]
    }

    /// 桌面上可见的文件/文件夹（跳过隐藏文件）。
    static func desktopEntries() -> [URL] {
        let fm = FileManager.default
        guard let urls = try? fm.contentsOfDirectory(at: desktopURL, includingPropertiesForKeys: [.isHiddenKey],
                                                     options: [.skipsHiddenFiles]) else { return [] }
        return urls.sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
    }

    /// 补位用：系统里已安装的应用（只借图标显示，不创建任何文件）。
    /// 只用系统自带的应用补位（做演示图时用，免得把自己装了哪些软件拍进去）。
    static var systemAppsOnly = CommandLine.arguments.contains("--system-apps-only")

    static func guestEntries(limit: Int) -> [URL] {
        guard limit > 0 else { return [] }
        let fm = FileManager.default
        var out: [URL] = []
        var seen = Set<String>()
        for dir in (systemAppsOnly ? [] : ["/Applications"]) + ["/System/Applications", "/System/Applications/Utilities"] {
            let base = URL(fileURLWithPath: dir, isDirectory: true)
            guard let urls = try? fm.contentsOfDirectory(at: base, includingPropertiesForKeys: nil,
                                                         options: [.skipsHiddenFiles]) else { continue }
            let apps = urls.filter { $0.pathExtension == "app" }
                .sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
            for u in apps where seen.insert(u.lastPathComponent).inserted {
                out.append(u)
                if out.count >= limit { return out }
            }
        }
        return out
    }

    static func renderIcon(path: String, pixels: Int) -> CGImage? {
        let img = NSWorkspace.shared.icon(forFile: path)
        guard let ctx = CGContext(data: nil, width: pixels, height: pixels, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.interpolationQuality = .high
        let g = NSGraphicsContext(cgContext: ctx, flipped: false)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = g
        img.draw(in: NSRect(x: 0, y: 0, width: pixels, height: pixels), from: .zero, operation: .sourceOver, fraction: 1)
        NSGraphicsContext.restoreGraphicsState()
        return ctx.makeImage()
    }

    /// 按名字找一个已安装的应用，做成补位图标。选来当篮球的那个应用不在这一批图标里时用它补进来。
    static func appItem(named key: String, scale: F) -> DancerItem? {
        let k = key.lowercased()
        guard !k.isEmpty else { return nil }
        let fm = FileManager.default
        var apps: [(url: URL, name: String)] = []
        for dir in ["/Applications", "/System/Applications", "/System/Applications/Utilities"] {
            guard let urls = try? fm.contentsOfDirectory(at: URL(fileURLWithPath: dir, isDirectory: true),
                                                         includingPropertiesForKeys: nil, options: [.skipsHiddenFiles]) else { continue }
            for u in urls where u.pathExtension == "app" { apps.append((u, fm.displayName(atPath: u.path))) }
        }
        // 名字完全相同的优先，找不到再按「包含」匹配
        guard let hit = apps.first(where: { $0.name.lowercased() == k })
                ?? apps.first(where: { $0.name.lowercased().contains(k) || $0.url.lastPathComponent.lowercased().contains(k) }),
              let icon = renderIcon(path: hit.url.path, pixels: Int(iconPoints * max(scale, 1))) else { return nil }
        let lab = renderLabel(text: hit.name, scale: max(scale, 1))
        return DancerItem(url: hit.url, fileName: hit.url.lastPathComponent, displayName: hit.name, isGuest: true, home: .zero,
                          icon: icon, label: lab?.0, labelSize: lab?.1 ?? .zero)
    }

    /// Finder 给的图标坐标（原点在主屏左上角，y 向下）是不是落在主屏里。
    /// 接了多块屏幕时，放在别的屏幕上的图标坐标会是负数或超出主屏尺寸。
    static func isOnMainScreen(_ p: CGPoint, size: CGSize) -> Bool {
        p.x >= 0 && p.y >= 0 && p.x <= size.width && p.y <= size.height
    }

    /// 在一串名字里找当篮球的那个：完全相同的优先，找不到再按「包含」匹配；key 为空返回 nil。
    static func matchName(_ names: [String], key: String) -> Int? {
        let k = key.lowercased()
        guard !k.isEmpty else { return nil }
        return names.firstIndex { $0.lowercased() == k } ?? names.firstIndex { $0.lowercased().contains(k) }
    }

    /// 仿 Finder 桌面的图标名：白字、黑色投影、最多两行。
    static func renderLabel(text: String, scale: F) -> (CGImage, CGSize)? {
        let para = NSMutableParagraphStyle()
        para.alignment = .center
        para.lineBreakMode = .byWordWrapping
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.85)
        shadow.shadowBlurRadius = 2.5
        shadow.shadowOffset = NSSize(width: 0, height: -1)
        let attr = NSAttributedString(string: text, attributes: [
            .font: NSFont.systemFont(ofSize: 12, weight: .medium),
            .foregroundColor: NSColor.white,
            .paragraphStyle: para,
            .shadow: shadow,
        ])
        let maxW: F = 108
        let opts: NSString.DrawingOptions = [.usesLineFragmentOrigin, .truncatesLastVisibleLine]
        let bounds = attr.boundingRect(with: NSSize(width: maxW, height: 31), options: opts)
        let size = CGSize(width: maxW + 8, height: min(ceil(bounds.height), 31) + 6)
        let pw = Int(ceil(size.width * scale)), ph = Int(ceil(size.height * scale))
        guard let ctx = CGContext(data: nil, width: pw, height: ph, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.scaleBy(x: scale, y: scale)
        let g = NSGraphicsContext(cgContext: ctx, flipped: false)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = g
        attr.draw(with: NSRect(x: 4, y: 3, width: maxW, height: size.height - 6), options: opts)
        NSGraphicsContext.restoreGraphicsState()
        guard let img = ctx.makeImage() else { return nil }
        return (img, size)
    }

    /// 读取桌面图标；数量不够 target 时用应用图标补位。
    static func load(target: Int, scale: F) -> (items: [DancerItem], desktopCount: Int) {
        let fm = FileManager.default
        let px = Int(iconPoints * max(scale, 1))
        var items: [DancerItem] = []
        let desk = Array(desktopEntries().prefix(maxDesktopItems))
        func add(_ url: URL, guest: Bool) {
            guard let icon = renderIcon(path: url.path, pixels: px) else { return }
            let name = fm.displayName(atPath: url.path)
            let lab = renderLabel(text: name, scale: max(scale, 1))
            items.append(DancerItem(url: url, fileName: url.lastPathComponent, displayName: name, isGuest: guest, home: .zero,
                                    icon: icon, label: lab?.0, labelSize: lab?.1 ?? .zero))
        }
        for u in desk { add(u, guest: false) }
        let desktopCount = items.count
        for u in guestEntries(limit: max(0, target - desktopCount)) { add(u, guest: true) }
        return (items, desktopCount)
    }

    /// 桌面上的文件，Finder 显示的是内容缩略图（图片、PDF、文本…）。这里向系统要同样的缩略图，
    /// 这样盖上遮罩的一瞬间副本和真实图标长得一样。超时或失败的保留通用图标。
    static func thumbnails(for items: [DancerItem], scale: F, timeout: TimeInterval,
                           completion: @escaping ([Int: CGImage]) -> Void) {
        // 替身和符号链接不要内容缩略图：Finder 给它们显示的是带箭头的类型图标
        let targets = items.enumerated().filter {
            guard !$0.element.isGuest else { return false }
            let v = try? $0.element.url.resourceValues(forKeys: [.isSymbolicLinkKey, .isAliasFileKey])
            return !(v?.isSymbolicLink ?? false) && !(v?.isAliasFile ?? false)
        }
        guard !targets.isEmpty else { completion([:]); return }
        var result: [Int: CGImage] = [:]
        let lock = NSLock()
        let group = DispatchGroup()
        for (i, it) in targets {
            group.enter()
            let req = QLThumbnailGenerator.Request(fileAt: it.url, size: CGSize(width: iconPoints, height: iconPoints),
                                                   scale: max(scale, 1), representationTypes: .all)
            req.iconMode = true
            QLThumbnailGenerator.shared.generateBestRepresentation(for: req) { rep, _ in
                if let rep { lock.lock(); result[i] = rep.cgImage; lock.unlock() }
                group.leave()
            }
        }
        var done = false
        func finish() {
            guard !done else { return }
            done = true
            lock.lock(); let r = result; lock.unlock()
            completion(r)
        }
        group.notify(queue: .main) { finish() }
        DispatchQueue.main.asyncAfter(deadline: .now() + timeout) { finish() }
    }

    /// 仿 Finder 默认排列：从右上角开始，一列一列往左排。
    static func gridHome(slot: Int, visible: CGRect) -> CGPoint {
        let cellW: F = 112, cellH: F = 108
        let rows = max(1, Int((visible.height - 30) / cellH))
        let col = slot / rows, row = slot % rows
        return CGPoint(x: visible.maxX - 66 - F(col) * cellW, y: visible.maxY - 58 - F(row) * cellH)
    }
}
