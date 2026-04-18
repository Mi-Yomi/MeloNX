//
//  Ryujinx.swift
//  MeloNX
//
//  Created by Stossy11 on 13/4/2026.
//

import MetalKit

final class Ryujinx {
    static var emulationView: MTKView?
    static func initialize() {
        MeloNX.initialize()
    }
    
    
    static func mainRyu(argv: [String]) -> Int {
        return argv.withCStrings { cStrings, argc in
            Int(MeloNX.main_ryujinx_sdl(argc, cStrings))
        }
    }
    
    static func setNativeWindow(_ layerPtr: UnsafeMutableRawPointer) {
        MeloNX.set_native_window(layerPtr)
    }
    
    /*
    static func initialize_dualmapped() -> Bool {
        MeloNX.initialize_dualmapped()
    }

    static func getGameInfo(arg0: Int32, arg1: NSString) -> GameInfo {
        let arg1Ptr = UnsafeMutablePointer<CChar>(mutating: arg1.utf8String)
        return MeloNX.get_game_info(arg0, arg1Ptr)
    }

    static func getDlcList(titleId: String, path: String) -> DlcNcaList {
        titleId.withCString { titlePtr in
            path.withCString { pathPtr in
                return MeloNX.get_dlc_nca_list(titlePtr, pathPtr)
            }
        }
    }

    static func installFirmware(at path: String) -> (string: String, isError: Bool) {
        guard let firmware = (path.withCString { MeloNX.install_firmware($0) }) else { return ("Failed to get error.", true) }
        var string = String(cString: firmware)
        let isErr = string.hasSuffix("✖")
        string.removeLast()
        defer { MeloNX.free_firmware_version(firmware) }
        return (string, isErr)
    }

    static var installedFirmwareVersion: String {
        guard let firmware = MeloNX.installed_firmware_version() else { return "" }
        defer { MeloNX.free_firmware_version(firmware) }
        return String(cString: firmware)
    }

    static func pauseEmulation(_ shouldPause: Bool) {
        MeloNX.pause_emulation(shouldPause)
    }

    static func stopEmulation() {
        MeloNX.stop_emulation()
    }

    static func mainRyu(argv: [String]) -> Int {
        return argv.withCStrings { cStrings, argc in
            Int(MeloNX.main_ryujinx_sdl(argc, cStrings))
        }
    }
    
    static func changeControllerInfo(argv: [String]) {
        argv.withCStrings { cStrings, argc in
            MeloNX.set_gamepad_configuration(argc, cStrings)
        }
    }

    static func updateSettingsExternal(argv: [String]) -> Int {
        return argv.withCStrings { cStrings, argc in
            Int(MeloNX.update_settings_external(argc, cStrings))
        }
    }
    
    static func setViewSize(width: Int, height: Int) {
        MeloNX.set_view_size(Int32(width), Int32(height))
    }

    static var currentFPS: Int {
        Int(MeloNX.get_current_fps())
    }
    
    static var currentVolume: Float {
        get {
            MeloNX.get_game_volume()
        } set {
            MeloNX.set_game_volume(newValue)
        }
    }


    static func touchBegan(x: Float, y: Float, index: Int) {
        MeloNX.touch_began(x, y, Int32(index))
    }

    static func touchMoved(x: Float, y: Float, index: Int) {
        MeloNX.touch_moved(x, y, Int32(index))
    }

    static func touchEnded(index: Int) {
        MeloNX.touch_ended(Int32(index))
    }

    static func refreshAccountManager() {
        MeloNX.refresh_account_manager()
    }

    static func createAccount(name: String, image: Data) {
        name.withCString { namePtr in
            image.withUnsafeBytes { bufferpointer in
                let imagePtr = bufferpointer.baseAddress!.assumingMemoryBound(to: UInt8.self)
                
                MeloNX.create_account(namePtr, imagePtr, Int32(image.count))
            }
        }
    }

    static func openUser(userId: String) {
        userId.withCString { MeloNX.open_user($0) }
    }

    static func closeUser(userId: String) {
        userId.withCString { MeloNX.close_user($0) }
    }
    
    static func attachGamepad(_ id: UnsafeMutableRawPointer?, _ name: String) {
        _ = name.withCString { MeloNX.attach_gamepad($0, id)  }
    }
    
    static func detachGamepad(_ id: UnsafeMutableRawPointer?) {
        MeloNX.detach_gamepad(id)
    }

    static func setGamepadButtonState(_ id: UnsafeMutableRawPointer?, buttonId: Int, pressed: Bool) {
        print("Gamepad button State \(Int32(buttonId)), pressed \(pressed)")
        MeloNX.set_gamepad_button_state(id, Int32(buttonId), pressed ? 1 : 0)
    }

    static func setGamepadStickAxis(_ id: UnsafeMutableRawPointer?, stickId: Int, x: Float, y: Float) {
        MeloNX.set_gamepad_stick_axis(id, Int32(stickId), x, y)
    }
    
    static func setGamepadMotion(_ id: UnsafeMutableRawPointer?, motionType: Int, axis: SIMD3<Float>) {
        MeloNX.set_gamepad_motion_axis(id, Int32(motionType), axis.x, axis.y, axis.z)
    }

    static var avatars: AvatarArray {
        MeloNX.get_avatars()
    }
     */
}

fileprivate extension Array where Element == String {
    func withCStrings<R>(_ body: (UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>, Int32) -> R) -> R {
        var cStrings = map { strdup($0) }
        defer { cStrings.forEach { free($0) } }
        return cStrings.withUnsafeMutableBufferPointer { buf in
            guard let base = buf.baseAddress else {
                fatalError("Failed to get baseAddress")
            }
            return body(base, Int32(count))
        }
    }
}



@_silgen_name("set_native_window")
fileprivate func set_native_window(_ layerPtr: UnsafeMutableRawPointer!)

@_silgen_name("main_ryujinx_sdl")
fileprivate func main_ryujinx_sdl(_ argc: Int32, _ argv: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>!) -> Int32

@_silgen_name("initialize")
fileprivate func initialize()
