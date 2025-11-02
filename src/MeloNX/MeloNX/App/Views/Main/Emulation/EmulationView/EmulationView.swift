//
//  EmulationView.swift
//  MeloNX
//
//  Created by Stossy11 on 09/02/2025.
//

import SwiftUI

// Emulation View
struct EmulationView: View {
    @AppStorage("performacehud") var performacehud: Bool = false
    @AppStorage("isVirtualController") var isVCA: Bool = true
    @AppStorage("showScreenShotButton") var ssb: Bool = false
    @AppStorage("showlogsgame") var showlogsgame: Bool = false
    
    @AppStorage("On-ScreenControllerOpacity") var controllerOpacity: Double = 1.0
    
    @AppStorage("OldView") var oldView = true
    @State var isPresentedThree: Bool = false
    @State var isAirplaying = Air.shared.connected
    @Binding var startgame: Game?
    
    @Environment(\.scenePhase) var scenePhase
    @State private var isInBackground = false
    @State var showSettings = false
    @State var pauseEmu = true
    @AppStorage("location-enabled") var locationenabled: Bool = false
    @FocusState private var isFocused: Bool
    @ObservedObject var ryujinx = Ryujinx.shared
    @State var rotationlock = false
    
    var body: some View {
        ZStack {
            if oldView {
                Color.black
                    .ignoresSafeArea()
                    .edgesIgnoringSafeArea(.all)
                    .allowsHitTesting(false)
            }
            
            if isAirplaying {
                TouchView()
                    .ignoresSafeArea()
                    .edgesIgnoringSafeArea(.all)
                    .onAppear {
                        Air.play(AnyView(MetalView().ignoresSafeArea().edgesIgnoringSafeArea(.all)))
                    }
                    .overlay(alignment: .topTrailing) {
                        if performacehud, getenv("MTL_HUD_ENABLED").flatMap({ String(cString: $0) }) != "1" {
                            PerformanceOverlayView()
                                .opacity(controllerOpacity)
                                .padding(5)
                        }
                    }
            } else {
                // The Emulation View
                if ryujinx.aspectRatio == .stretched || (ryujinx.aspectRatio == .fixed4x3 && isScreenAspectRatio(4, 3)) {
                    MetalView()
                        .allowsHitTesting(true)
                        .ignoresSafeArea(.all)
                        .edgesIgnoringSafeArea(.all)
                        .overlay(alignment: .topTrailing) {
                            if performacehud, getenv("MTL_HUD_ENABLED").flatMap({ String(cString: $0) }) != "1" {
                                PerformanceOverlayView()
                                    .opacity(controllerOpacity)
                                    .padding(5)
                            }
                        }
                } else {
                    MetalViewContainer()
                        .allowsHitTesting(true)
                        .overlay(alignment: .topTrailing) {
                            if performacehud, getenv("MTL_HUD_ENABLED").flatMap({ String(cString: $0) }) != "1" {
                                PerformanceOverlayView()
                                    .opacity(controllerOpacity)
                                    .padding(5)
                            }
                        }
                }
            }
            
            // Above Emulation View
            if isVCA {
                ControllerView(isEditing: .constant(false), gameId: startgame?.titleId) // Virtual Controller
                    .contentShape(Rectangle())
                    .opacity(controllerOpacity)
                    .allowsHitTesting(true)
            }
            
            
            VStack {
                HStack {
                    if !performacehud, showlogsgame, ProcessInfo.processInfo.isLowPowerModeEnabled {
                        Circle()
                            .fill(Color.orange)
                            .frame(width: 10, height: 10)
                            .padding()
                    }
                    

                    if ssb {
                        Menu {
                            Button {
                                RyujinxBridge.pauseEmulation(pauseEmu)
                                pauseEmu.toggle()
                            } label: {
                                Label {
                                    Text(pauseEmu ? "Pause" : "Play")
                                } icon: {
                                    Image(systemName: pauseEmu ? "pause.circle" : "play.circle")
                                }
                            }
                            
                            Button {
                                // ryujijnx.config?.aspectRatio
                                ryujinx.aspectRatio = nextAspectRatio(current: ryujinx.aspectRatio)
                            } label: {
                                Label {
                                    Text(ryujinx.aspectRatio.displayName)
                                } icon: {
                                    Image(systemName: "rectangle.expand.vertical")
                                }
                            }
                            
                            //  OrientationManager.lockOrientation(.landscape, rotateTo: .landscapeRight)
                            
                            if UIDevice.current.userInterfaceIdiom == .phone {
                                Button {
                                    // UIDevice.current.orientation
                                    rotationlock.toggle()
                                    if rotationlock {
                                        OrientationManager.lockCurrentOrientation(UIDevice.current.orientation)
                                    } else {
                                        OrientationManager.lockOrientation(.all, rotateTo: UIDevice.current.orientation)
                                    }
                                } label: {
                                    Label {
                                        Text("Rotation Lock")
                                    } icon: {
                                        Image(systemName: rotationlock ? "lock" : "lock.open")
                                    }
                                }
                            }
                            
                            Button(role: .destructive) {
                                stop()
                            } label: {
                                Label {
                                    Text("Exit (Unstable)")
                                } icon: {
                                    Image(systemName: "x.circle")
                                }
                            }
                        } label: {
                            ExtButtonIconView(button: .guide, opacity: 0.4)
                        }
                        .menuStyle(.borderlessButton)
                        .menuIndicator(.hidden)
                        .padding()
                    }
                    
                    
                    Spacer()
                }
                
                Spacer()
            }
            
            if showlogsgame, !Ryujinx.shared.showLoading {
                VStack {
                    LogFileView(isfps: false)
                        .allowsHitTesting(false)
                    
                    Spacer()
                }
                .allowsHitTesting(false)
            }
        }
        .onAppear {
           Task { @MainActor in
                isFocused = true
            }
            
            LocationManager.sharedInstance.startUpdatingLocation()
            Air.shared.connectionCallbacks.append { cool in
               Task { @MainActor in
                    isAirplaying = cool
                    // print(cool)
                }
            }
            
            RegisterCallback("exit-emulation") { cool in
               Task { @MainActor in
                    print(cool)
                    self.stop()
                }
            }
        }
        .statusBar(hidden: true)
        .onKeyPress()
        .focused($isFocused)
        .onChange(of: scenePhase) { newPhase in
            // Detect when the app enters the background
            if newPhase == .background {
                RyujinxBridge.pauseEmulation(true)
                isInBackground = true
            } else if newPhase == .active {
                RyujinxBridge.pauseEmulation(false)
                isInBackground = false
            } else if newPhase == .inactive {
                RyujinxBridge.pauseEmulation(true)
                isInBackground = true
            }
        }
        .sheet(isPresented: $showSettings) {
            // PerGameSettingsView(titleId: startgame?.titleId ?? "", manager: InGameSettingsManager.shared)
                // .onDisappear() {
                    // InGameSettingsManager.shared.saveSettings()
                // }
        }
    }
    
    func stop() {
        startgame = nil
        RyujinxBridge.stopEmulation()
        try? ryujinx.stop()
    }
    
    func nextAspectRatio(current: AspectRatio) -> AspectRatio {
        let all = AspectRatio.allCases
        if let index = all.firstIndex(of: current) {
            let nextIndex = (index + 1) % all.count
            return all[nextIndex]
        } else {
            return .fixed16x9 // Default fallback
        }
    }

}

// This is just to stop the sound on macOS when doing a keypress
extension View {
    func onKeyPress() -> some View {
        if #available(iOS 17.0, *), ProcessInfo.processInfo.isiOSAppOnMac {
            return AnyView(self
                .focusable()
                .focusEffectDisabled()
                .onKeyPress { _ in
                    return .handled
                })
        } else {
            return AnyView(self)
        }
    }
}
