//
//  NativeController.swift
//  MeloNX
//
//  Created by XITRIX on 15/02/2025.
//

import CoreHaptics
import GameController

class NativeController: Hashable, BaseController {
    private var instanceID: SDL_JoystickID = -1
    private var controller: OpaquePointer?
    private var nativeController: GCController
    private var controllerMotionProvider: ControllerMotionProvider?
    private var deviceMotionProvider: DeviceMotionProvider?
    
    private var controllerHaptics: CHHapticEngine?
    private var rumbleController: RumbleController?
    
    var uniqueID: String? { nativeController.vendorName }

    public var controllername: String { "GC - \(nativeController.vendorName ?? "Unknown")" }

    init(_ controller: GCController) {
        nativeController = controller
        var ncontrollerHaptics = nativeController.haptics?.createEngine(withLocality: .all)
        
        let vendorName = nativeController.vendorName ?? "Unknown"
        var usesdeviceHaptics = (vendorName.lowercased().contains("backbone") || vendorName.lowercased() == "Joy-Con (l/R)".lowercased())
        controllerHaptics = usesdeviceHaptics ?  try? CHHapticEngine() : ncontrollerHaptics
        
        // Make sure the haptic engine exists before attempting to start it or initialize the controller.
        if let hapticsEngine = controllerHaptics {
            do {
                try hapticsEngine.start()
                rumbleController = RumbleController(engine: hapticsEngine, rumbleMultiplier: usesdeviceHaptics ? 2.0 : 2.5)
            } catch {
                rumbleController = nil
            }
        } else {
            rumbleController = nil
        }
        setupHandheldController()
    }

    deinit {
        cleanup()
    }
    
    internal func tryRegisterMotion(slot: UInt8) {
        // Setup Motion
        let dsuServer = DSUServer.shared
        let vendorName = nativeController.vendorName ?? "Unknown"
        var usesdevicemotion = (vendorName.lowercased() == "Joy-Con (l/R)".lowercased() || vendorName.lowercased().hasSuffix("backbone") || vendorName.lowercased() == "backbone one")
        
        usesdevicemotion ? (deviceMotionProvider = DeviceMotionProvider(slot: slot)) : (controllerMotionProvider = ControllerMotionProvider(controller: nativeController, slot: slot))
        
        if let provider = controllerMotionProvider {
            dsuServer.register(provider)
        } else if let provider = deviceMotionProvider {
            dsuServer.register(provider)
        }
    }
    
    internal func tryGetMotionProvider() -> DSUMotionProvider? {
        if let deviceMotionProvider {
            return deviceMotionProvider
        }
        
        return controllerMotionProvider
    }

    private func setupHandheldController() {
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
            name: (controllername as NSString).utf8String,
            userdata: Unmanaged.passUnretained(self).toOpaque(),
            Update: { userdata in
                // Update joystick state here
            },
            SetPlayerIndex: { userdata, playerIndex in
                // print("Player index set to \(playerIndex)")
                guard let userdata, let player = GCControllerPlayerIndex(rawValue: Int(playerIndex)) else { return }
                let _self = Unmanaged<NativeController>.fromOpaque(userdata).takeUnretainedValue()
                _self.nativeController.playerIndex = player
            },
            Rumble: { userdata, lowFreq, highFreq in
                guard let userdata else { return 0 }
                let _self = Unmanaged<NativeController>.fromOpaque(userdata).takeUnretainedValue()
                _self.rumbleController?.rumble(lowFreq: Float(lowFreq), highFreq: Float(highFreq))
                return 0
            },
            RumbleTriggers: { userdata, leftRumble, rightRumble in
                return 0
            },
            SetLED: { userdata, red, green, blue in
                guard let userdata else { return 0 }
                let _self = Unmanaged<NativeController>.fromOpaque(userdata).takeUnretainedValue()
                guard let light = _self.nativeController.light else { return 0 }
                light.color = .init(red: Float(red), green: Float(green), blue: Float(blue))
                return 0
            },
            SendEffect: { userdata, data, size in
                return 0
            }
        )
        
        instanceID = SDL_JoystickAttachVirtualEx(&joystickDesc)
        if instanceID < 0 {
            return
        }
        
        controller = SDL_GameControllerOpen(Int32(instanceID))
        
        if controller == nil {
            return
        }
        
