//
//  RyujinxBridge.swift
//  MeloNX
//
//  Created by Stossy11 on 30/09/2025.
//

import Foundation

final class RyujinxBridge {
    static func initialize() {
        SN_initialize()
    }

    static func getGameInfo(arg0: Int32, arg1: NSString) -> GameInfo {
        let arg1Ptr = UnsafeMutablePointer<CChar>(mutating: arg1.utf8String)
        return SN_get_game_info(arg0, arg1Ptr)
    }

    static func getDlcList(titleId: String, path: String) -> DlcNcaList {
        titleId.withCString { titlePtr in
            path.withCString { pathPtr in
                return SN_get_dlc_nca_list(titlePtr, pathPtr)
            }
        }
    }

    static func installFirmware(at path: String) {
        path.withCString { SN_install_firmware($0) }
    }

    static var installedFirmwareVersion: String {
        String(cString: SN_installed_firmware_version())
    }

    static func pauseEmulation(_ shouldPause: Bool) {
        SN_pause_emulation(shouldPause)
    }

    static func stopEmulation() {
        SN_stop_emulation()
    }

    static func mainRyu(argv: [String]) -> Int {
        return argv.withCStrings { cStrings, argc in
            Int(SN_main_ryujinx_sdl(argc, cStrings))
        }
    }

    static func updateSettingsExternal(argv: [String]) -> Int {
        return argv.withCStrings { cStrings, argc in
            Int(SN_update_settings_external(argc, cStrings))
        }
    }
    
    static func setViewSize(width: Int, height: Int) {
        SN_set_view_size(Int32(width), Int32(height))
    }

    static var currentFPS: Int {
        Int(SN_get_current_fps())
    }

    static func touchBegan(x: Float, y: Float, index: Int) {
        SN_touch_began(x, y, Int32(index))
    }

    static func touchMoved(x: Float, y: Float, index: Int) {
        SN_touch_moved(x, y, Int32(index))
    }

    static func touchEnded(index: Int) {
        SN_touch_ended(Int32(index))
    }

    static func setNativeWindow(_ layerPtr: UnsafeMutableRawPointer) {
        SN_set_native_window(layerPtr)
    }

    static func refreshAccountManager() {
        SN_refresh_account_manager()
    }

    static func createAccount(name: String, image: String) {
        name.withCString { namePtr in
            image.withCString { imagePtr in
                SN_create_account(UnsafeMutablePointer(mutating: namePtr),
                               UnsafeMutablePointer(mutating: imagePtr),
                               Int32(image.count))
            }
        }
    }

    static func openUser(userId: String) {
        userId.withCString { SN_open_user(UnsafeMutablePointer(mutating: $0)) }
    }

    static func closeUser(userId: String) {
        userId.withCString { SN_close_user(UnsafeMutablePointer(mutating: $0)) }
    }
    
    static func addGamepadHandle(_ opaquePtr: OpaquePointer, _ id: String) {
        id.withCString { SN_add_gamepad_handle(UnsafeMutableRawPointer(opaquePtr), UnsafeMutablePointer(mutating: $0)) }
    }

    static var avatars: AvatarArray {
        SN_get_avatars()
    }
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

@_silgen_name("get_game_info")
func SN_get_game_info(_ arg0: Int32, _ arg1: UnsafeMutablePointer<CChar>!) -> GameInfo

@_silgen_name("get_dlc_nca_list")
func SN_get_dlc_nca_list(_ titleIdPtr: UnsafePointer<CChar>!, _ pathPtr: UnsafePointer<CChar>!) -> DlcNcaList

@_silgen_name("install_firmware")
func SN_install_firmware(_ inputPtr: UnsafePointer<CChar>!)

@_silgen_name("installed_firmware_version")
func SN_installed_firmware_version() -> UnsafeMutablePointer<CChar>!

@_silgen_name("set_native_window")
func SN_set_native_window(_ layerPtr: UnsafeMutableRawPointer!)

@_silgen_name("pause_emulation")
func SN_pause_emulation(_ shouldPause: Bool)

@_silgen_name("stop_emulation")
func SN_stop_emulation()

@_silgen_name("initialize")
func SN_initialize()

@_silgen_name("main_ryujinx_sdl")
func SN_main_ryujinx_sdl(_ argc: Int32, _ argv: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>!) -> Int32

@_silgen_name("update_settings_external")
func SN_update_settings_external(_ argc: Int32, _ argv: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>!) -> Int32

@_silgen_name("get_current_fps")
func SN_get_current_fps() -> Int32

@_silgen_name("touch_began")
func SN_touch_began(_ x: Float, _ y: Float, _ index: Int32)

@_silgen_name("touch_moved")
func SN_touch_moved(_ x: Float, _ y: Float, _ index: Int32)

@_silgen_name("touch_ended")
func SN_touch_ended(_ index: Int32)

@_silgen_name("refresh_account_manager")
func SN_refresh_account_manager()

@_silgen_name("create_account")
func SN_create_account(_ name: UnsafeMutablePointer<CChar>!, _ image: UnsafeMutablePointer<CChar>!, _ imagelength: Int32)

@_silgen_name("open_user")
func SN_open_user(_ userid: UnsafeMutablePointer<CChar>!)

@_silgen_name("close_user")
func SN_close_user(_ userid: UnsafeMutablePointer<CChar>!)

@_silgen_name("get_avatars")
func SN_get_avatars() -> AvatarArray

@_silgen_name("set_view_size")
func SN_set_view_size(_ width: Int32, _ height: Int32)

@_silgen_name("add_gamepad_handle")
func SN_add_gamepad_handle(_ inputPtr: UnsafeMutableRawPointer?, _ id: UnsafeMutablePointer<CChar>!)
