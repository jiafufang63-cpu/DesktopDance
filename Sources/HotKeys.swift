import AppKit
import Carbon.HIToolbox

/// 全局热键（Carbon RegisterEventHotKey：不需要「辅助功能」权限）。
final class HotKeys {
    static var handler: ((UInt32) -> Void)?
    private var refs: [UInt32: EventHotKeyRef] = [:]
    private static var installed = false

    init() {
        guard !HotKeys.installed else { return }
        HotKeys.installed = true
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var hk = EventHotKeyID()
            let st = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                       nil, MemoryLayout<EventHotKeyID>.size, nil, &hk)
            if st == noErr { HotKeys.handler?(hk.id) }
            return noErr
        }, 1, &spec, nil, nil)
    }

    @discardableResult
    func register(id: UInt32, keyCode: Int, modifiers: Int) -> Bool {
        unregister(id: id)
        var ref: EventHotKeyRef?
        let hkID = EventHotKeyID(signature: OSType(0x4444_4E43), id: id)   // 'DDNC'
        let st = RegisterEventHotKey(UInt32(keyCode), UInt32(modifiers), hkID, GetApplicationEventTarget(), 0, &ref)
        if st == noErr, let ref { refs[id] = ref; return true }
        dlog("热键注册失败 id=\(id) status=\(st)")
        return false
    }

    func unregister(id: UInt32) {
        if let r = refs.removeValue(forKey: id) { UnregisterEventHotKey(r) }
    }

    func isRegistered(_ id: UInt32) -> Bool { refs[id] != nil }
}

enum HotKeyID {
    static let restore: UInt32 = 1     // 不绑按键，只给自测信号 SIGUSR1 用
    static let escape: UInt32 = 2      // Esc：只在跳舞期间占用
}
