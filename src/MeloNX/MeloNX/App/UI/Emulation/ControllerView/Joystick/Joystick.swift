//
//  Joystick.swift
//  MeloNX
//
//  Created by Stossy11 on 21/03/2025.
//


import SwiftUI

struct Joystick: View {
    @AppStorage("On-ScreenControllerScale") var controllerScale: Double = 1.0
    @State var right = true
    @Binding var position: CGPoint
    let joystickSize: CGFloat
    var boundarySize: CGFloat
    
    @State private var offset: CGSize = .zero
    @Binding var showBackground: Bool
    @State var joystickSmallSize = false
    
    let sensitivity: CGFloat = 1.2
    
    private var displayJoystickSize: CGFloat {
        joystickSmallSize ? joystickSize * 1.4 : joystickSize
    }
    
    var dragGesture: some Gesture {
        DragGesture()
            .onChanged { value in
                withAnimation(.easeIn) {
                    showBackground = true
                    joystickSmallSize = true
                }
                
                let translation = value.translation
                let distance = sqrt(translation.width * translation.width + translation.height * translation.height)
                let maxRadius = (boundarySize - joystickSize) / 2
                let extendedRadius = maxRadius + (joystickSize / 2)
                
                if distance <= extendedRadius {
                    offset = translation
                } else {
                    let angle = atan2(translation.height, translation.width)
                    offset = CGSize(width: cos(angle) * extendedRadius, height: sin(angle) * extendedRadius)
                }
                
                position = CGPoint(
                    x: max(-1, min(1, (offset.width / extendedRadius) * sensitivity)),
                    y: max(-1, min(1, (offset.height / extendedRadius) * sensitivity))
                )
                
                setPos()
            }
            .onEnded { _ in
                offset = .zero
                position = .zero
                setPos()
                withAnimation(.easeOut) {
                    showBackground = false
                    joystickSmallSize = false
                }
            }
    }
    
    let virtualController = ControllerManager.shared.virtualController
    
    func setPos() {
        if right {
            virtualController.thumbstickMoved(.right, x: position.x, y: position.y)
        } else {
            virtualController.thumbstickMoved(.left, x: position.x, y: position.y)
        }
    }
    
    var body: some View {
        ZStack {
            Circle()
                .fill(Color.clear.opacity(0))
                .frame(width: boundarySize, height: boundarySize)
                .scaleEffect(controllerScale)
            
            if showBackground {
                Circle()
                    .fill(Color.gray.opacity(0.4))
                    .frame(width: boundarySize, height: boundarySize)
                    .animation(.easeInOut(duration: 0.1), value: showBackground)
                    .scaleEffect(controllerScale)
            }
            
            Circle()
                .fill(Color.white.opacity(0.5))
                .frame(width: displayJoystickSize, height: displayJoystickSize)
                .background(
                    Circle()
                        .fill(Color.gray.opacity(0.3))
                        .frame(width: displayJoystickSize * 1.25, height: displayJoystickSize * 1.25)
                )
                .offset(offset)
                .gesture(dragGesture)
                .scaleEffect(controllerScale)
        }
        .frame(width: boundarySize, height: boundarySize)
    }
}