        guard let gamepad = nativeController.extendedGamepad
        else { return }
        
        setupButtonChangeListener(gamepad.buttonA, for: UserDefaults.standard.bool(forKey: "swapBandA") ? .B : .A)
        setupButtonChangeListener(gamepad.buttonB, for: UserDefaults.standard.bool(forKey: "swapBandA") ? .A : .B)
        setupButtonChangeListener(gamepad.buttonX, for: UserDefaults.standard.bool(forKey: "swapBandA") ? .Y : .X)
        setupButtonChangeListener(gamepad.buttonY, for: UserDefaults.standard.bool(forKey: "swapBandA") ? .X : .Y)
        
        setupButtonChangeListener(gamepad.dpad.up, for: .dPadUp)
        setupButtonChangeListener(gamepad.dpad.down, for: .dPadDown)
        setupButtonChangeListener(gamepad.dpad.left, for: .dPadLeft)
        setupButtonChangeListener(gamepad.dpad.right, for: .dPadRight)
        
        setupButtonChangeListener(gamepad.leftShoulder, for: .leftShoulder)
        setupButtonChangeListener(gamepad.rightShoulder, for: .rightShoulder)
        gamepad.leftThumbstickButton.map { setupButtonChangeListener($0, for: .leftStick) }
        gamepad.rightThumbstickButton.map { setupButtonChangeListener($0, for: .rightStick) }
        
        setupButtonChangeListener(gamepad.buttonMenu, for: .start)
        gamepad.buttonOptions.map { setupButtonChangeListener($0, for: .back) }
        
        setupStickChangeListener(gamepad.leftThumbstick, for: .left)
        setupStickChangeListener(gamepad.rightThumbstick, for: .right)
        
