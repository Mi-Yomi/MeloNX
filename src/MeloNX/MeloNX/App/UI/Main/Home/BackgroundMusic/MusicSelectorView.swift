//
//  MusicSelectorView.swift
//  MeloNX
//
//  Created by Stossy11 on 27/12/2025.
//

import SwiftUI
import AVFoundation

struct MusicSelectorStore: Codable, Equatable {
    var path: String
    var loop: Bool
}

struct MusicItem: Codable, Identifiable {
    var id: String = UUID().uuidString
    var path: String
}

struct MusicSelectorView: View {
    var fileManager: FileManager = .default
    @AppCodableStorage("backgroundMusic") var selectedItem: MusicSelectorStore = .init(path: "", loop: false)
    @State var items: [MusicItem] = []
    let mp3Directory = URL.documentsDirectory.appendingPathComponent("audio")
    static var nativeSettings: NativeSettingsManager = .shared
    static var audioPlayer: AVAudioPlayer?
    static var isPlaying: Bool {
        return audioPlayer?.isPlaying ?? false
    }
    
    static func playMusic() {
        let mp3Directory = URL.documentsDirectory.appendingPathComponent("audio")
        let selectedItem = nativeSettings.backgroundMusic(MusicSelectorStore(path: "", loop: false)).value
        let fileManager: FileManager = .default
        let fullPath = mp3Directory.appendingPathComponent(selectedItem.path)
        
        if !selectedItem.path.isEmpty && fileManager.fileExists(atPath: fullPath.path) {
            do {
                if audioPlayer == nil || !isPlaying {
                    audioPlayer = try AVAudioPlayer(contentsOf: fullPath)
                    audioPlayer?.currentTime = 0
                    audioPlayer?.prepareToPlay()
                    audioPlayer?.play()
                    audioPlayer?.numberOfLoops = selectedItem.loop ? 1000 : 0
                    
                    print("playing music")
                } else {
                    print("Sound is already playing")
                }
            } catch {
                print("Error playing sound: \(error.localizedDescription)")
            }
        }
    }
    
    static func stopMusic() {
        if audioPlayer != nil || isPlaying {
            audioPlayer?.stop()
            audioPlayer = nil
        }
    }
    
    var body: some View {
        iOSNav {
            List {
                if items.isEmpty {
                    emptyStateView
                }
                
                ForEach(items) { music in
                    HStack {
                        Button(music.path) {
                            if music.path == selectedItem.path {
                                selectedItem.path = ""
                            } else {
                                selectedItem.path = music.path
                            }
                            selectedItem.loop = false
                        }
                        Spacer()
                        Image(systemName: music.path == selectedItem.path ? "checkmark.circle.fill" : "circle")
                            .foregroundColor(music.path == selectedItem.path ? .primary : .secondary)
                            .imageScale(.large)
                    }
                    .contextMenu {
                        Button {
                            selectedItem.loop = true
                            
                            if Self.isPlaying {
                                Self.audioPlayer?.numberOfLoops = 0
                            }
                        } label: {
                            Label("Loop", systemImage: selectedItem.loop ? "checkmark" : "circle")
                        }
                        .disabled(music.path != selectedItem.path)
                    }
                }
            }
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    if Self.isPlaying {
                        Button {
                            Self.stopMusic()
                        } label: {
                            Label("Stop", systemImage: "x.circle")
                        }
                    }
                }
        
                ToolbarItem(placement: .navigationBarTrailing) {
                    Button {
                        FileImporterManager.shared.importFiles(types: [.item], allowMultiple: true, completion: handleFileImport)
                    } label: {
                        Label("Add Music", systemImage: "plus")
                    }
                }
            }
            .onAppear() {
                getMP3s()
            }
        }
    }
    
    private var emptyStateView: some View {
        Group {
            if #available(iOS 17, *) {
                ContentUnavailableView(
                    "No Music Found",
                    systemImage: "arrow.down.circle",
                    description: Text("Tap the + button to add background music.")
                )
            } else {
                VStack(spacing: 20) {
                    Spacer()
                    
                    Image(systemName: "arrow.down.circle")
                        .font(.system(size: 64))
                        .foregroundColor(.secondary)
                    
                    Text("No Music Found")
                        .font(.title2)
                        .fontWeight(.semibold)
                    
                    Text("Tap the + button to add background music.")
                        .font(.subheadline)
                        .foregroundColor(.secondary)
                        .multilineTextAlignment(.center)
                        .padding(.horizontal)
                    
                    Spacer()
                }
                .frame(maxWidth: .infinity)
                .listRowInsets(EdgeInsets())
            }
        }
    }
    
    
    private func handleFileImport(result: Result<[URL], Error>) {
        switch result {
        case .success(let urls):
            for url in urls {
                if !fileManager.fileExists(atPath: mp3Directory.path) {
                    try? fileManager.createDirectory(at: mp3Directory, withIntermediateDirectories: true)
                }
                
                try? fileManager.copyItem(at: url, to: mp3Directory.appendingPathComponent(url.lastPathComponent))
                
                getMP3s()
            }
        case .failure(let error):
            print("File import failed: \(error.localizedDescription)")
        }
    }
    
    
    func getMP3s() {
        if let enumerator = fileManager.enumerator(at: mp3Directory, includingPropertiesForKeys: nil) {
            for case let fileURL as URL in enumerator {
                if fileURL.pathExtension != "mp3" {
                    continue
                }
                
                items.append(MusicItem(path: fileURL.lastPathComponent))
            }
            
            if selectedItem.path.isEmpty || !fileManager.fileExists(atPath: mp3Directory.appendingPathComponent(selectedItem.path).path) {
                
                if !fileManager.fileExists(atPath: mp3Directory.appendingPathComponent(items.first?.path ?? "").path) {
                    selectedItem.path = items.first?.path ?? ""
                    selectedItem.loop = false
                }
            }
        }
    }
}
