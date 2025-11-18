//
//  GamesListView.swift
//  MeloNX
//
//  Created by Stossy11 on 10/11/2025.
//

import SwiftUI
import UniformTypeIdentifiers

enum ActiveSheet: Identifiable, Equatable{
    case gameInfo(game: Game)
    case perGameSettings(game: Game)
    // needs to be a full screen sheet so commented out
    // case gameController(game: Game)
    case dlc(game: Game)
    case update(game: Game)
    case account

    var id: String {
        switch self {
        case .gameInfo(let game),
             .perGameSettings(let game),
             // .gameController(let game),
             .dlc(let game),
             .update(let game):
            return "\(type(of: self))-\(game.id)"
        case .account:
            return "account"
        }
    }
}

func bindingGame(_ game: Binding<Game?>) -> Binding<Bool> {
    Binding(
        get: { game.wrappedValue != nil },
        set: { newValue in
            if !newValue {
                game.wrappedValue = nil
            }
        }
    )
}

extension UTType {
    static let nsp = UTType(exportedAs: "com.nintendo.switch-package")
    static let xci = UTType(exportedAs: "com.nintendo.switch-cartridge")
}

struct GamesListView: View {
    @EnvironmentObject var gameHandler: LaunchGameHandler
    @StateObject var perGameSettings = PerGameSettingsManager.shared
    @State var gameRequirements: [GameRequirements] = []
    @EnvironmentObject var ryujinx: Ryujinx
    @Environment(\.gameNamespace) var namespace
    @State var showingAccounts = false
    @AppStorage("enableGridLayout") private var gridLayout: Bool = false
    @AppStorage("legacyUI") private var legacyUI: Bool = false
    @State var activeSheet: ActiveSheet?
    @State var controllerEditor: Game?
    @State var scrollTo: Game?
    
    var firmware: String {
        (ryujinx.fetchFirmwareVersion() == "" ? "0" : ryujinx.fetchFirmwareVersion())
    }
    
    var controllerEdit: Binding<Bool> {
        bindingGame($controllerEditor)
    }
    
    var games: Binding<[Game]> {
        Binding(
            get: { ryujinx.games },
            set: { ryujinx.games = $0 }
        )
    }
    
    var body: some View {
        iOSNav {
            ScrollView {
                if gridLayout {
                    let columns = [
                        GridItem(.adaptive(minimum: 160, maximum: 200), spacing: 16)
                    ]
                    LazyVGrid(columns: columns, spacing: 16) {
                        ForEach(ryujinx.games) { game in
                            GameCardView(
                                game: game,
                                games: games,
                                gameRequirements: $gameRequirements,
                            )
                            .id(game)
                            .iOS18MatchedTransitionSource(id: game.fileURL.absoluteString, in: namespace ?? Namespace().wrappedValue)
                            .contextMenu {
                                gameContextMenu(for: game)
                            }
                        }
                    }
                    .padding(.horizontal)
                    .padding(.top)
                } else {
                    ForEach(ryujinx.games) { game in
                        Section {
                            GameRowView(
                                game: game,
                                games: games,
                                gameRequirements: $gameRequirements
                            )
                            .id(game)
                            .iOS18MatchedTransitionSource(id: game.fileURL.absoluteString, in: namespace ?? Namespace().wrappedValue)
                            .padding(.horizontal)
                            .padding(.vertical, 5)
                            .contextMenu {
                                gameContextMenu(for: game)
                            }
                        }
                    }
                }
            }
            .padding(.top)
            .overlay {
                if ryujinx.jitenabled {
                    VStack {
                        HStack {
                            Spacer()
                            Circle()
                                .frame(width: 12, height: 12)
                                .padding(.horizontal, 8)
                                .padding(.vertical, 4)
                                .foregroundColor(checkAppEntitlement("com.apple.developer.kernel.increased-memory-limit") ? Color.green : Color.orange)
                                .padding()
                        }
                        Spacer()
                    }
                    .offset(x: 0, y: -25)
                }
            }
            .onAppear() {
                scrollTo = gameHandler.currentGame
            }
            .navigationTitle("Library")
            .toolbar {
                toolbarHandler()
            }
            .fullScreenCover(isPresented: controllerEdit) {
                ControllerView(isEditing: controllerEdit, gameId: controllerEditor?.titleId ?? "")
                    .interactiveDismissDisabled(true)
            }
            .sheet(item: $activeSheet) { sheet in
                switch sheet {
                case .gameInfo(let game):
                    GameInfoSheet(game: game)
                case .perGameSettings(let game):
                    PerGameSettingsView(titleId: game.titleId)
                case .dlc(let game):
                    DLCManagerSheet(game: game)
                case .update(let game):
                    UpdateManagerSheet(game: game)
                case .account:
                    AccountManagerView()
                }
            }
        }
        .onAppear() {
            ryujinx.addGames()
        }
    }
    
    

    private func gameContextMenu(for game: Game) -> some View {
        Group {
            Section {
                Button {
                    gameHandler.currentGame = game
                } label: {
                    Label("Play Now", systemImage: "play.fill")
                }
                
                Button {
                    activeSheet = .gameInfo(game: game)
                } label: {
                    Label("Game Info", systemImage: "info.circle")
                }
                
                Button {
                    activeSheet = .perGameSettings(game: game)
                } label: {
                    Label("\(game.titleName) Settings", systemImage: "gear")
                }
                
                Button {
                    controllerEditor = game
                } label: {
                    Label("Controller Layout", systemImage: "formfitting.gamecontroller")
                }
            }
            
            Section {
                Button {
                    activeSheet = .update(game: game)
                } label: {
                    Label("Update Manager", systemImage: "arrow.up.circle")
                }
                
                Button {
                    activeSheet = .dlc(game: game)
                } label: {
                    Label("DLC Manager", systemImage: "plus.circle")
                }
            }
            
            Section {
                
                Button(role: .destructive) {
                    Ryujinx.clearShaderCache(game.titleId)
                } label: {
                    Label("Clear Shader Cache", systemImage: "trash")
                }
                
                Button(role: .destructive) {
                    deleteGame(game: game)
                } label: {
                    Label("Delete Game", systemImage: "trash")
                }
            }
        }
    }
    
    private func deleteGame(game: Game) {
        let fileManager = FileManager.default
        do {
            try fileManager.removeItem(at: game.fileURL)
            Ryujinx.shared.games.removeAll { $0.id == game.id }
            Ryujinx.shared.games = Ryujinx.shared.loadGames()
        } catch {
        }
    }
}
