import AppKit
import AVFoundation
import CoreAudio

/// 简单的能量 / 鼓点检测。音频线程喂样本，主线程每帧取结果。
final class BeatAnalyzer {
    struct Onset { var time: Double; var strength: Float }
    struct Features { var onsets: [Onset] = []; var level: Float = 0; var active = false }

    private let lock = NSLock()
    private var pending: [(time: Double, strength: Float)] = []
    private var levelOut: Float = 0
    private var lastLoud: Double = -100
    private var dbgCalls = 0, dbgOnsets = 0
    private var dbgMaxFull: Float = 0, dbgLastStart: Double = 0

    /// 调试信息：收到多少段音频、检出多少鼓点、最大音量、时间戳与现在差多少秒。
    func debugSummary(now: Double) -> String {
        lock.lock(); defer { lock.unlock() }
        return "calls=\(dbgCalls) onsets=\(dbgOnsets) maxRMS=\(dbgMaxFull) startMinusNow=\(String(format: "%.3f", dbgLastStart - now)) pending=\(pending.count)"
    }

    // 以下只在音频线程里用
    private let hop = 512
    private var lp: Float = 0
    private var accLow: Float = 0, accFull: Float = 0
    private var accCount = 0
    private var prevLow: Float = 0, prevFull: Float = 0
    private var fluxHist = [Float](repeating: 0, count: 64)
    private var fluxIdx = 0
    private var peak: Float = 1e-4
    private var smoothed: Float = 0
    private var lastOnset: Double = -100

    func reset() {
        lock.lock(); pending.removeAll(); levelOut = 0; lastLoud = -100; lock.unlock()
    }

    /// samples：单声道 float。startTime：这段声音真正响起的时刻（与 CACurrentMediaTime 同一时钟）。
    func process(_ samples: UnsafePointer<Float>, count: Int, sampleRate: Double, startTime: Double) {
        guard count > 0, sampleRate > 0 else { return }
        let a = Float(exp(-2 * Double.pi * 160 / sampleRate))       // 160Hz 低通，抓底鼓
        var found: [(Double, Float)] = []
        var loudAt: Double = -1
        for i in 0..<count {
            let x = samples[i]
            lp = a * lp + (1 - a) * x
            accLow += lp * lp
            accFull += x * x
            accCount += 1
            if accCount >= hop {
                let low = (accLow / Float(hop)).squareRoot()
                let full = (accFull / Float(hop)).squareRoot()
                accLow = 0; accFull = 0; accCount = 0
                let t = startTime + Double(i) / sampleRate
                let flux = max(0, low - prevLow) + 0.4 * max(0, full - prevFull)
                prevLow = low; prevFull = full
                var mean: Float = 0
                for v in fluxHist { mean += v }
                mean /= Float(fluxHist.count)
                var varSum: Float = 0
                for v in fluxHist { varSum += (v - mean) * (v - mean) }
                let std = (varSum / Float(fluxHist.count)).squareRoot()
                fluxHist[fluxIdx] = flux
                fluxIdx = (fluxIdx + 1) % fluxHist.count
                if flux > mean * 1.5 + std * 1.4 + 0.004, t - lastOnset > 0.24 {
                    lastOnset = t
                    found.append((t, min(1, flux / (mean + std * 3 + 0.02))))
                }
                peak = max(full, peak * 0.9997)
                let lv = min(1, full / (peak + 1e-5))
                smoothed += (lv - smoothed) * (lv > smoothed ? 0.5 : 0.08)
                if full > 0.003 { loudAt = t }
            }
        }
        lock.lock()
        dbgCalls += 1; dbgOnsets += found.count; dbgMaxFull = max(dbgMaxFull, sqrt(peak * peak)); dbgLastStart = startTime
        pending.append(contentsOf: found.map { (time: $0.0, strength: $0.1) })
        if pending.count > 64 { pending.removeFirst(pending.count - 64) }
        levelOut = smoothed
        if loudAt > 0 { lastLoud = loudAt }
        lock.unlock()
    }

    /// 取出「到现在为止已经响过」的鼓点。
    func drain(now: Double) -> Features {
        lock.lock(); defer { lock.unlock() }
        var f = Features()
        var rest: [(time: Double, strength: Float)] = []
        for o in pending { if o.time <= now { f.onsets.append(Onset(time: o.time, strength: o.strength)) } else { rest.append(o) } }
        pending = rest
        f.level = levelOut
        f.active = now - lastLoud < 1.2
        return f
    }
}

