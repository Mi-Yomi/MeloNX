//
//  NativeController.swift
//  MeloNX
//
//  Created by Stossy11 on 19/10/2025.
//

import Foundation
import CoreHaptics
import UIKit
import GameController

class NativeController: BaseController {
    override init(nativeController: GCController?) {
        super.init(nativeController: nativeController)
        setupNativeController()
    }
    
    func setupNewNativeController(_ newNativeController: GCController?) {
        self.nativeController = newNativeController
        setupNativeController()
    }
    
    func setupNativeController() {
        guard let gamepad = nativeController?.extendedGamepad
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

        setupHaptics()
        
        reRegisterMotion()
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
                updateAxisValue(value: scaledX, forAxis: SDL_CONTROLLER_AXIS_LEFTX)
                updateAxisValue(value: scaledY, forAxis: SDL_CONTROLLER_AXIS_LEFTY)
            case .right:
                updateAxisValue(value: scaledX, forAxis: SDL_CONTROLLER_AXIS_RIGHTX)
                updateAxisValue(value: scaledY, forAxis: SDL_CONTROLLER_AXIS_RIGHTY)
            }
        }
    }

    func setupTriggerChangeListener(_ button: GCControllerButtonInput, for key: ThumbstickType) {
        button.valueChangedHandler = { [unowned self] _, value, pressed in
            let axis: SDL_GameControllerAxis = (key == .left) ? SDL_CONTROLLER_AXIS_TRIGGERLEFT : SDL_CONTROLLER_AXIS_TRIGGERRIGHT
            let scaledValue = Sint16(value * 32767.0)
            updateAxisValue(value: scaledValue, forAxis: axis)
        }
    }
}
