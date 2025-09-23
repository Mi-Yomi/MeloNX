//
//  ContentView.swift
//  MeloNX
//
//  Created by Stossy11 on 3/11/2024.
//

import SwiftUI
import GameController
import Darwin
import UIKit
import MetalKit
import CoreLocation
import Metal

struct MoltenVKSettings: Codable, Hashable {
    let string: String
    var value: String
}

struct ContentView: View {
    // MARK: - Properties
    
    // Games
    @State private var game: Game?
    
    // Controllers
    @StateObject var controllerManager = ControllerManager.shared
    @State private var isVirtualControllerActive: Bool = false
    @AppStorage("isVirtualController") var isVCA: Bool = true
    
    // Settings and Configuration
    private var config: Ryujinx.Arguments {
        settingsManager.config
    }
    
    @StateObject private var settingsManager = SettingsManager.shared
    
    @State var settings: [MoltenVKSettings]
    
    // JIT
    @AppStorage("useTrollStore") var useTrollStore: Bool = false
    @AppStorage("jitStreamerEB") var jitStreamerEB: Bool = false
    @AppStorage("stikJIT") var stikJIT: Bool = false
    
    // Other Configuration
    @State var isMK8: Bool = false
    @AppStorage("quit") var quit: Bool = false
    @State var quits: Bool = false
    @AppStorage("MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS") var mVKPreFillBuffer: Bool = true
    @AppStorage("MVK_CONFIG_SYNCHRONOUS_QUEUE_SUBMITS") var syncqsubmits: Bool = false
    @AppStorage("ignoreJIT") var ignoreJIT: Bool = false
    @AppStorage("DUAL_MAPPED_JIT") var dualMapped: Bool = false
    @AppStorage("DUAL_MAPPED_JIT_edit") var dualMappededit: Bool = false
    @AppStorage("showProfileonGame") var showProfileonGame: Bool = false
    @AppStorage("LDN_MITM") var ldn = printAllIPv4Addresses().first ?? "Unknown"
    @State var choosedProfile = false
    @State var showSheet = false
    
    // Loading Animation
    @AppStorage("showlogsloading") var showlogsloading: Bool = true
    @State private var clumpOffset: CGFloat = -100
    private let clumpWidth: CGFloat = 100
    private let animationDuration: Double = 1.0
    @State private var isAnimating = false
    @State var isLoading = true
    
    
    // MARK: - CORE
    @StateObject var ryujinx = Ryujinx.shared
    
    // MARK: - SDL
    var sdlInitFlags: UInt32 = SDL_INIT_EVENTS | SDL_INIT_GAMECONTROLLER | SDL_INIT_JOYSTICK | SDL_INIT_AUDIO | SDL_INIT_VIDEO

    // MARK: - Initialization
    init() {
        let defaultSettings: [MoltenVKSettings] = [
            MoltenVKSettings(string: "MVK_USE_METAL_PRIVATE_API", value: "1"),
            MoltenVKSettings(string: "MVK_CONFIG_USE_METAL_PRIVATE_API", value: "1"),
            MoltenVKSettings(string: "MVK_DEBUG", value: "0"),
            // MoltenVKSettings(string: "MVK_CONFIG_LOG_LEVEL", value: "3"),
            MoltenVKSettings(string: "MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS", value: "0"),
            MoltenVKSettings(string: "MVK_CONFIG_MAX_ACTIVE_METAL_COMMAND_BUFFERS_PER_QUEUE", value: "512"),
        ]
        
        if #available(iOS 19, *) {
            setenv("HAS_TXM", ProcessInfo.processInfo.hasTXM ? "1" : "0", 1)
        } else {
            setenv("HAS_TXM", "0", 1)
        }
        
        _settings = State(initialValue: defaultSettings)
        
