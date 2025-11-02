//
//  BaseController.swift
//  MeloNX
//
//  Created by Stossy11 on 19/10/2025.
//

import Foundation
import CoreHaptics
import UIKit
import GameController

class BaseController: Equatable, Identifiable {
    var id: String = ""
    var type: ControllerType
    var virtual: Bool = false
    
    var name: String { virtual ? "MeloNX Virtual Controller" : nativeController?.vendorName ?? "Unknown" }
    
    var nativeController: GCController?
    private var instanceID: SDL_JoystickID = -1
    private var controller: OpaquePointer?
    private var joystick: OpaquePointer?
    
    private var hapticEngine: CHHapticEngine?
    private var rumbleController: RumbleController?
    
    private var usesDeviceHandlers: Bool { virtual ? true : (name.lowercased() == "Joy-Con (l/R)".lowercased() ||
                                                      name.lowercased().hasSuffix("backbone") ||
                                                      name.lowercased() == "backbone one") }
    
    private var playerSlot: UInt8?
    var motionProvider: DSUMotionProvider?

    
    init(nativeController: GCController?) {
        virtual = nativeController == nil
        self.nativeController = nativeController
        type = virtual ? .joyconPair : .proController
        id = setupHandheldController() ?? ""
    }
    
    private func setupHandheldController() -> String? {
        if !id.isEmpty {
            return id
        }
        
        if SDL_WasInit(Uint32(SDL_INIT_GAMECONTROLLER)) == 0 {
            SDL_InitSubSystem(Uint32(SDL_INIT_GAMECONTROLLER))
        }
        
        var joystickDesc = SDL_VirtualJoystickDesc(
            version: UInt16(SDL_VIRTUAL_JOYSTICK_DESC_VERSION),
            type: Uint16(SDL_JOYSTICK_TYPE_GAMECONTROLLER.rawValue),
            naxes: 6,
            nbuttons: 15,
            nhats: 1,
            vendor_id: 0,
            product_id: 0,
            padding: 0,
            button_mask: 0,
            axis_mask: 0,
            name: (name as NSString).utf8String,
            userdata: Unmanaged.passUnretained(self).toOpaque(),
            Update: { userdata in
            },
            SetPlayerIndex: { userdata, playerIndex in
                guard let userdata, let player = GCControllerPlayerIndex(rawValue: Int(playerIndex)) else { return }
                let _self = Unmanaged<BaseController>.fromOpaque(userdata).takeUnretainedValue()
                _self.nativeController?.playerIndex = player
            },
            Rumble: { userdata, lowFreq, highFreq in
                guard let userdata else { return 0 }
                let _self = Unmanaged<BaseController>.fromOpaque(userdata).takeUnretainedValue()
                _self.rumbleController?.rumble(lowFreq: Float(lowFreq), highFreq: Float(highFreq))
                return 0
            },
            RumbleTriggers: { userdata, leftRumble, rightRumble in
                return 0
            },
            SetLED: { userdata, red, green, blue in
                guard let userdata else { return 0 }
                let _self = Unmanaged<BaseController>.fromOpaque(userdata).takeUnretainedValue()
                guard let light = _self.nativeController?.light else { return 0 }
                light.color = .init(red: Float(red), green: Float(green), blue: Float(blue))
                return 0
            },
            SendEffect: { userdata, data, size in
                return 0
            }
        )
        
        instanceID = SDL_JoystickAttachVirtualEx(&joystickDesc)
        print("Controller \(name) registered with instanceID: \(instanceID)")

        if instanceID < 0 {
            print("Failed to register controller: \(String(cString: SDL_GetError()))")
            return nil
        }
        
        controller = SDL_GameControllerOpen(Int32(instanceID))
        
        if controller == nil {
            print("failedSDLController")
            return nil
        }
        
        joystick = SDL_JoystickFromInstanceID(instanceID)
        
        if joystick == nil {
            print("failed to get joystick")
            return nil
        }
        
        let id = getGamePadId()
        
        if let id {
            RyujinxBridge.addGamepadHandle(controller!, id)
        }
        
        setupHaptics()
        
        return id
    }
    
    func setupHaptics() {
        func createHaptic(_ device: Bool = false) {
            if let hapticsEngine = hapticEngine {
                do {
                    rumbleController = nil
                    try hapticsEngine.start()
                    rumbleController = RumbleController(engine: hapticsEngine, rumbleMultiplier: device ? 2.0 : 2.5)
                } catch {
                    rumbleController = nil
                }
            } else {
                rumbleController = nil
            }
        }
        
        if let nativeController, !usesDeviceHandlers {
            hapticEngine = nativeController.haptics?.createEngine(withLocality: .all)
            createHaptic()
        } else {
            hapticEngine = try? CHHapticEngine()
            createHaptic(true)
        }
    }
    
    func getGamePadId() -> String? {
        if !id.isEmpty {
            return id
        }
        
        return generateGamepadId()
    }
    
    func generateGamepadId() -> String? {
        guard instanceID != -1 else { return nil }
        var joystickIndex: Int32 = -1
        
        let numJoysticks = SDL_NumJoysticks()
        for i in 0..<numJoysticks {
            if SDL_JoystickGetDeviceInstanceID(i) == instanceID {
                joystickIndex = i
                break
            }
        }
        
        if joystickIndex == -1 {
            return nil
        }
        
        let guid = SDL_JoystickGetGUID(joystick)
        
        let guidData = withUnsafeBytes(of: guid.data) { bytes in
            return Array(bytes)
        }
        
        if guidData.allSatisfy({ $0 == 0 }) {
            return nil
        }
        
        let guidString = String(format: "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
                               guidData[0], guidData[1], guidData[2], guidData[3],
                               guidData[4], guidData[5],
                               guidData[6], guidData[7],
                               guidData[8], guidData[9],
                               guidData[10], guidData[11], guidData[12], guidData[13], guidData[14], guidData[15])
        
        return "\(joystickIndex)-\(guidString)"
    }
    