/// 把鼓点变成连续的拍子相位。没有声音时就是一个 120 BPM 的节拍器。
final class BeatClock {
    private(set) var bpm: Double = 120
    private(set) var beat: Double = 0
    private(set) var energy: Double = 0.55
    private(set) var pulse: Double = 0
    private(set) var locked = false
    private var lastOnset: Double = -100
    private var iois: [Double] = []
    private var strength: Double = 0.6

    func reset() { beat = 0; pulse = 0; energy = 0.55; iois.removeAll(); lastOnset = -100; locked = false; strength = 0.6 }

    @discardableResult
    func update(dt: Double, now: Double, features f: BeatAnalyzer.Features, speed: Double) -> Double {
        locked = f.active
        if f.active {
            energy += (Double(f.level) - energy) * min(1, dt * 8)
            for o in f.onsets {
                // 用鼓点真实响起的时刻（音频回调是一批一批送来的，会晚 0.1~0.2 秒）
                strength += (Double(o.strength) - strength) * 0.3
                if iois.count < 4 { pulse = max(pulse, 0.55 + 0.45 * Double(o.strength)) }   // 还没摸清速度时，先跟着鼓点闪
                let ioi = o.time - lastOnset
                lastOnset = o.time
                if ioi > 0.2 && ioi < 2.0 {
                    var x = ioi
                    while x < 0.4 { x *= 2 }            // 折到 75~150 BPM
                    while x >= 0.8 { x /= 2 }
                    iois.append(x)
                    if iois.count > 16 { iois.removeFirst() }
                    if iois.count >= 4 {
                        let med = iois.sorted()[iois.count / 2]
                        bpm += (60 / med - bpm) * 0.2
                    }
                }
                if abs(speed - 1) < 0.06 {              // 速度为 1 时把相位往鼓点上靠：鼓点那一刻应该正好是整数拍
                    let atOnset = beat - (now - o.time) * bpm / 60 * speed
                    let err = atOnset - atOnset.rounded()
                    if abs(err) < 0.3 { beat -= err * 0.25 }
                }
            }
        } else {
            energy += (0.55 - energy) * min(1, dt * 2)
        }
        let prev = beat
        let d = dt * bpm / 60 * speed
        beat += d
        if floor(beat) > floor(prev) { pulse = max(pulse, f.active ? (iois.count >= 4 ? 0.5 + 0.5 * strength : 0.3) : 0.9) }
        pulse *= exp(-dt * 7)
        return d
    }
}

/// 播放用户选的本地音频文件（循环），并把声音送去做节拍检测。不需要任何权限。
final class FilePlayer {
    private let engine = AVAudioEngine()
    private let node = AVAudioPlayerNode()
    private var file: AVAudioFile?
    private var generation = 0
    private var attached = false
    private var tapped = false
    private(set) var duration: Double = 0
    private(set) var isPlaying = false

    func start(url: URL, analyzer: BeatAnalyzer) throws {
        stop()
        let f = try AVAudioFile(forReading: url)
        file = f
        duration = Double(f.length) / f.processingFormat.sampleRate
        if !attached { engine.attach(node); attached = true }
        engine.connect(node, to: engine.mainMixerNode, format: f.processingFormat)
        let latency = engine.outputNode.presentationLatency
        engine.mainMixerNode.installTap(onBus: 0, bufferSize: 1024, format: nil) { buf, when in
            guard let ch = buf.floatChannelData else { return }
            let n = Int(buf.frameLength), c = Int(buf.format.channelCount)
            guard n > 0, c > 0 else { return }
            var mono = [Float](repeating: 0, count: n)
            for k in 0..<c { let p = ch[k]; for j in 0..<n { mono[j] += p[j] } }
            if c > 1 { let inv = 1 / Float(c); for j in 0..<n { mono[j] *= inv } }
            let t0 = when.isHostTimeValid ? AVAudioTime.seconds(forHostTime: when.hostTime) : CACurrentMediaTime()
            mono.withUnsafeBufferPointer { analyzer.process($0.baseAddress!, count: n, sampleRate: buf.format.sampleRate, startTime: t0 + latency) }
        }
        tapped = true
        try engine.start()
        scheduleLoop()
        node.play()
        isPlaying = true
    }

