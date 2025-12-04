//
//  ContentView.swift
//  MeloNX
//
//  Created by Stossy11 on 09/11/2025.
//

import SwiftUI

struct ContentView: View {
    @StateObject var gameHandler = LaunchGameHandler()
    @StateObject var controllerManager = ControllerManager.shared
    @StateObject var ryujinx = Ryujinx.shared
    @Namespace var gameAnimation
    @State private var selectedTab: Tab = .games
    @State var showing = true
    @State var viewShown = false
    
    @ViewBuilder
    var tabView: some View {
        TabView(selection: $selectedTab) {
            GamesListView()
                .tabItem {
                    Label("Library", systemImage: "gamecontroller.fill")
                }
                .tag(Tab.games)
            
            SettingsViewNew()
                .tabItem {
                    Label("Settings", systemImage: "gear")
                }
                .tag(Tab.settings)
        }
        .if(!gameHandler.showApp) { view in
            view.hidden()
        }
    }
    
    var body: some View {
        tabView
            .background {
                if showing {
                    // To load all defaults :3
                    SettingsViewNew().allBody
                }
            }
            .onOpenURL { url in
                handleDeepLink(url)
            }
            .onAppear() {
                controllerManager.initAll()
                enableJIT()
                
                
                Air.play(AnyView(
                    GamesListAirplay()
                        .environmentObject(gameHandler)
                        .environmentObject(ryujinx)
                ))
                
                
                Timer.scheduledTimer(withTimeInterval: 0.5, repeats: false) { _ in
                    showing = false
                }
                
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
                    viewShown = true
                }
            }
            .environmentObject(gameHandler)
            .environmentObject(ryujinx)
            .environment(\.gameNamespace, gameAnimation)
            .fullScreenCover(isPresented: gameHandler.shouldLaunchGame) {
                Group {
                    if #available(iOS 18.0, *) {
                        EmulationContainerView()
                            .navigationTransition(.zoom(sourceID: gameHandler.currentGame?.fileURL.absoluteString ?? "cool", in: gameAnimation))
                    } else {
                        EmulationContainerView()
                    }
                }
                .onAppear {
                    Timer.scheduledTimer(withTimeInterval: 0.5, repeats: false) { _ in
                        gameHandler.showApp = false
                    }
                }
                .onDisappear {
                    gameHandler.showApp = true
                }
                .interactiveDismissDisabled()
                .environmentObject(gameHandler)
                .environmentObject(ryujinx)
            }
            .alert(isPresented: gameHandler.shouldShowEntitlement) {
                Alert(
                    title: Text("Entitlement"),
                    message: Text(LocalizedStringKey("MeloNX **REQUIRES** the Increased Memory Limit entitlement, Please follow the instructions on how to Install MeloNX and Enable the Entitlement.")),
                    primaryButton: .default(Text("Instructions")) {
                        UIApplication.shared.open(
                            URL(string: "https://git.ryujinx.app/melonx/emu#how-to-install")!,
                            options: [:],
                            completionHandler: nil
                        )
                    },
                    secondaryButton: .cancel(Text("Cancel"))
                )
            }
            .fullScreenCover(isPresented: gameHandler.shouldCheckJIT) {
                JITPopover() {
                    
                }
            }
            .if(gameHandler.shouldShowPopover.wrappedValue) { view in
                view
                    .halfScreenSheet(isPresented: gameHandler.shouldShowPopover) {
                        AccountSelector { cool in
                            if cool {
                                gameHandler.profileSelected = true
                            } else {
                                gameHandler.currentGame = nil
                            }
                        }
                    }
            }
    }
    
    private func enableJIT() {
        ryujinx.checkForJIT()
        print("Has TXM? \(ProcessInfo.processInfo.hasTXM)")
        
        if !ryujinx.jitenabled {
            if NativeSettingsManager.shared.useTrollStore.value {
                askForJIT()
            } else if NativeSettingsManager.shared.stikJIT.value {
                enableJITStik()
            } else {
                // nothing
            }
        }
    }
    
    private func handleDeepLink(_ url: URL) {
        Task {
            if let components = URLComponents(url: url, resolvingAgainstBaseURL: true),
               components.host == "game" {
                
                while (!viewShown) {
                    try? await Task.sleep(nanoseconds: 100)
                }
                
                if let text = components.queryItems?.first(where: { $0.name == "id" })?.value {
                    gameHandler.currentGame = ryujinx.games.first(where: { $0.titleId == text })
                    if gameHandler.currentGame == nil {
                        gameHandler.currentGame = ryujinx.games.first(where: { $0.titleName == text })
                    }
                    
                } else if let text = components.queryItems?.first(where: { $0.name == "name" })?.value {
                    gameHandler.currentGame = ryujinx.games.first(where: { $0.titleName == text })
                    
                    if gameHandler.currentGame == nil {
                        gameHandler.currentGame = ryujinx.games.first(where: { $0.titleId == text })
                    }
                }
            }
        }
    }
}

struct GameNamespaceKey: EnvironmentKey {
    static let defaultValue: Namespace.ID? = nil
}

extension EnvironmentValues {
    var gameNamespace: Namespace.ID? {
        get { self[GameNamespaceKey.self] }
        set { self[GameNamespaceKey.self] = newValue }
    }
}