    func updateAxisValue(value: Sint16, forAxis axis: SDL_GameControllerAxis) {
        guard controller != nil, joystick != nil else { return }
        
        let axisIndex = getJoystickAxisIndex(forControllerAxis: axis)
        guard axisIndex >= 0 else { return }
        
        let result = SDL_JoystickSetVirtualAxis(joystick, axisIndex, value)
        if result != 0 {
            print("Failed to set axis \(axisIndex) to \(value): \(String(cString: SDL_GetError()))")
        }
    }
    
    func thumbstickMoved(_ stick: ThumbstickType, x: Double, y: Double) {
        let scaledX = Sint16(max(-32768.0, min(32767.0, x * 32767.0)))
        let scaledY = Sint16(max(-32768.0, min(32767.0, y * 32767.0)))

        if stick == .right {
            updateAxisValue(value: scaledX, forAxis: SDL_CONTROLLER_AXIS_RIGHTX)
            updateAxisValue(value: scaledY, forAxis: SDL_CONTROLLER_AXIS_RIGHTY)
        } else {  // ThumbstickType.left
            updateAxisValue(value: scaledX, forAxis: SDL_CONTROLLER_AXIS_LEFTX)
            updateAxisValue(value: scaledY, forAxis: SDL_CONTROLLER_AXIS_LEFTY)
        }
    }
    
    private func getJoystickAxisIndex(forControllerAxis axis: SDL_GameControllerAxis) -> Int32 {
        switch axis {
        case SDL_CONTROLLER_AXIS_LEFTX:
            return 0
        case SDL_CONTROLLER_AXIS_LEFTY:
            return 1
        case SDL_CONTROLLER_AXIS_RIGHTX:
            return 2
        case SDL_CONTROLLER_AXIS_RIGHTY:
            return 3
        case SDL_CONTROLLER_AXIS_TRIGGERLEFT:
            return 4
        case SDL_CONTROLLER_AXIS_TRIGGERRIGHT:
            return 5
        default:
            return -1
        }
    }

    func setButtonState(_ state: Uint8, for button: VirtualControllerButton) {
        guard controller != nil,  joystick != nil else { return }
        
        if (button == .leftTrigger || button == .rightTrigger) && (state == 1 || state == 0) {
            let axis: SDL_GameControllerAxis = (button == .leftTrigger) ? SDL_CONTROLLER_AXIS_TRIGGERLEFT : SDL_CONTROLLER_AXIS_TRIGGERRIGHT
            let value: Int = (state == 1) ? 32767 : 0
            updateAxisValue(value: Sint16(value), forAxis: axis)
        } else {
            SDL_JoystickSetVirtualButton(joystick, Int32(button.rawValue), state)
        }
    }
    
    func tryRegisterMotion(slot: UInt8) {
        if playerSlot == slot && motionProvider != nil {
            reRegisterMotion()
            return
        }
        
        playerSlot = slot
        
        // Setup Motion
        let dsuServer = DSUServer.shared
        let vendorName = nativeController?.vendorName ?? "Unknown"
        let usesdevicemotion = virtual ? true : (vendorName.lowercased() == "Joy-Con (l/R)".lowercased() ||
                                vendorName.lowercased().hasSuffix("backbone") ||
                                vendorName.lowercased() == "backbone one")
        
        if usesdevicemotion {
            motionProvider = DeviceMotionProvider(slot: slot)
            dsuServer.register(motionProvider!)
        } else if let nativeController {
            motionProvider = ControllerMotionProvider(controller: nativeController, slot: slot)
            dsuServer.register(motionProvider!)
        }
    }
    
    func reRegisterMotion() {
        let playerSlot = playerSlot ?? 0
        let dsuServer = DSUServer.shared
        let vendorName = nativeController?.vendorName ?? "Unknown"
        let usesdevicemotion = virtual ? true : (vendorName.lowercased() == "Joy-Con (l/R)".lowercased() ||
                                vendorName.lowercased().hasSuffix("backbone") ||
                                vendorName.lowercased() == "backbone one")
        
        if usesdevicemotion {
            motionProvider = DeviceMotionProvider(slot: playerSlot)
            dsuServer.register(motionProvider!)
        } else if let nativeController {
            (motionProvider as? ControllerMotionProvider)?.setNewController(nativeController)
        }
    }
    
    func tryGetMotionProvider() -> DSUMotionProvider? {
        return motionProvider
    }
    
    deinit {
        // cleanup()
    }
    
    func cleanup() {
        if let controller {
            SDL_JoystickDetachVirtual(instanceID)
            SDL_GameControllerClose(controller)
            self.controller = nil
        }
    }
    
    func hash(into hasher: inout Hasher) {
        hasher.combine(nativeController)
    }
    
    static func == (lhs: BaseController, rhs: BaseController) -> Bool {
        lhs.controller == rhs.controller && lhs.instanceID == rhs.instanceID &&
        lhs.joystick == rhs.joystick && lhs.hapticEngine == rhs.hapticEngine &&
        lhs.name == rhs.name && lhs.virtual == rhs.virtual && lhs.id == rhs.id
    }
}