    private func scheduleLoop() {
        guard let f = file else { return }
        let gen = generation
        node.scheduleFile(f, at: nil) { [weak self] in
            DispatchQueue.main.async {
                guard let self, self.generation == gen, self.file != nil else { return }
                self.scheduleLoop()
            }
        }
    }

    /// 当前播放到歌曲的第几秒（循环后回到 0），给歌词用。
    var currentTime: Double? {
        guard file != nil, duration > 0, let nt = node.lastRenderTime, let pt = node.playerTime(forNodeTime: nt) else { return nil }
        let t = Double(pt.sampleTime) / pt.sampleRate
        return t < 0 ? 0 : t.truncatingRemainder(dividingBy: duration)
    }

    func pause() { if isPlaying { node.pause(); isPlaying = false } }
    func resume() { if file != nil, !isPlaying { node.play(); isPlaying = true } }

    func stop() {
        generation += 1
        if file != nil { node.stop() }
        if tapped { engine.mainMixerNode.removeTap(onBus: 0); tapped = false }
        if engine.isRunning { engine.stop() }
        file = nil
        isPlaying = false
    }
}

/// 听电脑正在播放的声音（网易云、抖音、浏览器…），只用来检测节拍。
/// 用 Core Audio 的「进程监听」(process tap)：只需要「仅系统录音」权限，不需要「录屏」权限；
/// 不录音、不保存、不碰麦克风（聚合设备里把输出设备的输入通道数设为 0）。
/// 监听和聚合设备都是本进程私有的，进程退出就自动消失。
final class SystemAudioCapture {
    private let analyzer: BeatAnalyzer
    private let queue = DispatchQueue(label: "desktopdance.sysaudio")
    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggID = AudioObjectID(kAudioObjectUnknown)
    private var procID: AudioDeviceIOProcID?
    private var format = AudioStreamBasicDescription()
    private var lastSound: Double = 0          // 最近一次听到非静音的时刻
    private var callbacks = 0
    var onStopped: ((String) -> Void)?
    private(set) var running = false

    init(analyzer: BeatAnalyzer) { self.analyzer = analyzer }

    /// 最近 4 秒内有没有听到声音。没在放歌、或系统没允许「系统录音」时都是 false（后者系统只给静音，不报错）。
    var hearing: Bool { CACurrentMediaTime() - lastSound < 4 }

    private var generation = 0            // 只在主线程读写：每次 start/stop 加一，过期的启动结果作废
    private var wanted = false            // 只在 queue 上读写：现在是否应该在监听
    private var listenerInstalled = false

    /// 在主线程调用。连接是异步的；期间如果又 stop 或 start 了，这次的结果作废。
    func start(completion: @escaping (String?) -> Void) {
        generation += 1
        let g = generation
        queue.async {
            self.wanted = true
            self.tearDown()                       // 上一次的监听如果还在，先撤干净，避免叠两套
            let err = self.setUp()
            if err != nil { self.tearDown() }
            self.installDeviceListener()
            DispatchQueue.main.async {
                guard g == self.generation else { return }
                self.running = err == nil
                completion(err)
            }
        }
    }

    /// 在主线程调用。即使还在连接中也有效：撤销排在连接之后执行。
    func stop() {
        generation += 1
        running = false
        queue.async {
            self.wanted = false
            self.tearDown()
        }
    }

