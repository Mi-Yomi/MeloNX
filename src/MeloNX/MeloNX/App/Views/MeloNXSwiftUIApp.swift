//
//  MeloNXApp.swift
//  MeloNX
//
//  Created by Stossy11 on 3/11/2024.
//

import SwiftUI
import UIKit
import CryptoKit
import UniformTypeIdentifiers
import AVFoundation


extension UIDocumentPickerViewController {
    @objc func fix_init(forOpeningContentTypes contentTypes: [UTType], asCopy: Bool) -> UIDocumentPickerViewController {
        return fix_init(forOpeningContentTypes: contentTypes, asCopy: true)
    }
}


@main
struct MeloNXApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) var appDelegate
    @State var showed = false
    @Environment(\.scenePhase) var scenePhase
    @State var alert: UIAlertController? = nil
    
    @State var showOutOfDateSheet = false
    @State var updateInfo: LatestVersionResponse? = nil
    
    @StateObject var metalHudEnabler = MTLHud.shared
    
    @State var finished = false
    @AppStorage("hasbeenfinished") var finishedStorage: Bool = false
    
    @AppStorage("location-enabled") var locationenabled: Bool = false
    @AppStorage("checkForUpdate") var checkForUpdate: Bool = true
    
    @AppStorage("runOnMainThread") var runOnMainThread = false
    
    @AppStorage("autoJIT") var autoJIT = false
    
    @State var fourgbiPad = false
    @State var ios19 = false
    @AppStorage("4GB iPad") var ignores = false
    @AppStorage("iOS19") var ignores19 = false
    @AppStorage("DUAL_MAPPED_JIT") var dualMapped: Bool = false
    @AppStorage("DUAL_MAPPED_JIT_edit") var dualMappededit: Bool = false
    // String(format: "%.0f GB", Double(totalMemory) / 1_000_000_000)
    


    var body: some Scene {
        WindowGroup {
            Group {
                if finishedStorage {
                    ContentView()
                        .onAppear {
                            if checkForUpdate {
                                checkLatestVersion()
                            }
                            
                            print(metalHudEnabler.canMetalHud)
                            
                            UserDefaults.standard.set(false, forKey: "lockInApp")
                        }
                        .sheet(isPresented: Binding(
                            get: { showOutOfDateSheet && updateInfo != nil },
                            set: { newValue in
                                if !newValue {
                                    showOutOfDateSheet = false
                                    updateInfo = nil
                                }
                            }
                        )) {
                            if let updateInfo = updateInfo {
                                MeloNXUpdateSheet(updateInfo: updateInfo, isPresented: $showOutOfDateSheet)
                            }
                        }
                } else {
                    SetupView(finished: $finished)
                        .onChange(of: finished) { newValue in
                            withAnimation(.easeOut) {
                                finishedStorage = newValue
                            }
                            
                            if #available(iOS 19, *), newValue {
                                dualMapped = !ProcessInfo.processInfo.isiOSAppOnMac
                                dualMappededit = true
                            }
                        }
                }
            }
            .onAppear() {
                setup26JITHandler()
                if #available(iOS 19, *), ProcessInfo.processInfo.hasTXM, !ignores19 {
                    ios19 = true
                }
                
                if UIDevice.current.userInterfaceIdiom == .pad && !ignores {
                    print((Double(ProcessInfo.processInfo.physicalMemory) / 1_000_000_000))
                    if round(Double(ProcessInfo.processInfo.physicalMemory) / 1_000_000_000) <= 4 {
                        fourgbiPad = true
                    }
                }
            }
            .alert("Unsupported Device", isPresented: $fourgbiPad) {
                Button("Continue") {
                    ignores = true
                    fourgbiPad = false
                }
            } message: {
                Text("Your Device is an iPad with \(String(format: "%.0f GB", Double(ProcessInfo.processInfo.physicalMemory) / 1_000_000_000)) of memory, MeloNX has issues with those devices")
            }
        }
    }
    
    func checkLatestVersion() {
        let appVersion = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.0.0"
        let strippedAppVersion = appVersion.replacingOccurrences(of: ".", with: "")
        
        #if DEBUG
        let urlString = "http://192.168.178.116:8000/api/latest_release"
        #else
        let urlString = "https://melonx.org/api/latest_release"
        #endif
        
        guard let url = URL(string: urlString) else {
            // print("Invalid URL")
            return
        }
        
        let task = URLSession.shared.dataTask(with: url) { data, response, error in
            if let error = error {
                // print("Error checking for new version: \(error)")
                return
            }
            
            guard let data = data else {
                // print("No data received")
                return
            }
            
            do {
                let latestVersionResponse = try JSONDecoder().decode(LatestVersionResponse.self, from: data)
                let latestAPIVersionStripped = latestVersionResponse.version_number_stripped
                
                if Int(strippedAppVersion) ?? 0 > Int(latestAPIVersionStripped) ?? 0 {
                   Task { @MainActor in
                        updateInfo = latestVersionResponse
                        showOutOfDateSheet = true
                    }
                }
            } catch {
                // print("Failed to decode response: \(error)")
            }
        }
        
        task.resume()
    }
}

func changeAppUI(_ string: String) -> String? {
    guard let data = Data(base64Encoded: string) else { return nil }
    return String(data: data, encoding: .utf8)
}

func isDebuggerAttached() -> Bool {
    var info = kinfo_proc()
    var mib = [CTL_KERN, KERN_PROC, KERN_PROC_PID, getpid()]
    var size = MemoryLayout.stride(ofValue: info)
    
    let result = sysctl(&mib, u_int(mib.count), &info, &size, nil, 0)
    if result != 0 {
        perror("sysctl failed")
        return false
    }
    
    return (info.kp_proc.p_flag & P_TRACED) != 0
}

func trapHandler(signal: Int32, info: UnsafeMutablePointer<__siginfo>?, uap: UnsafeMutableRawPointer?) {
    guard let uap = uap else { return }
    
    let context = uap.assumingMemoryBound(to: ucontext_t.self)
    
#if arch(arm64)
    if !isDebuggerAttached() {
        context.pointee.uc_mcontext.pointee.__ss.__x.0 = 0
    }
    
    context.pointee.uc_mcontext.pointee.__ss.__pc += 4
#elseif arch(x86_64)
    if !isDebuggerAttached() {
        context.pointee.uc_mcontext.pointee.__ss.__rax = 0
    }
    context.pointee.uc_mcontext.pointee.__ss.__rip += 1
#endif
}

func setup26JITHandler() {
    var sa = sigaction()
    sa.__sigaction_u.__sa_sigaction = trapHandler
    sa.sa_flags = SA_SIGINFO
    sigemptyset(&sa.sa_mask)
    sigaction(SIGTRAP, &sa, nil)
}
