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
import CoreMotion

class BaseController: Equatable, Identifiable {
    var id: String {
        let pointerInt64 = Int64(bitPattern: UInt64(UInt(bitPattern: self.pointer)))
        
        let hexString = String(pointerInt64, radix: 16, uppercase: true)
        
        return hexString
    }
    
    var pointer: UnsafeMutableRawPointer {
        UnsafeMutableRawPointer(Unmanaged.passUnretained(self).toOpaque())
    }
    
    var type: ControllerType
    var virtual: Bool = false
    
    var name: String { virtual ? "MeloNX Virtual Controller" : nativeController?.vendorName ?? "Unknown" }
    
    var nativeController: GCController?
    
    // Motion
    var orientation: UIDeviceOrientation =
        UIDevice.current.orientation == .unknown ? .landscapeLeft : UIDevice.current.orientation
    let motionOperation = OperationQueue()
    var lastAccel = SIMD3<Float>(repeating: 0)
    var lastGyro = SIMD3<Float>(repeating: 0)
    let filterAlpha: Float = 0.05
    
    private var hapticEngine: CHHapticEngine?
    private var rumbleController: RumbleController?

    // queue for controller input :3
    var inputQueue: DispatchQueue
    var motionQueue: DispatchQueue
    
    var usesDeviceHandlers: Bool { virtual ? true : (name.lowercased() == "Joy-Con (l/R)".lowercased() ||
                                                      name.lowercased().hasSuffix("backbone") ||
                                                      name.lowercased() == "backbone one") }

    
    init(nativeController: GCController?) {
        self.nativeController = nativeController
        self.virtual = nativeController == nil
        self.type = virtual ? .joyconPair : .proController
        
        let identifier = UUID().uuidString
        
        let queueLabel = virtual
            ? "com.stossy11.MeloNX.controller.virtual"
            : "com.stossy11.MeloNX.controller.\(identifier)"
        
        let motionLabel = queueLabel + ".motion"
        
        self.inputQueue = DispatchQueue(label: queueLabel, qos: .userInteractive)
        self.motionQueue = DispatchQueue(label: motionLabel, qos: .background)
        
        RyujinxBridge.attachGamepad(self.pointer, self.name)
        
        setupController()
    }
    

    
    public func setupController() {
        inputQueue.async { [weak self] in
            guard let self = self else { return }
            setupMotion()
        }
        
        RyujinxBridge.attachGamepad(self.pointer, self.name)
        
        setupHaptics()
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
            
            RegisterCallbackWithData("rumble-\(self.id)") { data in
                if let rumbleData = RumbleData(data: data ?? Data()) {
                    self.rumbleController?.rumble(lowFreq: rumbleData.lowFrequency, highFreq: rumbleData.highFrequency, durationMs: rumbleData.durationMs)
                }
            }
        }
        
        DispatchQueue.main.async { [weak self] in
            guard let self = self else { return }
            
            if let nativeController = self.nativeController, !self.usesDeviceHandlers {
                self.hapticEngine = nativeController.haptics?.createEngine(withLocality: .all)
                createHaptic()
            } else {
                self.hapticEngine = try? CHHapticEngine()
                createHaptic(true)
            }
        }
    }
    
    func updateAxisValue(x: Float, y: Float, forAxis axis: Int) {
        inputQueue.async { [weak self] in
            guard let self = self else { return }
            RyujinxBridge.setGamepadStickAxis(self.pointer, stickId: axis, x: x, y: y)
        }
    }
    
    func thumbstickMoved(_ stick: ThumbstickType, x: Double, y: Double) {
        if stick == .right {
            updateAxisValue(x: Float(x), y: Float(-y), forAxis: 2)
        } else {  // ThumbstickType.left
            updateAxisValue(x: Float(x), y: Float(-y), forAxis: 1)
        }
    }
    
    func setButtonState(_ state: Uint8, for button: VirtualControllerButton) {
        inputQueue.async { [weak self] in
            guard let self = self else { return }
            RyujinxBridge.setGamepadButtonState(self.pointer, buttonId: button.rawValue, pressed: state == 1)
        }
    }
    
    deinit {
        // cleanup()
    }
    
    func cleanup() {
        inputQueue.async { [weak self] in
            guard let self = self else { return }
            RyujinxBridge.detachGamepad(self.pointer)
        }
    }
    
    func hash(into hasher: inout Hasher) {
        hasher.combine(nativeController)
    }
    
    static func == (lhs: BaseController, rhs: BaseController) -> Bool {
        lhs.id == rhs.id &&
        lhs.hapticEngine == rhs.hapticEngine &&
        lhs.name == rhs.name && lhs.virtual == rhs.virtual && lhs.id == rhs.id
    }
}
