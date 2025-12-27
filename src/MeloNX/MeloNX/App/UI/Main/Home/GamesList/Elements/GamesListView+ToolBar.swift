//
//  GamesListView+ToolBar.swift
//  MeloNX
//
//  Created by Stossy11 on 10/11/2025.
//

import SwiftUI

extension GamesListView {
    func toolbarHandler() -> some ToolbarContent {
        Group {
            ToolbarItem(placement: .topBarTrailing) {
                Button {
                    FileImporterManager.shared.importFiles(types: [.nsp, .xci, .item]) { result in
                        ImportHandler.handleAddingGame(result: result)
                    }
                } label: {
                    Label("Add Game", systemImage: "plus")
                        .labelStyle(.iconOnly)
                        .font(.system(size: 16, weight: .semibold))
                }
                .accentColor(.blue)
            }

            ToolbarItem(placement: .topBarLeading) {
                Menu {
                    firmwareSection
                    
                    Divider()
                    
                    Button {
                        FileImporterManager.shared.importFiles(types: [.nsp, .xci, .item]) { result in
                            ImportHandler.handleRunningGame(result: result, gameHandler: gameHandler)
                        }
                    } label: {
                        Label("Open Game", systemImage: "square.and.arrow.down")
                    }
                    
                    Button {
                        let documentsUrl = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first!
                        var sharedurl = documentsUrl.absoluteString.replacingOccurrences(of: "file://", with: "shareddocuments://")
                        if ProcessInfo.processInfo.isiOSAppOnMac {
                            sharedurl = documentsUrl.absoluteString
                        }
                        if UIApplication.shared.canOpenURL(URL(string: sharedurl)!) {
                            UIApplication.shared.open(URL(string: sharedurl)!, options: [:])
                        }
                    } label: {
                        Label("Show MeloNX Folder", systemImage: "folder")
                    }
                    
                    Divider()
                    
                    Button {
                        self.activeSheet = .account
                    } label: {
                        Label("Profile Manager", systemImage: "person.2")
                    }
                    
                } label: {
                    Label("Options", systemImage: "ellipsis.circle")
                        .labelStyle(.iconOnly)
                        .foregroundColor(.blue)
                }
            }
        }
    }

    private var firmwareSection: some View {
        Group {
            if firmware == "0" {
                Button {
                    FileImporterManager.shared.importFiles(types: [.folder, .zip]) { result in
                        ImportHandler.handleFirmwareImport(result: result)
                    }
                } label: {
                    Label("Install Firmware", systemImage: "square.and.arrow.down")
                }
            
            } else {
                Button {
                    
                } label: {
                    Text("Firmware: \(firmware)")
                }
                
                Menu("Applets") {
                    Button {
                        let game = Game(containerFolder: URL(string: "none")!, fileType: .item, fileURL: URL(string: "0x0100000000001009")!, titleName: "Mii Maker", titleId: "0", developer: "Nintendo", version: firmware)
                        self.gameHandler.currentGame = game
                    } label: {
                        Label("Launch Mii Maker", systemImage: "person.crop.circle")
                    }
                }
            }
        }
    }
}