        initializeSDL()
    }
    
    // MARK: - Body
    var body: some View {
        if game != nil && (ryujinx.jitenabled || ignoreJIT) && (showProfileonGame ? choosedProfile : true) {
            gameView
        } else if game != nil && !ryujinx.jitenabled {
            jitErrorView
        } else {
            mainMenuView
                .halfScreenSheet(isPresented: $showSheet) {
                    AccountSelector() { cool in
                        choosedProfile = cool
                        showSheet = false
                    }
                }
                .onChange(of: game) { _ in
                    showSheet = game != nil && showProfileonGame && !choosedProfile
                }
                .onChange(of: showSheet) { _ in
                    if !showSheet {
                        game = nil
                    }
                }
        }
    }
    
    // MARK: - View Components
    
    private var gameView: some View {
        ZStack {
            if #available(iOS 16, *) {
                EmulationView(startgame: $game)
                    .persistentSystemOverlays(.hidden)
            } else {
                EmulationView(startgame: $game)
            }
            
            if isLoading {
                ZStack {
                    Color.black.opacity(0.8)
                    emulationView.ignoresSafeArea(.all)
                }
                .edgesIgnoringSafeArea(.all)
                .ignoresSafeArea(.all)
            }
        }
    }
    
    private var jitErrorView: some View {
        Text("")
            .fullScreenCover(isPresented:Binding(
                get: { !ryujinx.jitenabled },
                set: { newValue in
                    ryujinx.jitenabled = newValue
                    
                    ryujinx.ryuIsJITEnabled()
                })
            ) {
                JITPopover() {
                    ryujinx.jitenabled = false
                }
            }
    }
    
    private var mainMenuView: some View {
        MainTabView(
            startemu: $game,
            MVKconfig: $settings,
        )
        .onAppear {
            quits = false
            let _ = loadSettings()
            isLoading = true
            
            UserDefaults.standard.set(false, forKey: "lockInApp")
            
            self.controllerManager.initControllerObservers()
            
            Air.play(AnyView(
                ControllerListView(game: $game)
            ))
            
            self.controllerManager.refreshControllersList()
            
            ryujinx.addGames()
            
            checkJitStatus()
        }
        .onOpenURL { url in
            handleDeepLink(url)
        }
    }
    
    private var emulationView: some View {
        GeometryReader { screenGeometry in
            ZStack {
                gameLoadingContent(screenGeometry: screenGeometry)
                
                HStack{
                    
                    VStack {
                        if showlogsloading {
                            LogFileView(isfps: true)
                                .frame(alignment: .topLeading)
                        }
                        
                        Spacer()
                    }
                    
                    Spacer()
                }
            }
        }
    }
    
    @State private var isShaderOrPTC: Bool = false
    @State private var loadingType: String = ""
    @State private var currentProgress: Int = 0
    @State private var totalProgress: Int = 1
    
    // MARK: - Helper Methods
    
    private func gameLoadingContent(screenGeometry: GeometryProxy) -> some View {
        HStack(spacing: screenGeometry.size.width * 0.04) {
            if let icon = game?.icon {
                Image(uiImage: icon)
                    .resizable()
                    .frame(
                        width: min(screenGeometry.size.width * 0.25, 250),
                        height: min(screenGeometry.size.width * 0.25, 250)
                    )
                    .clipShape(RoundedRectangle(cornerRadius: 16))
                    .shadow(color: .black.opacity(0.5), radius: 10, x: 0, y: 5)
            }
            
            VStack(alignment: .leading, spacing: screenGeometry.size.height * 0.015) {
                Text("Loading \(game?.titleName ?? "Game")")
                    .font(.system(size: min(screenGeometry.size.width * 0.04, 32)))
                    .foregroundColor(.white)
                
                loadingProgressBar(screenGeometry: screenGeometry)
                
                if isShaderOrPTC {
                    Text("\(loadingType): \(currentProgress)/\(totalProgress)")
                }
            }
        }
        .padding(.horizontal, screenGeometry.size.width * 0.06)
        .padding(.vertical, screenGeometry.size.height * 0.05)
        .position(
            x: screenGeometry.size.width / 2,
            y: screenGeometry.size.height * 0.5
        )
    }
    
    private func loadingProgressBar(screenGeometry: GeometryProxy) -> some View {
        GeometryReader { geometry in
            let containerWidth = min(screenGeometry.size.width * 0.35, 350)

            ZStack(alignment: .leading) {
                Rectangle()
                    .cornerRadius(10)
                    .frame(width: containerWidth, height: min(screenGeometry.size.height * 0.015, 12))
                    .foregroundColor(.gray.opacity(0.3))
                
                if isShaderOrPTC {
                    Rectangle()
                        .cornerRadius(10)
                        .frame(width: containerWidth * CGFloat(currentProgress) / CGFloat(totalProgress), height: min(screenGeometry.size.height * 0.015, 12))
                        .foregroundColor(.blue)
                }
                
                Rectangle()
                    .cornerRadius(10)
                    .frame(width: clumpWidth, height: min(screenGeometry.size.height * 0.015, 12))
                    .foregroundColor(isShaderOrPTC ? .clear : .blue)
                    .offset(x: isAnimating ? containerWidth : -clumpWidth)
                    .onAppear {
                        withAnimation(Animation.linear(duration: 1.0).repeatForever(autoreverses: false)) {
                            isAnimating = false
                            
                            isAnimating = true
                        }
                    }
            }
            .clipShape(RoundedRectangle(cornerRadius: 16))
            .onAppear {
                Ryujinx.shared.showLoading = true
                
                RegisterCallbackWithData("ProgressWithPTCorShaderCache") { data in
                    guard let rawData = data else { return }

                    if let jsonArray = try? JSONSerialization.jsonObject(with: rawData, options: []) as? [Any],
                       jsonArray.count == 3,
                       let type = jsonArray[0] as? String,
                       let current = jsonArray[1] as? Int,
                       let total = jsonArray[2] as? Int {

                        DispatchQueue.main.async {
                            if current < total - 1 {
                                self.isShaderOrPTC = true
                                self.loadingType = type
                                self.currentProgress = current
                                self.totalProgress = total
                            } else {
                                self.isShaderOrPTC = false
                                
                            }
                        }
                    } else {
                        DispatchQueue.main.async {
                            self.isShaderOrPTC = false
                        }
                    }
                }

                
                setupEmulation()
                
                RegisterCallback("ran-first-frame") { _ in
                    DispatchQueue.main.async {
                        withAnimation {
                            isLoading = false
                            isAnimating = false
                            Ryujinx.shared.showLoading = false
                        }
                    }
                }
            }
        }
        .frame(height: min(screenGeometry.size.height * 0.015, 12))
        .frame(width: min(screenGeometry.size.width * 0.35, 350))
    }
    
    private func initializeSDL() {
        setMoltenVKSettings()
        SDL_SetMainReady()
        SDL_iPhoneSetEventPump(SDL_TRUE)
        SDL_Init(sdlInitFlags)
        initialize()
    }

    private func setupEmulation() {
        isVCA = (controllerManager.currentControllers.first(where: { $0.isVirtualController }) != nil)
        
        DispatchQueue.main.async {
            start(displayid: 1)
        }
    }
    
    
    @StateObject private var persettings = PerGameSettingsManager.shared
    private func start(displayid: UInt32) {
        guard let game else { return }
        var config = self.config
        
        persettings.loadSettings()
        
        if let customgame = persettings.config[game.titleId] {
            config = customgame
        }
        
        
        for index in controllerManager.currentControllers.indices {
            ControllerManager.shared.controllerTypes[index] = controllerManager.currentControllers[index].controllerType
        }
        
        print("\(controllerManager.currentControllers), \(Array(Set(controllerManager.currentControllers.map(\.id))))")
        
        config.gamepath = game.fileURL.path
        config.inputids = Array(Set(controllerManager.currentControllers.map(\.id)))
        
        configureEnvironmentVariables()
        
        controllerManager.registerMotionForMatchingControllers()
        
        config.inputids.isEmpty ? config.inputids.append("0") : ()
        
        // Local DSU loopback to ryujinx per input id
        for _ in config.inputids {
            config.inputDSUServers.append("127.0.0.1:26760")
        }
        
        do {
            try ryujinx.start(with: config)
        } catch {
            // print("Error: \(error.localizedDescription)")
        }
    }
    
    private func configureEnvironmentVariables() {
        if mVKPreFillBuffer {
            mVKPreFillBuffer = false
            // setenv("MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS", "2", 1)
        }
        
        if syncqsubmits {
            setenv("MVK_CONFIG_SYNCHRONOUS_QUEUE_SUBMITS", "1", 1)
        }
        
        if dualMapped {
            setenv("DUAL_MAPPED_JIT", "1", 1)
        } else {
            setenv("DUAL_MAPPED_JIT", "0", 1)
        }
    }
    
    private func setMoltenVKSettings() {
        settings.forEach { setting in
            setenv(setting.string, setting.value, 1)
        }
    }
    
    private func checkJitStatus() {
        ryujinx.ryuIsJITEnabled()
        if jitStreamerEB {
            jitStreamerEB = false // byee jitstreamer eb
        }
        print("Has TXM? \(ProcessInfo.processInfo.hasTXM)")
        if #available(iOS 19, *), !dualMappededit {
            dualMapped = !ProcessInfo.processInfo.isiOSAppOnMac
            dualMappededit = true
        }
        
        if !ryujinx.jitenabled {
            if useTrollStore {
                askForJIT()
            } else if stikJIT {
                enableJITStik()
            } else if jitStreamerEB {
                enableJITEB()
            } else {
                // nothing
            }
        }
    }
    
    private func handleDeepLink(_ url: URL) {
        if let components = URLComponents(url: url, resolvingAgainstBaseURL: true),
           components.host == "game" {
            
            if let text = components.queryItems?.first(where: { $0.name == "id" })?.value {
                game = ryujinx.games.first(where: { $0.titleId == text })
            } else if let text = components.queryItems?.first(where: { $0.name == "name" })?.value {
                game = ryujinx.games.first(where: { $0.titleName == text })
            }
        }
    }
}