        setupTriggerChangeListener(gamepad.leftTrigger, for: .left)
        setupTriggerChangeListener(gamepad.rightTrigger, for: .right)
    }
    
    func changeGamepad(_ controller: GCController) {
        self.nativeController = controller
        let ncontrollerHaptics = nativeController.haptics?.createEngine(withLocality: .all)
        
        let vendorName = nativeController.vendorName ?? "Unknown"
        let usesdeviceHaptics = (vendorName.lowercased().contains("backbone") || vendorName.lowercased() == "Joy-Con (l/R)".lowercased())
        controllerHaptics = usesdeviceHaptics ?  try? CHHapticEngine() : ncontrollerHaptics
        
        if let hapticsEngine = controllerHaptics {
            do {
                rumbleController = nil
                try hapticsEngine.start()
                rumbleController = RumbleController(engine: hapticsEngine, rumbleMultiplier: usesdeviceHaptics ? 2.0 : 2.5)
            } catch {
                rumbleController = nil
            }
        } else {
            rumbleController = nil
        }
        
        guard let gamepad = nativeController.extendedGamepad
        else { return }
        
        setupButtonChangeListener(gamepad.buttonA, for: UserDefaults.standard.bool(forKey: "swapBandA") ? .B : .A)
        setupButtonChangeListener(gamepad.buttonB, for: UserDefaults.standard.bool(forKey: "swapBandA") ? .A : .B)
        setupButtonChangeListener(gamepad.buttonX, for: UserDefaults.standard.bool(forKey: "swapBandA") ? .Y : .X)
        setupButtonChangeListener(gamepad.buttonY, for: UserDefaults.standard.bool(forKey: "swapBandA") ? .X : .Y)
        
        setupButtonChangeListener(gamepad.dpad.up, for: .dPadUp)
        setupButtonChangeListener(gamepad.dpad.down, for: .dPadDown)
        setupButtonChangeListener(gamepad.dpad.left, for: .dPadLeft)
        setupButtonChangeListener(gamepad.dpad.right, for: .dPadRight)
        
        setupButtonChangeListener(gamepad.leftShoulder, for: .leftShoulder)
        setupButtonChangeListener(gamepad.rightShoulder, for: .rightShoulder)
        gamepad.leftThumbstickButton.map { setupButtonChangeListener($0, for: .leftStick) }
        gamepad.rightThumbstickButton.map { setupButtonChangeListener($0, for: .rightStick) }
        
        setupButtonChangeListener(gamepad.buttonMenu, for: .start)
        gamepad.buttonOptions.map { setupButtonChangeListener($0, for: .back) }
        
        setupStickChangeListener(gamepad.leftThumbstick, for: .left)
        setupStickChangeListener(gamepad.rightThumbstick, for: .right)
        
        setupTriggerChangeListener(gamepad.leftTrigger, for: .left)
        setupTriggerChangeListener(gamepad.rightTrigger, for: .right)
    }

    func setupButtonChangeListener(_ button: GCControllerButtonInput, for key: VirtualControllerButton) {
        button.valueChangedHandler = { [unowned self] _, _, pressed in
            setButtonState(pressed ? 1 : 0, for: key)
        }
    }

    func setupStickChangeListener(_ button: GCControllerDirectionPad, for key: ThumbstickType) {
        button.valueChangedHandler = { [unowned self] _, xValue, yValue in
            let scaledX = Sint16(xValue * 32767.0)
            let scaledY = -Sint16(yValue * 32767.0)

            switch key {
            case .left:
                updateAxisValue(value: scaledX, forAxis: SDL_GameControllerAxis(SDL_CONTROLLER_AXIS_LEFTX.rawValue))
                updateAxisValue(value: scaledY, forAxis: SDL_GameControllerAxis(SDL_CONTROLLER_AXIS_LEFTY.rawValue))
            case .right:
                updateAxisValue(value: scaledX, forAxis: SDL_GameControllerAxis(SDL_CONTROLLER_AXIS_RIGHTX.rawValue))
                updateAxisValue(value: scaledY, forAxis: SDL_GameControllerAxis(SDL_CONTROLLER_AXIS_RIGHTY.rawValue))
            }
        }
    }

    func setupTriggerChangeListener(_ button: GCControllerButtonInput, for key: ThumbstickType) {
        button.valueChangedHandler = { [unowned self] _, value, pressed in
//            // print("Value: \(value), Is pressed: \(pressed)")
            let axis: SDL_GameControllerAxis = (key == .left) ? SDL_CONTROLLER_AXIS_TRIGGERLEFT : SDL_CONTROLLER_AXIS_TRIGGERRIGHT
            let scaledValue = Sint16(value * 32767.0)
            updateAxisValue(value: scaledValue, forAxis: axis)
        }
    }

    func updateAxisValue(value: Sint16, forAxis axis: SDL_GameControllerAxis) {
        guard controller != nil else { return }
        let joystick = SDL_JoystickFromInstanceID(instanceID)
        SDL_JoystickSetVirtualAxis(joystick, axis.rawValue, value)
    }

    func thumbstickMoved(_ stick: ThumbstickType, x: Double, y: Double) {
        let scaleFactor = 32767.0 / 160

        let scaledX = Int16(min(32767.0, max(-32768.0, x * scaleFactor)))
        let scaledY = Int16(min(32767.0, max(-32768.0, y * scaleFactor)))

        if stick == .right {
            updateAxisValue(value: scaledX, forAxis: SDL_GameControllerAxis(SDL_CONTROLLER_AXIS_RIGHTX.rawValue))
            updateAxisValue(value: scaledY, forAxis: SDL_GameControllerAxis(SDL_CONTROLLER_AXIS_RIGHTY.rawValue))
        } else {  // ThumbstickType.left
            updateAxisValue(value: scaledX, forAxis: SDL_GameControllerAxis(SDL_CONTROLLER_AXIS_LEFTX.rawValue))
            updateAxisValue(value: scaledY, forAxis: SDL_GameControllerAxis(SDL_CONTROLLER_AXIS_LEFTY.rawValue))
        }
    }

    func setButtonState(_ state: Uint8, for button: VirtualControllerButton) {
        guard controller != nil else { return }

        if (button == .leftTrigger || button == .rightTrigger) && (state == 1 || state == 0) {
            let axis: SDL_GameControllerAxis = (button == .leftTrigger) ? SDL_CONTROLLER_AXIS_TRIGGERLEFT : SDL_CONTROLLER_AXIS_TRIGGERRIGHT
            let value: Int = (state == 1) ? 32767 : 0
            updateAxisValue(value: Sint16(value), forAxis: axis)
        } else {
            let joystick = SDL_JoystickFromInstanceID(instanceID)
            SDL_JoystickSetVirtualButton(joystick, Int32(button.rawValue), state)
        }
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

    static func == (lhs: NativeController, rhs: NativeController) -> Bool {
        lhs.nativeController == rhs.nativeController
    }
}
