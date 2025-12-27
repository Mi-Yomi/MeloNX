//
//  MeloMTKView.swift
//  MeloNX
//
//  Created by Stossy11 on 03/03/2025.
//

import MetalKit
import UIKit

class MeloMTKView: MTKView {
    private var activeTouches: [UITouch] = []
    private var ignoredTouches: Set<UITouch> = []
    private var touchIndexMap: [UITouch: Int] = [:]

    private var aspectRatio: AspectRatio = .fixed16x9

    func setAspectRatio(_ ratio: AspectRatio) {
        self.aspectRatio = ratio
        RyujinxBridge.setViewSize(width: Int(self.bounds.width), height: Int(self.bounds.height))
    }
    
    private func transformToTargetCoordinates(_ point: CGPoint) -> CGPoint {
        return point
    }

    private func getNextAvailableIndex() -> Int {
        for i in 0..<Int.max {
            if !touchIndexMap.values.contains(i) {
                return i
            }
        }
        return 0
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesBegan(touches, with: event)

        let disabled = UserDefaults.standard.bool(forKey: "disableTouch")
        guard !disabled else { return }
        
        setAspectRatio(Ryujinx.shared.aspectRatio)

        for touch in touches {
            let location = touch.location(in: self)
            let index = getNextAvailableIndex()
            touchIndexMap[touch] = index
            activeTouches.append(touch)
            print("Touch location: \(location)")
            let transformed = transformToTargetCoordinates(location)
            RyujinxBridge.touchBegan(x: Float(transformed.x), y: Float(transformed.y), index: index)
        }
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesEnded(touches, with: event)

        let disabled = UserDefaults.standard.bool(forKey: "disableTouch")
        guard !disabled else {
            for touch in touches {
                ignoredTouches.remove(touch)
                if let index = activeTouches.firstIndex(of: touch) {
                    activeTouches.remove(at: index)
                }
                touchIndexMap.removeValue(forKey: touch)
            }
            return
        }

        for touch in touches {
            if ignoredTouches.remove(touch) != nil {
                continue
            }

            if let touchIndex = touchIndexMap[touch] {
                RyujinxBridge.touchEnded(index: touchIndex)
                
                if let arrayIndex = activeTouches.firstIndex(of: touch) {
                    activeTouches.remove(at: arrayIndex)
                }
                touchIndexMap.removeValue(forKey: touch)
            }
        }
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesMoved(touches, with: event)

        let disabled = UserDefaults.standard.bool(forKey: "disableTouch")
        guard !disabled else { return }
        
        setAspectRatio(Ryujinx.shared.aspectRatio)

        for touch in touches {
            if ignoredTouches.contains(touch) {
                continue
            }

            guard let touchIndex = touchIndexMap[touch] else {
                continue
            }

            let location = touch.location(in: self)
            let transformed = transformToTargetCoordinates(location)
            RyujinxBridge.touchMoved(x: Float(transformed.x), y: Float(transformed.y), index: touchIndex)
        }
    }

    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesCancelled(touches, with: event)
        touchesEnded(touches, with: event)
    }
    

    func resetTouchTracking() {
        activeTouches.removeAll()
        ignoredTouches.removeAll()
        touchIndexMap.removeAll()
    }
}