extension Array {
    @inlinable public mutating func mutableForEach(_ body: (inout Element) throws -> Void) rethrows {
        for index in self.indices {
            try body(&self[index])
        }
    }
}

class LocationManager: NSObject, CLLocationManagerDelegate {
    
    private var locationManager: CLLocationManager
    
    static let sharedInstance = LocationManager()
    
    private override init() {
        locationManager = CLLocationManager()
        super.init()
        locationManager.delegate = self
        locationManager.desiredAccuracy = kCLLocationAccuracyBest
        locationManager.pausesLocationUpdatesAutomatically = false
    }
    
    func locationManager(_ manager: CLLocationManager, didUpdateLocations locations: [CLLocation]) {
        // print("wow")
    }
    
    func locationManager(_ manager: CLLocationManager, didFailWithError error: Error) {
        print("Location manager failed with: \(error)")
    }
    
    func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) {
        if manager.authorizationStatus == .denied {
            print("Location services are disabled in settings.")
        } else {
            startUpdatingLocation()
        }
    }
    
    func stop() {
        if UserDefaults.standard.bool(forKey: "location-enabled") {
            locationManager.stopUpdatingLocation()
        }
    }
    
    func startUpdatingLocation() {
        if UserDefaults.standard.bool(forKey: "location-enabled") {
            locationManager.requestAlwaysAuthorization()
            locationManager.allowsBackgroundLocationUpdates = true
            locationManager.startUpdatingLocation()
        }
    }
}

