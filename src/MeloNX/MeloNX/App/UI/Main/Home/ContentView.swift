//
//  ContentView.swift
//  MeloNX
//
//  Created by Stossy11 on 09/11/2025.
//

import OSLog
import SwiftUI

struct ContentView: View {
    @StateObject var gameHandler = LaunchGameHandler()
    @StateObject var controllerManager = ControllerManager.shared
    @StateObject var ryujinx = Ryujinx.shared
    @Namespace var gameAnimation
    @State private var selectedTab: Tab = .games
    @State var showing = true
    @Binding var viewShown: Bool
    @State var launchedFromURL = false
    @State var date: Date?
    
    @AppStorage("gametorun") var gametorun: String = ""
    @AppStorage("gametorun-date") var gametorunDate: String = ""
    
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
                launchedFromURL = true
                handleDeepLink(url)
            }
            .onAppear() {
                controllerManager.initAll()
                
                MusicSelectorView.playMusic()
                
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                    if !launchedFromURL {
                        gameHandler.enableJIT()
                    }
                }
                
                Air.play(AnyView(
                    GamesListAirplay()
                        .environmentObject(gameHandler)
                        .environmentObject(ryujinx)
                ))
                
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.45) {
                    showing = false
                }
                
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
                    viewShown = true
                    
                    Task { @MainActor in
                        checkJITAndRunGame()
                    }
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
                .environmentObject(gameHandler)
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
    
    func checkJITAndRunGame(attempt: Int = 0) {
        if gametorun.isEmpty { return }
        
        guard attempt < 6 else { return }

        if isJITEnabled() {
            if let timeInterval = TimeInterval(gametorunDate) {
                let date = Date(timeIntervalSince1970: timeInterval)
                let isMoreThan60SecondsOld = Date().timeIntervalSince(date) > 60
                if !isMoreThan60SecondsOld {
                    gameHandler.currentGame = ryujinx.games.first(where: { $0.titleId == gametorun || $0.titleName == gametorun })
                }
            } else {
                gameHandler.currentGame = ryujinx.games.first(where: { $0.titleId == gametorun || $0.titleName == gametorun })
            }

            gametorunDate = ""
            gametorun = ""
        } else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                checkJITAndRunGame(attempt: attempt + 1)
            }
        }
    }
    
    
    //         if let data = try? JSONEncoder().encode(controllerTypes)
    
    private func handleDeepLink(_ url: URL) {
        Task {
            if let components = URLComponents(url: url, resolvingAgainstBaseURL: true) {
                switch components.host {
                case "game":
                    while (!viewShown) {
                        try? await Task.sleep(nanoseconds: 100)
                    }
                    
                    if let text = components.queryItems?.first(where: { $0.name == "id" })?.value {
                        gameHandler.currentGame = ryujinx.games.first(where: { $0.titleId == text || $0.titleName == text })
                        
                    } else if let text = components.queryItems?.first(where: { $0.name == "name" })?.value {
                        gameHandler.currentGame = ryujinx.games.first(where: { $0.titleId == text || $0.titleName == text })
                    }
                case "gameInfo":
                    guard let urlscheme = components.queryItems?.first(where: { $0.name == "scheme" })?.value else { return }

                    if let data = try? JSONEncoder().encode(ryujinx.games.map({ GameScheme($0) })) {
                        let string = data.base64urlEncodedString()
                        if let url = URL(string: urlscheme + "://" + (url.scheme ?? "melonx") + "?games=" + string) {
                            await UIApplication.shared.open(url)
                            if !ryujinx.jitenabled {
                                exit(0)
                            }
                        }
                    }
                default:
                    return
                }
                
            }
        }
    }
}

extension Data {
    
    public func base64urlEncodedString() -> String {
        var result = self.base64EncodedString()
        result = result.replacingOccurrences(of: "+", with: "-")
        result = result.replacingOccurrences(of: "/", with: "_")
        result = result.replacingOccurrences(of: "=", with: "")
        return result
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
