import Foundation

/// LRC 歌词：每行形如 [mm:ss.xx]歌词，一行可以带多个时间标签。
struct Lyrics {
    struct Line { let time: Double; let text: String }
    let lines: [Line]

    /// 占位词（不是任何真实歌曲的歌词）。换成自己的：启动时加 `--lyrics 你的文件.lrc`。
    static let placeholderText = """
    [00:00.00]🎵 占位歌词
    [00:04.00]桌面图标排好队 💃
    [00:08.00]跟着节拍左右摆 🕺
    [00:12.00]一二三四 扭起来 🐍
    [00:16.00]篮球在手 运起来 🏀
    [00:20.00]全场一起蹦 ✨
    """

    static var placeholder: Lyrics { parse(placeholderText) }

    static func parse(_ text: String) -> Lyrics {
        guard let re = try? NSRegularExpression(pattern: #"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]"#) else { return Lyrics(lines: []) }
        var offset: Double = 0
        var out: [Line] = []
        for raw in text.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.lowercased().hasPrefix("[offset:") {
                let v = line.dropFirst(8).dropLast().trimmingCharacters(in: .whitespaces)
                offset = (Double(v) ?? 0) / 1000
                continue
            }
            let ns = line as NSString
            let matches = re.matches(in: line, range: NSRange(location: 0, length: ns.length))
            guard let last = matches.last else { continue }
            let body = ns.substring(from: last.range.location + last.range.length).trimmingCharacters(in: .whitespaces)
            for m in matches {
                let mm = Double(ns.substring(with: m.range(at: 1))) ?? 0
                let secText = ns.substring(with: m.range(at: 2)).replacingOccurrences(of: ":", with: ".")
                let ss = Double(secText) ?? 0
                out.append(Line(time: max(0, mm * 60 + ss - offset), text: body))
            }
        }
        return Lyrics(lines: out.sorted { $0.time < $1.time })
    }

    static func load(url: URL) -> Lyrics? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        let gb18030 = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.GB_18030_2000.rawValue)))
        for enc in [String.Encoding.utf8, .utf16, gb18030] {
            if let s = String(data: data, encoding: enc) {
                let l = parse(s)
                if !l.lines.isEmpty { return l }
            }
        }
        return nil
    }

    /// 没有歌曲时长可参考时，歌词按这个长度循环。
    var loopLength: Double { (lines.last?.time ?? 0) + 4 }

    func text(at t: Double) -> String {
        var cur = ""
        for l in lines { if l.time <= t { cur = l.text } else { break } }
        return cur
    }
}
