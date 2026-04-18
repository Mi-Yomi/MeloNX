//
//  ContentView.swift
//  MeloNX
//
//  Created by Stossy11 on 4/4/2026.
//

import SwiftUI
import MetalKit

struct EnvironmentVariable: Codable, Hashable {
    let string: String
    var value: String
    
    func set() {
        setenv(string, value, 1)
    }
    
    static func set(_ env: EnvironmentVariable) {
        setenv(env.string, env.value, 1)
    }
}


struct ContentView: View {
    var body: some View {
        VStack {
            MetalView()
        }
        .padding()
        .onAppear {
            EnvironmentVariable(string: "HAS_TXM", value: ProcessInfo.processInfo.hasTXM && !ProcessInfo.processInfo.isiOSAppOnMac ? "1" : "0").set()
            EnvironmentVariable(string: "DUAL_MAPPED_JIT", value: "1").set()
            
            runpleaseibegyou()
            print(URL.documentsDirectory)
            
        }
    }
    
    func runpleaseibegyou() {
        let beans = buildCommandLineArgs(path: URL.documentsDirectory.appendingPathComponent("fuck.nsp").path)
        
        RunLoop.main.perform {
            print(Ryujinx.mainRyu(argv: beans))
        }
    }
    
    func buildCommandLineArgs(path: String) -> [String] {
        var args: [String] = []
        
        args.append(path)
        
        // Starts with vulkan
        args.append("--graphics-backend")
        args.append("Vulkan")
        
        args.append("--disable-shader-cache")
        args.append("--disable-docked-mode")
        
        return args
    }
}

extension URL {
    @available(iOS, introduced: 14.0, deprecated: 16.0, message: "Use URL.documentsDirectory on iOS 16 and above")
    static var documentsDirectory: URL {
        let documentDirectory = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first!
        return documentDirectory
    }
}

struct MetalView: UIViewRepresentable {
    func makeUIView(context: Context) -> UIView {
        return Self.createView()
    }
    
    func updateUIView(_ uiView: UIView, context: Context) {
        // nothin
    }
    
    @discardableResult
    static func createView() -> UIView {
        if Ryujinx.emulationView == nil {
            let view = MTKView()
            
            guard let metalLayer = view.layer as? CAMetalLayer else {
                fatalError("[Swift] Error: MTKView's layer is not a CAMetalLayer")
            }
            
            UIApplication.shared.isIdleTimerDisabled = true
            
            //metalLayer.presentsWithTransaction = false
            //metalLayer.allowsNextDrawableTimeout = false
            
            
            let framesSelector = NSSelectorFromString("setNominalFramesPerSecond:")
            
            if metalLayer.responds(to: framesSelector) {
                metalLayer.perform(framesSelector, with: 60 as NSNumber)
            }
            
            let setterSelector = NSSelectorFromString("setDisplaySyncEnabled:")
            
            if metalLayer.responds(to: setterSelector) {
                metalLayer.perform(setterSelector, with: NSNumber(value: false))
            }
            
            metalLayer.device == nil ? () : (metalLayer.device = MTLCreateSystemDefaultDevice())
            
            let layerPtr = Unmanaged.passUnretained(metalLayer).toOpaque()
            
            Ryujinx.setNativeWindow(layerPtr)
            
            Ryujinx.emulationView = view
            return view
        }
        
        return Ryujinx.emulationView!
    }
}

extension FileManager {
    func filePath(atPath path: String, withLength length: Int) -> String? {
        guard let file = try? contentsOfDirectory(atPath: path).filter({ $0.count == length }).first else { return nil }
        return "\(path)/\(file)"
    }
}

public extension ProcessInfo {
    var hasTXM: Bool {
        { if let boot = FileManager.default.filePath(atPath: "/System/Volumes/Preboot", withLength: 36), let file = FileManager.default.filePath(atPath: "\(boot)/boot", withLength: 96) { return access("\(file)/usr/standalone/firmware/FUD/Ap,TrustedExecutionMonitor.img4", F_OK) == 0 } else { return (FileManager.default.filePath(atPath: "/private/preboot", withLength: 96).map { access("\($0)/usr/standalone/firmware/FUD/Ap,TrustedExecutionMonitor.img4", F_OK) == 0 }) ?? false } }()
    }
}




#Preview {
    ContentView()
}