struct ControllerListView: View {
    @State private var selectedIndex = 0
    @Binding var game: Game?
    @ObservedObject private var ryujinx = Ryujinx.shared

    var body: some View {
        List(ryujinx.games.indices, id: \.self) { index in
            let game = ryujinx.games[index]

            HStack(spacing: 16) {
                // Game Icon
                Group {
                    if let icon = game.icon {
                        Image(uiImage: icon)
                            .resizable()
                            .aspectRatio(contentMode: .fill)
                    } else {
                        ZStack {
                            RoundedRectangle(cornerRadius: 10)
                            Image(systemName: "gamecontroller.fill")
                                .font(.system(size: 24))
                                .foregroundColor(.gray)
                        }
                    }
                }
                .frame(width: 55, height: 55)
                .cornerRadius(10)

                // Game Info
                VStack(alignment: .leading, spacing: 4) {
                    Text(game.titleName)
                        .font(.system(size: 16, weight: .medium))
                        .foregroundColor(.primary)

                    HStack(spacing: 4) {
                        Text(game.developer)

                        if !game.version.isEmpty && game.version != "0" {
                            Text("•")
                            Text("v\(game.version)")
                        }
                    }
                    .font(.system(size: 14))
                    .foregroundColor(.secondary)
                }

                Spacer()
            }
            .background(selectedIndex == index ? Color.blue.opacity(0.3) : .clear)
        }
        .onAppear(perform: setupControllerObservers)
    }

