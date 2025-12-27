//
//  MetalViewContainer.swift
//  MeloNX
//
//  Created by Stossy11 on 06/07/2025.
//

import SwiftUI

struct MetalViewContainer: View {
    @EnvironmentObject var ryujinx: Ryujinx
    @EnvironmentObject var gameHandler: LaunchGameHandler
    
    @State private var targetSize1: CGSize = .zero
    @State private var isPortrait: Bool = false
    
    var body: some View {
        GeometryReader { geo in
            ZStack {
                Color.clear
                
                VStack {
                    MetalView()
                        .frame(width: targetSize1.width,
                               height: targetSize1.height)
                        .allowsHitTesting(true)
                        .ignoresSafeArea(.container,
                                         edges: !isPortrait ? .all : .horizontal)
                    
                    if isPortrait {
                        Spacer()
                    }
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: isPortrait ? .top : .center)
            .onAppear {
                getSize()
            }
            .onChange(of: geo.size) { newSize in
                getSize()
            }
            .onChange(of: ryujinx.aspectRatio) { _ in
                getSize()
            }
        }
    }
    
    func getSize() {
        targetSize1 = targetSize(ryujinx: ryujinx)
                                 
        guard let window = UIApplication.shared.connectedScenes
            .compactMap({ $0 as? UIWindowScene })
            .flatMap({ $0.windows })
            .first(where: { $0.isKeyWindow }) else { return }
        
        isPortrait = window.frame.width < window.frame.height
    }
}



func targetSize(for containerSize: CGSize? = nil, ryujinx: Ryujinx) -> CGSize {
    var targetAspect: CGFloat
    
    guard let window = UIApplication.shared.connectedScenes
        .compactMap({ $0 as? UIWindowScene })
        .flatMap({ $0.windows })
        .first(where: { $0.isKeyWindow }) else { return containerSize ?? CGSize.zero }
    
    let containerSize = containerSize ?? window.frame.size
    let ratio = ryujinx.aspectRatio
    
    switch ratio {
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
