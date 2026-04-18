//
//  MeloNXApp.swift
//  MeloNX
//
//  Created by Stossy11 on 4/4/2026.
//

import SwiftUI
import SDL3

@main
struct MeloNXApp: App {
    
    init() {
        SDL_SetMainReady()
        SDL_SetiOSEventPump(true)
        SDL_Init(SDL_INIT_EVENTS | SDL_INIT_AUDIO)
        Ryujinx.initialize()
    }
    
    var body: some Scene {
        WindowGroup {
            ContentView()
        }
    }
}
