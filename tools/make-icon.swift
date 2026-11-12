// 画应用图标（1024×1024 PNG）。改完设计后运行 tools/make-icon.sh 重新生成 AppIcon.icns。
// 设计：深色「夜间桌面」底，一个由彩色小图标拼成的小人在运球。
import AppKit

let S: CGFloat = 1024
let out = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "icon-1024.png"

func rgb(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
    CGColor(red: CGFloat((hex >> 16) & 0xff) / 255, green: CGFloat((hex >> 8) & 0xff) / 255, blue: CGFloat(hex & 0xff) / 255, alpha: a)
}
func shade(_ hex: UInt32, _ k: CGFloat) -> CGColor {   // k>0 提亮，k<0 压暗
    func ch(_ v: UInt32) -> CGFloat {
        let x = CGFloat(v) / 255
        return k >= 0 ? x + (1 - x) * k : x * (1 + k)
    }
    return CGColor(red: ch((hex >> 16) & 0xff), green: ch((hex >> 8) & 0xff), blue: ch(hex & 0xff), alpha: 1)
}

let space = CGColorSpace(name: CGColorSpace.sRGB)!
let ctx = CGContext(data: nil, width: Int(S), height: Int(S), bitsPerComponent: 8, bytesPerRow: 0,
                    space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!

// ── 底板：macOS 图标网格（824 见方、圆角 185），带一点投影 ──────────────
let plate = CGRect(x: 100, y: 100, width: 824, height: 824)
let platePath = CGPath(roundedRect: plate, cornerWidth: 185, cornerHeight: 185, transform: nil)
ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 26, color: CGColor(gray: 0, alpha: 0.32))
ctx.addPath(platePath)
ctx.setFillColor(rgb(0x141a44))
ctx.fillPath()
ctx.restoreGState()

ctx.saveGState()
ctx.addPath(platePath)
ctx.clip()
let bg = CGGradient(colorsSpace: space, colors: [rgb(0x2c3a8c), rgb(0x1a1f55), rgb(0x0b0e2a)] as CFArray, locations: [0, 0.5, 1])!
ctx.drawLinearGradient(bg, start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 100), options: [])
// 舞台追光
let spot = CGGradient(colorsSpace: space, colors: [rgb(0x8aa2ff, 0.42), rgb(0x8aa2ff, 0)] as CFArray, locations: [0, 1])!
ctx.drawRadialGradient(spot, startCenter: CGPoint(x: 512, y: 560), startRadius: 0, endCenter: CGPoint(x: 512, y: 560), endRadius: 430, options: [])
// 地面影子
ctx.saveGState()
ctx.translateBy(x: 512, y: 196)
ctx.scaleBy(x: 1, y: 0.16)
let floor = CGGradient(colorsSpace: space, colors: [rgb(0x000000, 0.55), rgb(0x000000, 0)] as CFArray, locations: [0, 1])!
ctx.drawRadialGradient(floor, startCenter: .zero, startRadius: 0, endCenter: .zero, endRadius: 300, options: [])
ctx.restoreGState()

// ── 小图标块 ────────────────────────────────────────────────────────
func tile(_ c: CGPoint, size: CGFloat, angle: CGFloat, color: UInt32) {
    ctx.saveGState()
    ctx.translateBy(x: c.x, y: c.y)
    ctx.rotate(by: angle)
    let r = CGRect(x: -size / 2, y: -size / 2, width: size, height: size)
    let p = CGPath(roundedRect: r, cornerWidth: size * 0.235, cornerHeight: size * 0.235, transform: nil)
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -6), blur: 14, color: CGColor(gray: 0, alpha: 0.45))
    ctx.addPath(p)
    ctx.setFillColor(rgb(color))
    ctx.fillPath()
    ctx.restoreGState()
    ctx.addPath(p)
    ctx.clip()
    let g = CGGradient(colorsSpace: space, colors: [shade(color, 0.28), rgb(color), shade(color, -0.16)] as CFArray, locations: [0, 0.45, 1])!
    // 光从画面正上方来：把「上」换算到旋转后的坐标里，每块的受光方向才一致
    let up = CGPoint(x: sin(angle) * size / 2, y: cos(angle) * size / 2)
    ctx.drawLinearGradient(g, start: up, end: CGPoint(x: -up.x, y: -up.y), options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
    ctx.restoreGState()
}

/// 沿折线均匀摆 n 个图标块，from/to 是折线上的起止比例。
func chain(_ pts: [CGPoint], n: Int, from: CGFloat, to: CGFloat = 1, size: CGFloat, colors: [UInt32]) {
    var lens: [CGFloat] = []
    for i in 1..<pts.count { lens.append(hypot(pts[i].x - pts[i - 1].x, pts[i].y - pts[i - 1].y)) }
    let total = lens.reduce(0, +)
    for k in 0..<n {
        let t = n == 1 ? (from + to) / 2 : from + (to - from) * CGFloat(k) / CGFloat(n - 1)
        var d = t * total
        var seg = 0
        while seg < lens.count - 1 && d > lens[seg] { d -= lens[seg]; seg += 1 }
        let a = pts[seg], b = pts[seg + 1]
        let u = d / lens[seg]
        let c = CGPoint(x: a.x + (b.x - a.x) * u, y: a.y + (b.y - a.y) * u)
        let ang = atan2(b.y - a.y, b.x - a.x) + (k % 2 == 0 ? 0.10 : -0.08)
        tile(c, size: size, angle: ang, color: colors[k % colors.count])
    }
}

