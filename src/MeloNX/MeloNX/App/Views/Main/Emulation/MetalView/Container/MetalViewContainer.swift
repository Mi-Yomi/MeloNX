//
//  MetalViewContainer.swift
//  MeloNX
//
//  Created by Stossy11 on 06/07/2025.
//

import SwiftUI

struct MetalViewContainer: View {
    @ObservedObject var ryujinx = Ryujinx.shared
    @AppStorage("OldView") var oldView = true
    @Environment(\.colorScheme) var colorScheme
    
    var body: some View {
        GeometryReader { geo in
            let containerSize = geo.size
            if ryujinx.aspectRatio == .stretched || (ryujinx.aspectRatio == .fixed4x3 && isScreenAspectRatio(4, 3)) {
                if ryujinx.aspectRatio == .stretched || (ryujinx.aspectRatio == .fixed4x3 && isScreenAspectRatio(4, 3)) {
                    Color.clear
                        .overlay(
                            MetalView()
                                .frame(maxWidth: .infinity, maxHeight: .infinity)
                                .ignoresSafeArea(.all)
                        )
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .ignoresSafeArea(.all)
                }
            } else {
                if oldView {
                    oldViewLayout(containerSize: containerSize)
                } else {
                    newViewLayout(containerSize: containerSize)
                }
            }
        }
    }
    
    @ViewBuilder
    private func oldViewLayout(containerSize: CGSize) -> some View {
        let windowSize = UIApplication.shared.windows.first?.bounds.size ?? UIScreen.main.bounds.size
        let targetSize = targetSize(for: windowSize)
        let isLandscape = windowSize.width > windowSize.height
        
        VStack(spacing: 0) {
            if isLandscape {
                Spacer(minLength: 0)
            }
            
            HStack(spacing: 0) {
                Spacer(minLength: 0)
                
                MetalView()
                    .frame(width: targetSize.width, height: targetSize.height)
                    .ignoresSafeArea(.container, edges: isLandscape ? .all : .horizontal)
                
                Spacer(minLength: 0)
            }
            
            Spacer(minLength: 0)
        }
    }
    
    @ViewBuilder
    private func newViewLayout(containerSize: CGSize) -> some View {
        let isLandscape = containerSize.width > containerSize.height
        let targetSize = targetSize(for: containerSize)
        let corner: CGFloat = 13.0
        let borderWidth: CGFloat = 2.0
        
        let isPhone = UIDevice.current.userInterfaceIdiom == .phone
        let scale: CGFloat = isPhone ? (isLandscape ? 1.0 : 0.97) : (isLandscape ? 0.97 : 1.0)
        
        VStack(spacing: 0) {
            Spacer(minLength: 0)
            
            HStack(spacing: 0) {
                if isPhone && isLandscape {
                    Spacer()
                }
                
                ZStack {
                    RoundedRectangle(cornerRadius: corner, style: .continuous)
                        .strokeBorder(
                            colorScheme == .light ? Color.gray : Color(UIColor.darkGray),
                            lineWidth: borderWidth
                        )
                        .background(
                            RoundedRectangle(cornerRadius: corner, style: .continuous)
                                .fill(Color.clear)
                        )
                    
                    // Metal view with proper insets
                    MetalView()
                        .frame(
                            width: targetSize.width - borderWidth * 2,
                            height: targetSize.height - borderWidth * 2
                        )
                        .background(Color.black.opacity(0.8))
                        .clipShape(RoundedRectangle(cornerRadius: corner - 1, style: .continuous))
                        .shadow(color: .black.opacity(0.3), radius: 15)
                }
                .frame(width: targetSize.width, height: targetSize.height)
                .scaleEffect(scale)
                .ignoresSafeArea(.container, edges: isLandscape ? .all : .horizontal)
                
                if isPhone && isLandscape {
                    Spacer()
                }
            }
            
            Spacer(minLength: 0)
        }
    }
    
    func targetSize(for containerSize: CGSize) -> CGSize {
        var targetAspect: CGFloat
        
        switch ryujinx.aspectRatio {
        case .fixed4x3:
            targetAspect = 4.0 / 3.0
        case .fixed16x9:
            targetAspect = 16.0 / 9.0
        case .fixed16x10:
            targetAspect = 16.0 / 10.0
        case .fixed21x9:
            targetAspect = 21.0 / 9.0
        case .fixed32x9:
            targetAspect = 32.0 / 9.0
        case .stretched:
            return containerSize
        }
        
        let containerAspect = containerSize.width / containerSize.height
        
        if containerAspect > targetAspect {
            // Container is wider than target - fit to height
            let targetHeight = containerSize.height
            let targetWidth = targetHeight * targetAspect
            return CGSize(width: targetWidth, height: targetHeight)
        } else {
            // Container is taller than target - fit to width
            let targetWidth = containerSize.width
            let targetHeight = targetWidth / targetAspect
            return CGSize(width: targetWidth, height: targetHeight)
        }
    }
    
    func isScreenAspectRatio(_ targetWidth: CGFloat, _ targetHeight: CGFloat, tolerance: CGFloat = 0.05) -> Bool {
        let screenSize = UIScreen.main.bounds.size
        let width = max(screenSize.width, screenSize.height)
        let height = min(screenSize.width, screenSize.height)

        let actualRatio = width / height
        let targetRatio = targetWidth / targetHeight

        return abs(actualRatio - targetRatio) < tolerance
    }
}
