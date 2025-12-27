//
//  LaunchGameHandler.swift
//  MeloNX
//
//  Created by Stossy11 on 10/11/2025.
//

import Combine
import Foundation
import SwiftUI

class LaunchGameHandler: ObservableObject {
    @Published var currentGame: Game? = nil
    @Published var profileSelected = false
    @Published var showApp: Bool = true
    @Published var isPortrait: Bool = true
    static var succeededJIT: Bool = true
    @ObservedObject private var ryujinx = Ryujinx.shared
    @ObservedObject private var nativeSettings = NativeSettingsManager.shared
    @ObservedObject private var settingsManager = SettingsManager.shared
    @ObservedObject private var persettings = PerGameSettingsManager.shared
    @ObservedObject private var controllerManager = ControllerManager.shared
    
    
    private var config: Ryujinx.Arguments {
        settingsManager.config
    }
    
    var shouldLaunchGame: Binding<Bool> {
        Binding(
            get: { self.checkForGame() && checkAppEntitlement("com.apple.developer.kernel.increased-memory-limit") },
            set: { newValue in
                print(newValue)
            }
        )
    }
    
    var shouldShowEntitlement: Binding<Bool> {
        Binding(
            get: { self.checkForGame() && !checkAppEntitlement("com.apple.developer.kernel.increased-memory-limit") },
            set: { newValue in
                print(newValue)
            }
        )
    }
    
    var shouldShowPopover: Binding<Bool> {
        Binding(
            get: { self.currentGame != nil && self.ryujinx.jitenabled && !self.profileSelected && self.nativeSettings.showProfileonGame.value && checkAppEntitlement("com.apple.developer.kernel.increased-memory-limit") },
            set: { newValue in
                print(newValue)
            }
        )
    }
    
    var shouldCheckJIT: Binding<Bool> {
        Binding(
            get: { self.currentGame != nil && !self.ryujinx.jitenabled && !(self.nativeSettings.ignoreJIT.value as Bool) &&  checkAppEntitlement("com.apple.developer.kernel.increased-memory-limit") },
            set: { newValue in
                print(newValue)
            }
        )
    }
    
    func checkForGame() -> Bool {
        self.currentGame != nil && (self.nativeSettings.ignoreJIT.value as Bool ? true : self.ryujinx.jitenabled) && (self.nativeSettings.showProfileonGame.value ? self.profileSelected : true)
    }
    
    func enableJIT() {
        ryujinx.checkForJIT()
        print("Has TXM? \(ProcessInfo.processInfo.hasTXM)")
        
        if !ryujinx.jitenabled {
            if nativeSettings.useTrollStore.value {
                let setting = nativeSettings.setting(forKey: "gametorun", default: "")
                nativeSettings.setting(forKey: "gametorun-date", default: "").value = "\(Date().timeIntervalSince1970)"
                setting.value = currentGame?.titleId ?? ""
                askForJIT()
            } else if nativeSettings.stikJIT.value {
                let setting = nativeSettings.setting(forKey: "gametorun", default: "")
                nativeSettings.setting(forKey: "gametorun-date", default: "").value = "\(Date().timeIntervalSince1970)"
                setting.value = currentGame?.titleId ?? ""
                
                enableJITStik()
            } else {
                // nothing
            }
        }
        
    }
    
    
    func startGame() {
        enableJIT()
        MusicSelectorView.stopMusic()
        
        nativeSettings.isVirtualController.value = controllerManager.hasVirtualController()
        
        MetalView.createView()
        
        guard let currentGame else { return }
        var config = self.config
        
        persettings.loadSettings()
        
        if let customgame = persettings.config[currentGame.titleId] {
            config = customgame
        }
        
        controllerManager.registerControllerTypeForMatchingControllers()
        
        config.gamepath = currentGame.fileURL.path
        config.inputids = Array(Set(controllerManager.selectedControllers))
        
        print(config.inputids)
        
        configureEnvironmentVariables()
        
        config.inputids.isEmpty ? config.inputids.append("0") : ()
        
        // LogCapture.shared.startCapturing()
        
        do {
            try ryujinx.start(with: config)
        } catch {
            
        }
    }
    
    private func configureEnvironmentVariables() {
        // this in case you set the Dual Mapped JIT option after app launched
        let cool: Bool
        if #available(iOS 19, *) {
            cool = nativeSettings.setting(forKey: "DUAL_MAPPED_JIT", default: true).value
        } else {
            cool = nativeSettings.setting(forKey: "DUAL_MAPPED_JIT", default: false).value
        }
        
        if cool {
            setenv("DUAL_MAPPED_JIT", "1", 1)
            Self.succeededJIT = RyujinxBridge.initialize_dualmapped()
        } else {
            setenv("DUAL_MAPPED_JIT", "0", 1)
        }
        
        guard let device = MTLCreateSystemDefaultDevice() else {
            setenv("MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS", "1", 1)
            return
        }
        
        let tier = device.argumentBuffersSupport
        if tier.rawValue >= MTLArgumentBuffersTier.tier2.rawValue {
            setenv("MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS", "1", 1)
        } else {
            setenv("MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS", "0", 1)
        }
    }
}
