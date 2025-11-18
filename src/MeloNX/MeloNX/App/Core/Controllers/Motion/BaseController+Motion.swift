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
        let motionRate: Double = 120.0
        
        if let nativeController, nativeController.motion != nil {
            nativeController.motion?.sensorsActive = true
            
            nativeController.motion?.valueChangedHandler = { [weak self] m in
                guard let self = self else { return }
                let g0: Float = 9.80665
                
                let ax = Float(m.gravity.x + m.userAcceleration.x) * g0
                let ay = Float(m.gravity.y + m.userAcceleration.y) * g0
                let az = Float(m.gravity.z + m.userAcceleration.z) * g0
                
                let gx = Float(m.rotationRate.x)
                let gy = Float(m.rotationRate.y)
                let gz = Float(m.rotationRate.z)

                // Transform axes to match expected coordinate system (like DSUController)
                let g = SIMD3<Float>(gx, -gz, gy)
                let a = SIMD3(ax, ay, az)
                
                let gDeg = g * (180.0 / .pi)
                
                RyujinxBridge.setGamepadMotion(self.nativePointer, motionType: 1, axis: a)
                RyujinxBridge.setGamepadMotion(self.nativePointer, motionType: 2, axis: gDeg)
            }
        } else if self.virtual {
            mm.deviceMotionUpdateInterval = 1.0 / motionRate
            UIDevice.current.beginGeneratingDeviceOrientationNotifications()
            NotificationCenter.default.addObserver(
                self,
                selector: #selector(orientationDidChange),
                name: UIDevice.orientationDidChangeNotification,
                object: nil
            )
            
            mm.startDeviceMotionUpdates(to: motionOperation) { [weak self] m, _ in
                guard let self = self, let m = m, Ryujinx.shared.isRunning, ControllerManager.shared.hasVirtualController() else { return }
                
                let g0: Float = 9.80665
                
                let ax = Float(m.gravity.x + m.userAcceleration.x) * g0
                let ay = Float(m.gravity.y + m.userAcceleration.y) * g0
                let az = Float(m.gravity.z + m.userAcceleration.z) * g0
                
                let gx = Float(m.rotationRate.x)
                let gy = Float(m.rotationRate.y)
                let gz = Float(m.rotationRate.z)
                
                let (a, g) = self.transformMotionData(
                    accel: SIMD3(ax, ay, az),
                    gyro: SIMD3(gx, gy, gz),
                    orientation: self.orientation
                )
                
                RyujinxBridge.setGamepadMotion(self.nativePointer, motionType: 1, axis: a)
                RyujinxBridge.setGamepadMotion(self.nativePointer, motionType: 2, axis: g)
            }
        }
    }
    
    private func transformMotionData(
        accel: SIMD3<Float>,
        gyro: SIMD3<Float>,
        orientation: UIDeviceOrientation
    ) -> (accel: SIMD3<Float>, gyro: SIMD3<Float>) {

        let gDeg = gyro * (180 / .pi)

        switch orientation {
        case .portrait:
            return (SIMD3(accel.x, accel.y, -accel.z),
                    SIMD3(gDeg.x, gDeg.y,  gDeg.z))

        case .portraitUpsideDown:
            return (SIMD3(-accel.x, -accel.y, -accel.z),
                    SIMD3(-gDeg.x, -gDeg.y,  gDeg.z))

        case .landscapeLeft:
            return (SIMD3( accel.y, -accel.x, -accel.z),
                    SIMD3(-gDeg.y,  gDeg.x,  gDeg.z))

        case .landscapeRight:
            return (SIMD3(-accel.y,  accel.x, -accel.z),
                    SIMD3( gDeg.y, -gDeg.x,  gDeg.z))

        default:
            return (SIMD3(accel.x, accel.y, -accel.z),
                    SIMD3(gDeg.x, gDeg.y, gDeg.z))
        }
    }

    
    @objc private func orientationDidChange() {
        let o = UIDevice.current.orientation
        if o.isFlat { return }          // ignore face-up / face-down
        orientation = o
    }
}