// ── 小人骨架 ────────────────────────────────────────────────────────
let neck = CGPoint(x: 508, y: 668), hip = CGPoint(x: 492, y: 452)
let headC = CGPoint(x: 530, y: 772)
let elbL = CGPoint(x: 386, y: 690), handL = CGPoint(x: 318, y: 806)
let elbR = CGPoint(x: 634, y: 600), handR = CGPoint(x: 716, y: 512)
let kneeL = CGPoint(x: 398, y: 352), footL = CGPoint(x: 336, y: 236)
let kneeR = CGPoint(x: 588, y: 356), footR = CGPoint(x: 650, y: 236)
let ball = CGPoint(x: 762, y: 384), ballR: CGFloat = 74

let red: UInt32 = 0xff5a52, yellow: UInt32 = 0xffc02e, green: UInt32 = 0x30cf5b, blue: UInt32 = 0x2f9bff
let purple: UInt32 = 0xa972ff, pink: UInt32 = 0xff6fae, teal: UInt32 = 0x2fd6cf
let ts: CGFloat = 80

chain([hip, kneeL, footL], n: 3, from: 0.36, size: ts, colors: [purple, pink, yellow])
chain([hip, kneeR, footR], n: 3, from: 0.36, size: ts, colors: [teal, blue, red])
chain([neck, elbL, handL], n: 3, from: 0.31, size: ts, colors: [yellow, red, teal])
chain([neck, elbR, handR], n: 3, from: 0.34, size: ts, colors: [pink, purple, green])
chain([neck, hip], n: 3, from: 0.10, to: 0.96, size: 92, colors: [blue, green, blue])

// 头：一块白色大图标，带两只眼睛
tile(headC, size: 138, angle: 0.16, color: 0xf4f6ff)
ctx.saveGState()
ctx.translateBy(x: headC.x, y: headC.y)
ctx.rotate(by: 0.16)
ctx.setFillColor(rgb(0x1a1f55))
for dx in [-24.0, 30.0] {
    ctx.addPath(CGPath(roundedRect: CGRect(x: dx - 9, y: -8, width: 18, height: 34), cornerWidth: 9, cornerHeight: 9, transform: nil))
}
ctx.fillPath()
ctx.restoreGState()

// ── 篮球 ────────────────────────────────────────────────────────────
ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -6), blur: 16, color: CGColor(gray: 0, alpha: 0.5))
ctx.setFillColor(rgb(0xff8a1f))
ctx.fillEllipse(in: CGRect(x: ball.x - ballR, y: ball.y - ballR, width: ballR * 2, height: ballR * 2))
ctx.restoreGState()
ctx.saveGState()
ctx.addEllipse(in: CGRect(x: ball.x - ballR, y: ball.y - ballR, width: ballR * 2, height: ballR * 2))
ctx.clip()
let bg2 = CGGradient(colorsSpace: space, colors: [rgb(0xffb04a), rgb(0xff8a1f), rgb(0xe0640c)] as CFArray, locations: [0, 0.5, 1])!
ctx.drawRadialGradient(bg2, startCenter: CGPoint(x: ball.x - 22, y: ball.y + 26), startRadius: 0, endCenter: ball, endRadius: ballR * 1.15, options: [])
ctx.translateBy(x: ball.x, y: ball.y)
ctx.rotate(by: -0.42)
ctx.setStrokeColor(rgb(0x5a2406))
ctx.setLineWidth(7)
ctx.setLineCap(.round)
ctx.move(to: CGPoint(x: -ballR, y: 0)); ctx.addLine(to: CGPoint(x: ballR, y: 0))
ctx.move(to: CGPoint(x: 0, y: -ballR)); ctx.addLine(to: CGPoint(x: 0, y: ballR))
ctx.strokePath()
for sx in [-1.0, 1.0] {
    ctx.addArc(center: CGPoint(x: sx * ballR * 1.28, y: 0), radius: ballR * 0.92, startAngle: 0, endAngle: .pi * 2, clockwise: false)
    ctx.strokePath()
}
ctx.restoreGState()

// 运球的动感线
ctx.setStrokeColor(rgb(0xffffff, 0.55))
ctx.setLineWidth(9)
ctx.setLineCap(.round)
for (k, r) in [ballR + 26, ballR + 50].enumerated() {
    let span: CGFloat = k == 0 ? 0.42 : 0.30
    ctx.addArc(center: ball, radius: r, startAngle: -.pi / 2 - span, endAngle: -.pi / 2 + span, clockwise: false)
    ctx.strokePath()
}

ctx.restoreGState()   // 底板裁剪

// 底板上沿的一道高光
ctx.saveGState()
ctx.addPath(platePath)
ctx.clip()
ctx.addPath(CGPath(roundedRect: plate.insetBy(dx: 2, dy: 2), cornerWidth: 183, cornerHeight: 183, transform: nil))
ctx.setStrokeColor(rgb(0xffffff, 0.14))
ctx.setLineWidth(4)
ctx.strokePath()
ctx.restoreGState()

let img = ctx.makeImage()!
let rep = NSBitmapImageRep(cgImage: img)
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))
print("wrote \(out)")