    /// 换了输出设备（插拔耳机、连上音箱）时，原来的监听挂在旧设备上会没声音：这里自动重接一次。
    private func installDeviceListener() {
        guard !listenerInstalled else { return }
        listenerInstalled = true
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                              mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &addr, queue) { [weak self] _, _ in
            guard let self, self.wanted else { return }
            self.tearDown()
            if let err = self.setUp() {
                self.tearDown()
                DispatchQueue.main.async {
                    self.running = false
                    self.onStopped?(err)
                }
            }
        }
    }

    private func setUp() -> String? {
        // 1. 全局监听：所有进程的输出混成立体声
        let desc = CATapDescription(stereoGlobalTapButExcludeProcesses: [])
        desc.name = "DesktopDance"
        desc.uuid = UUID()
        desc.isPrivate = true
        desc.muteBehavior = .unmuted
        var tap = AudioObjectID(kAudioObjectUnknown)
        var st = AudioHardwareCreateProcessTap(desc, &tap)
        guard st == noErr, tap != kAudioObjectUnknown else { return "建不了系统声音监听（\(st)）" }
        tapID = tap

        var addr = AudioObjectPropertyAddress(mSelector: kAudioTapPropertyFormat, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        st = AudioObjectGetPropertyData(tap, &addr, 0, nil, &size, &format)
        guard st == noErr, format.mSampleRate > 0 else { return "读不到系统声音格式（\(st)）" }

        // 2. 当前输出设备，只借它的时钟
        var dev = AudioDeviceID(kAudioObjectUnknown)
        addr.mSelector = kAudioHardwarePropertyDefaultOutputDevice
        size = UInt32(MemoryLayout<AudioDeviceID>.size)
        st = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &dev)
        guard st == noErr, dev != kAudioObjectUnknown else { return "找不到输出设备（\(st)）" }
        var uidRef: Unmanaged<CFString>?
        addr.mSelector = kAudioDevicePropertyDeviceUID
        size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        st = AudioObjectGetPropertyData(dev, &addr, 0, nil, &size, &uidRef)
        guard st == noErr, let outUID = uidRef?.takeRetainedValue() as String? else { return "读不到输出设备 ID（\(st)）" }

        // 3. 私有聚合设备 = 输出设备（输入通道 0，不碰麦克风）+ 监听
        let aggDesc: [String: Any] = [
            kAudioAggregateDeviceNameKey: "DesktopDance-Tap",
            kAudioAggregateDeviceUIDKey: UUID().uuidString,
            kAudioAggregateDeviceMainSubDeviceKey: outUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: outUID, kAudioSubDeviceInputChannelsKey: 0]],
            kAudioAggregateDeviceTapListKey: [[kAudioSubTapDriftCompensationKey: true, kAudioSubTapUIDKey: desc.uuid.uuidString]],
        ]
        var agg = AudioObjectID(kAudioObjectUnknown)
        st = AudioHardwareCreateAggregateDevice(aggDesc as CFDictionary, &agg)
        guard st == noErr, agg != kAudioObjectUnknown else { return "建不了聚合设备（\(st)）" }
        aggID = agg

        // 4. 开始收样本
        var pid: AudioDeviceIOProcID?
        st = AudioDeviceCreateIOProcIDWithBlock(&pid, agg, queue) { [weak self] _, input, _, _, _ in
            self?.handle(input)
        }
        guard st == noErr, let pid else { return "接不上系统声音（\(st)）" }
        procID = pid
        st = AudioDeviceStart(agg, pid)
        guard st == noErr else { return "系统声音启动失败（\(st)）" }
        return nil
    }

    private func tearDown() {
        if aggID != kAudioObjectUnknown {
            if let p = procID {
                AudioDeviceStop(aggID, p)
                AudioDeviceDestroyIOProcID(aggID, p)
            }
            AudioHardwareDestroyAggregateDevice(aggID)
        }
        if tapID != kAudioObjectUnknown { AudioHardwareDestroyProcessTap(tapID) }
        procID = nil
        aggID = AudioObjectID(kAudioObjectUnknown)
        tapID = AudioObjectID(kAudioObjectUnknown)
    }

    private func handle(_ input: UnsafePointer<AudioBufferList>) {
        guard format.mFormatFlags & kAudioFormatFlagIsFloat != 0, format.mBitsPerChannel == 32 else { return }
        let abl = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: input))
        guard let first = abl.first, first.mData != nil else { return }
        let sz = MemoryLayout<Float>.size
        let chPerBuf = max(1, Int(first.mNumberChannels))
        let frames = Int(first.mDataByteSize) / (sz * chPerBuf)
        guard frames > 0 else { return }
        var mono = [Float](repeating: 0, count: frames)
        var used = 0
        for b in abl {
            guard let d = b.mData else { continue }
            let ch = max(1, Int(b.mNumberChannels))
            let n = min(frames, Int(b.mDataByteSize) / (sz * ch))
            let p = d.assumingMemoryBound(to: Float.self)
            for j in 0..<n { var v: Float = 0; for c in 0..<ch { v += p[j * ch + c] }; mono[j] += v }
            used += ch
        }
        var peak: Float = 0
        let inv = 1 / Float(max(1, used))
        for j in 0..<frames { mono[j] *= inv; peak = max(peak, abs(mono[j])) }
        callbacks += 1
        if peak > 0.0005 { lastSound = CACurrentMediaTime() }
        let sr = format.mSampleRate
        mono.withUnsafeBufferPointer {
            analyzer.process($0.baseAddress!, count: frames, sampleRate: sr, startTime: CACurrentMediaTime() - Double(frames) / sr)
        }
    }
}
