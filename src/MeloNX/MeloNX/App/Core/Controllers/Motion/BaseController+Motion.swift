//
//  BaseController+Motion.swift
//  MeloNX
//
//  Created by Stossy11 on 16/11/2025.
//

import Foundation
import CoreMotion

extension BaseController {
    func setupMotion() {
        if let nativeController, nativeController.motion != nil, !self.usesDeviceHandlers {
            nativeController.motion?.sensorsActive = true
            
            nativeController.motion?.valueChangedHandler = { [weak self] m in
                guard let self = self else { return }
                self.motionQueue.async {
                    let g0: Float = 9.80665
                    
                    let ax = Float(m.gravity.x + m.userAcceleration.x) * g0
                    let ay = Float(m.gravity.y + m.userAcceleration.y) * g0
                    let az = Float(m.gravity.z + m.userAcceleration.z) * g0
                    
                    let gx = Float(m.rotationRate.x)
                    let gy = Float(m.rotationRate.y)
                    let gz = Float(m.rotationRate.z)
                    
                    let g = SIMD3<Float>(gx, gy, -gz)
                    let a = SIMD3(ax, ay, az)
                    let gDeg = g * (180.0 / .pi)
                    
                    RyujinxBridge.setGamepadMotion(self.pointer, motionType: 1, axis: a)
                    RyujinxBridge.setGamepadMotion(self.pointer, motionType: 2, axis: gDeg)
                }
            }
        } else {
            // Virtual controller motion handling
            let motionManager = DeviceMotionManager()
            
            motionManager.motionUpdateHandler = { [weak self] accel, gyro in
                self?.motionQueue.async {
                    guard let self = self,
                          Ryujinx.shared.isRunning,
                          ControllerManager.shared.hasVirtualController() else { return }
                    RyujinxBridge.setGamepadMotion(self.pointer, motionType: 1, axis: accel)
                    RyujinxBridge.setGamepadMotion(self.pointer, motionType: 2, axis: gyro)
                }
            }
            
            motionManager.start()
        }
    }
}
