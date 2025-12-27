//
//  DeviceMotionManager.swift
//  MeloNX
//
//  Created by Stossy11 on 22/11/2025.
//

import Foundation
import CoreMotion
import UIKit

class DeviceMotionManager {
    private let motionManager = CMMotionManager()
    private let motionQueue = OperationQueue()
    private let motionRate: Double = 120.0
    private let g0: Float = 9.80665
    
    private(set) var orientation: UIDeviceOrientation = .portrait
    
    var isActive: Bool = false
    
    var motionUpdateHandler: ((SIMD3<Float>, SIMD3<Float>) -> Void)?
    
    init() {
        motionManager.deviceMotionUpdateInterval = 1.0 / motionRate
        setupOrientationObserver()
    }
    
    deinit {
        stop()
        NotificationCenter.default.removeObserver(self)
    }
    
    func start() {
        guard !isActive else { return }
        
        DispatchQueue.main.async {
            UIDevice.current.beginGeneratingDeviceOrientationNotifications()
        }
        
        motionManager.startDeviceMotionUpdates(to: motionQueue) { [weak self] motion, error in
            guard let self = self, let motion = motion else { return }
            self.processMotionData(motion)
        }
        
        isActive = true
    }
    
    func stop() {
        guard isActive else { return }
        
        motionManager.stopDeviceMotionUpdates()
        DispatchQueue.main.async {
            UIDevice.current.endGeneratingDeviceOrientationNotifications()
        }
        isActive = false
    }
    
    private func setupOrientationObserver() {
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(orientationDidChange),
            name: UIDevice.orientationDidChangeNotification,
            object: nil
        )
    }
    
    private func processMotionData(_ motion: CMDeviceMotion) {
        let ax = Float(motion.gravity.x + motion.userAcceleration.x) * g0
        let ay = Float(motion.gravity.y + motion.userAcceleration.y) * g0
        let az = Float(motion.gravity.z + motion.userAcceleration.z) * g0
        
        let gx = Float(motion.rotationRate.x)
        let gy = Float(motion.rotationRate.y)
        let gz = Float(motion.rotationRate.z)
        
        let (accel, gyro) = transformMotionData(
            accel: SIMD3(ax, ay, az),
            gyro: SIMD3(gx, gy, gz),
            orientation: orientation
        )
        
        motionUpdateHandler?(accel, gyro)
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
                    SIMD3(gDeg.x, gDeg.y, gDeg.z))
            
        case .portraitUpsideDown:
            return (SIMD3(-accel.x, -accel.y, -accel.z),
                    SIMD3(-gDeg.x, -gDeg.y, gDeg.z))
            
        case .landscapeLeft:
            return (SIMD3(accel.y, -accel.x, -accel.z),
                    SIMD3(-gDeg.y, gDeg.x, gDeg.z))
            
        case .landscapeRight:
            return (SIMD3(-accel.y, accel.x, -accel.z),
                    SIMD3(gDeg.y, -gDeg.x, gDeg.z))
            
        default:
            return (SIMD3(accel.x, accel.y, -accel.z),
                    SIMD3(gDeg.x, gDeg.y, gDeg.z))
        }
    }
    
    @objc private func orientationDidChange() {
        let newOrientation = UIDevice.current.orientation
        if !newOrientation.isFlat {
            orientation = newOrientation
        }
    }
}