    private func setupControllerObservers() {
        let dpadHandler: GCControllerDirectionPadValueChangedHandler = { _, _, yValue in
            if yValue == 1.0 {
                selectedIndex = max(0, selectedIndex - 1)
            } else if yValue == -1.0 {
                selectedIndex = min(ryujinx.games.count - 1, selectedIndex + 1)
            }
        }

        for controller in GCController.controllers() {
            print("Controller connected: \(controller.vendorName ?? "Unknown")")
            controller.playerIndex = .index1

            controller.microGamepad?.dpad.valueChangedHandler = dpadHandler
            controller.extendedGamepad?.dpad.valueChangedHandler = dpadHandler

            controller.extendedGamepad?.buttonA.pressedChangedHandler = { _, _, pressed in
                if pressed {
                    print("A button pressed")
                    game = ryujinx.games[selectedIndex]
                }
            }
        }

        NotificationCenter.default.addObserver(
            forName: .GCControllerDidConnect,
            object: nil,
            queue: .main
        ) { _ in
            setupControllerObservers()
        }
    }
}


func printAllIPv4Addresses() -> [String] {
    var ifaddr: UnsafeMutablePointer<ifaddrs>?
    
    var cool: [String] = []

    guard getifaddrs(&ifaddr) == 0, let firstAddr = ifaddr else {
        print("Failed to get network interfaces")
        return []
    }

    var ptr: UnsafeMutablePointer<ifaddrs>? = firstAddr
    while ptr != nil {
        let interface = ptr!.pointee
        let name = String(cString: interface.ifa_name)

        if let addr = interface.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET) {
            var hostname = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            let addrLen = socklen_t(addr.pointee.sa_len)

            let result = getnameinfo(
                addr,
                addrLen,
                &hostname,
                socklen_t(hostname.count),
                nil,
                0,
                NI_NUMERICHOST
            )

            if result == 0 {
                let address = String(cString: hostname)
                print("\(name): \(address)")
                if !cool.contains(where: { $0.contains(address) }), address != "127.0.0.1" {
                    cool.append("\(name): \(address)")
                }
            } else {
                print("\(name): Address lookup failed (\(result))")
            }
        }

        ptr = interface.ifa_next
    }

    freeifaddrs(ifaddr)
    
    if cool.contains(where: { $0.contains("en0") }) {
        let indexToMove = cool.firstIndex(where: { $0.contains("en0") }) ?? 0
        if indexToMove < cool.count && indexToMove != 0 {
            let element = cool.remove(at: indexToMove)
            cool.insert(element, at: 0)
        }
    }
    
    return cool
}

