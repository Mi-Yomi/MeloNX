//
//  Ryujinx.swift
//  MeloNX
//
//  Created by Stossy11 on 3/11/2024.
//

import Foundation
import SwiftUI
import GameController
import MetalKit
import Metal
import Darwin

class LogCapture {
    static let shared = LogCapture()

    private var stdoutPipe: Pipe?
    private var stderrPipe: Pipe?
    private let originalStdout: Int32
    private let originalStderr: Int32

    var capturedLogs: [String] = [] {
        didSet {
            DispatchQueue.main.async {
                NotificationCenter.default.post(name: .newLogCaptured, object: nil)
            }
        }
    }

    private init() {
        originalStdout = dup(STDOUT_FILENO)
        originalStderr = dup(STDERR_FILENO)
        startCapturing()
    }

    func startCapturing() {
        stdoutPipe = Pipe()
        stderrPipe = Pipe()

        redirectOutput(to: stdoutPipe!, fileDescriptor: STDOUT_FILENO)
        redirectOutput(to: stderrPipe!, fileDescriptor: STDERR_FILENO)

        setupReadabilityHandler(for: stdoutPipe!, isStdout: true)
        setupReadabilityHandler(for: stderrPipe!, isStdout: false)
    }

    func stopCapturing() {
        dup2(originalStdout, STDOUT_FILENO)
        dup2(originalStderr, STDERR_FILENO)

        stdoutPipe?.fileHandleForReading.readabilityHandler = nil
        stderrPipe?.fileHandleForReading.readabilityHandler = nil
    }

    private func redirectOutput(to pipe: Pipe, fileDescriptor: Int32) {
        dup2(pipe.fileHandleForWriting.fileDescriptor, fileDescriptor)
    }

    private func setupReadabilityHandler(for pipe: Pipe, isStdout: Bool) {
        pipe.fileHandleForReading.readabilityHandler = { [weak self] fileHandle in
            let data = fileHandle.availableData
            let originalFD = isStdout ? self?.originalStdout : self?.originalStderr
            write(originalFD ?? STDOUT_FILENO, (data as NSData).bytes, data.count)

            if let logString = String(data: data, encoding: .utf8),
               let cleanedLog = self?.cleanLog(logString), !cleanedLog.isEmpty {
                self?.capturedLogs.append(cleanedLog)
            }
        }
    }

    private func cleanLog(_ raw: String) -> String? {
        let lines = raw.split(separator: "\n")
        let filteredLines = lines.filter { line in
            !line.contains("SwiftUI") &&
            !line.contains("ForEach") &&
            !line.contains("VStack") &&
            !line.contains("Invalid frame dimension (negative or non-finite).")
        }

        let cleaned = filteredLines.map { line -> String in
            if let tabRange = line.range(of: "\t") {
                return line[tabRange.upperBound...].trimmingCharacters(in: .whitespacesAndNewlines)
            }
            return line.trimmingCharacters(in: .whitespacesAndNewlines)
        }.joined(separator: "\n")

        return cleaned.isEmpty ? nil : cleaned.replacingOccurrences(of: "\n\n", with: "\n")
    }

    deinit {
        stopCapturing()
    }
}


extension Notification.Name {
    static let newLogCaptured = Notification.Name("newLogCaptured")
}



struct iOSNav<Content: View>: View {
    @ViewBuilder var content: () -> Content

    var body: some View {
        if #available(iOS 16, *) {
            NavigationStack(root: content)
        } else {
            NavigationView(content: content)
                .navigationViewStyle(StackNavigationViewStyle())
                .navigationViewStyle(.stack)
        }
    }
}

func threadEntry(_ arg: () -> Void) -> UnsafeMutableRawPointer? {
    arg()
    return nil
}




class Ryujinx : ObservableObject {
    @Published var isRunning = false
    @Published var showLoading = true
    @AppStorage("LDN_MITM") var ldn = printAllIPv4Addresses().first ?? "Unknown"
    @Published var controllerType: [String: ControllerType] = [:]
    
    let virtualController = VirtualController()
    
    @Published var controllerMap: [Controller] = []
    @Published var metalLayer: CAMetalLayer? = nil
    @Published var isPortrait = false
    @Published var firmwareversion = "0"
    @Published var emulationUIView: MeloMTKView? = nil
    @Published var config: Ryujinx.Arguments? = nil
    @Published var games: [Game] = []
    @Published var aspectRatio: AspectRatio = .fixed16x9
    
    @Published var defMLContentSize: CGFloat?
    
    var thread: pthread_t? = nil
    
    @Published var jitenabled = false
    
    var shouldMetal: Bool {
        metalLayer == nil
    }
    
    static let shared = Ryujinx()

    func addGames() {
        self.games = loadGames()
    }
    
    func runloop(_ cool: @escaping () -> Void) {
        if UserDefaults.standard.bool(forKey: "runOnMainThread") {
            RunLoop.main.perform {
                cool()
            }
        } else {
            // Box the closure
            let boxed = Unmanaged.passRetained(ClosureBox(cool)).toOpaque()

            var thread: pthread_t?
            let result = pthread_create(&thread, nil, { arg in
                let unmanaged = Unmanaged<ClosureBox>.fromOpaque(arg)
                let box = unmanaged.takeRetainedValue()
                box.closure()
                return nil
            }, boxed)

            if result == 0 {
                pthread_detach(thread!)
            } else {
                print("Failed to create thread: \(result)")
                Unmanaged<ClosureBox>.fromOpaque(boxed).release()
            }
        }
    }

    private class ClosureBox {
        let closure: () -> Void
        init(_ closure: @escaping () -> Void) {
            self.closure = closure
        }
    }
    
    public class Arguments : Observable, Codable, Equatable {
        var gamepath: String
        var inputids: [String]
        var inputDSUServers: [String]
        var resscale: Float = 1.0
        var debuglogs: Bool = false
        var tracelogs: Bool = false
        var nintendoinput: Bool = true
        var enableInternet: Bool = false
        var ldn_mitm: Bool = false
        var listinputids: Bool = false
        var aspectRatio: AspectRatio = .fixed16x9
        var memoryManagerMode: String = "HostMappedUnsafe"
        var disableShaderCache: Bool = false
        var hypervisor: Bool = false
        var disableDockedMode: Bool = false
        var enableTextureRecompression: Bool = true
        var additionalArgs: [String] = []
        var maxAnisotropy: Float = 1.0
        var macroHLE: Bool = true
        var ignoreMissingServices: Bool = false
        var expandRam: Bool = false
        var dfsIntegrityChecks: Bool = false
        var disablePTC: Bool = false
        var disablevsync: Bool = false
        var language: SystemLanguage = .americanEnglish
        var regioncode: SystemRegionCode = .usa
        var handHeldController: Bool = true
        
        
        init(gamepath: String = "",
             inputids: [String] = [],
             inputDSUServers: [String] = [],
             debuglogs: Bool = false,
             tracelogs: Bool = false,
             listinputids: Bool = false,
             aspectRatio: AspectRatio = .fixed16x9,
             memoryManagerMode: String = "HostMappedUnsafe",
             disableShaderCache: Bool = false,
             disableDockedMode: Bool = false,
             nintendoinput: Bool = true,
             enableInternet: Bool = false,
             ldn_mitm: Bool = false,
             enableTextureRecompression: Bool = true,
             additionalArgs: [String] = [],
             resscale: Float = 1.00,
             maxAnisotropy: Float = 0,
             macroHLE: Bool = false,
             ignoreMissingServices: Bool = false,
             hypervisor: Bool = false,
             expandRam: Bool = false,
             dfsIntegrityChecks: Bool = false,
             disablePTC: Bool = false,
             disablevsync: Bool = false,
             language: SystemLanguage = .americanEnglish,
             regioncode: SystemRegionCode = .usa,
             handHeldController: Bool = false
        ) {
            self.gamepath = gamepath
            self.inputids = inputids
            self.inputDSUServers = inputDSUServers
            self.debuglogs = debuglogs
            self.tracelogs = tracelogs
            self.listinputids = listinputids
            self.aspectRatio = aspectRatio
            self.disableShaderCache = disableShaderCache
            self.disableDockedMode = disableDockedMode
            self.enableTextureRecompression = enableTextureRecompression
            self.additionalArgs = additionalArgs
            self.memoryManagerMode = memoryManagerMode
            self.resscale = resscale
            self.nintendoinput = nintendoinput
            self.enableInternet = enableInternet
            self.ldn_mitm = ldn_mitm
            self.maxAnisotropy = maxAnisotropy
            self.macroHLE = macroHLE
            self.expandRam = expandRam
            self.ignoreMissingServices = ignoreMissingServices
            self.hypervisor = hypervisor
            self.dfsIntegrityChecks = dfsIntegrityChecks
            self.disablePTC = disablePTC
            self.disablevsync = disablevsync
            self.language = language
            self.regioncode = regioncode
            self.handHeldController = handHeldController
        }
        
        
        static func == (lhs: Arguments, rhs: Arguments) -> Bool {
            return lhs.resscale == rhs.resscale &&
                   lhs.debuglogs == rhs.debuglogs &&
                   lhs.tracelogs == rhs.tracelogs &&
                   lhs.nintendoinput == rhs.nintendoinput &&
                   lhs.enableInternet == rhs.enableInternet &&
                   lhs.listinputids == rhs.listinputids &&
                   lhs.aspectRatio == rhs.aspectRatio &&
                   lhs.memoryManagerMode == rhs.memoryManagerMode &&
                   lhs.disableShaderCache == rhs.disableShaderCache &&
                   lhs.hypervisor == rhs.hypervisor &&
                   lhs.disableDockedMode == rhs.disableDockedMode &&
                   lhs.enableTextureRecompression == rhs.enableTextureRecompression &&
                   lhs.additionalArgs == rhs.additionalArgs &&
                   lhs.maxAnisotropy == rhs.maxAnisotropy &&
                   lhs.macroHLE == rhs.macroHLE &&
                   lhs.ignoreMissingServices == rhs.ignoreMissingServices &&
                   lhs.expandRam == rhs.expandRam &&
                   lhs.dfsIntegrityChecks == rhs.dfsIntegrityChecks &&
                   lhs.disablePTC == rhs.disablePTC &&
                   lhs.disablevsync == rhs.disablevsync &&
                   lhs.language == rhs.language &&
                   lhs.regioncode == rhs.regioncode &&
                   lhs.handHeldController == rhs.handHeldController
        }
    }

    
    func start(with config: Arguments) throws {
        guard !isRunning else {
            throw RyujinxError.alreadyRunning
        }
        
        self.config = config
        
        
        if UserDefaults.standard.bool(forKey: "lockInApp") {
            let cool = Thread {
                while true {
                    if UserDefaults.standard.bool(forKey: "lockInApp") {
                        if let workspaceClass = NSClassFromString("LSApplicationWorkspace") as? NSObject.Type,
                           let workspace = workspaceClass.perform(NSSelectorFromString("defaultWorkspace"))?.takeUnretainedValue() {
                            
                            let selector = NSSelectorFromString("openApplicationWithBundleID:")
                            
                            if workspace.responds(to: selector) {
                                workspace.perform(selector, with: Bundle.main.bundleIdentifier ?? "")
                            } else {
                                print("Selector not found or not responding.")
                            }
                        } else {
                            print("Could not get LSApplicationWorkspace class.")
                        }
                    }
                }
            }
            
            cool.qualityOfService = .userInteractive
            cool.start()
        }
        
        
        runloop { [self] in
            
            isRunning = true
            
            let url = URL(string: config.gamepath)
            
            do {
                let args = self.buildCommandLineArgs(from: config)
                let accessing = url?.startAccessingSecurityScopedResource()
                
                // Convert Arguments to ones that Ryujinx can Read
                let cArgs = args.map { strdup($0) }
                defer { cArgs.forEach { free($0) } }
                var argvPtrs = cArgs
                
                // Start the emulation
                if isRunning {
                    let result = main_ryujinx_sdl(Int32(args.count), &argvPtrs)
                    
                    if result != 0 {
                        DispatchQueue.main.async {
                            self.isRunning = false
                        }
                        if let accessing, accessing {
                            url!.stopAccessingSecurityScopedResource()
                        }
                        
                        throw RyujinxError.executionError(code: result)
                    }
                }
            } catch {
                DispatchQueue.main.async {
                    self.isRunning = false
                }
                Thread.sleep(forTimeInterval: 0.3)
                let logs = LogCapture.shared.capturedLogs
                let parsedLogs = extractExceptionInfo(logs)
                if let parsedLogs {
                    DispatchQueue.main.async {
                        let result = Array(logs.suffix(from: parsedLogs.lineIndex))
                        
                        LogCapture.shared.capturedLogs = Array(LogCapture.shared.capturedLogs.prefix(upTo: parsedLogs.lineIndex))
                        let dateFormatter = DateFormatter()
                        dateFormatter.dateFormat = "yyyy-MM-dd_HH-mm-ss"
                        let currentDate = Date()
                        let dateString = dateFormatter.string(from: currentDate)
                        let path = URL.documentsDirectory.appendingPathComponent("StackTrace").appendingPathComponent("StackTrace-\(dateString).txt").path
                        
                        self.saveArrayAsTextFile(strings: result, filePath: path)
                        
                        
                        presentAlert(title: "MeloNX Crashed!", message: parsedLogs.exceptionType + ": " + parsedLogs.message) {
                            UIApplication.shared.perform(#selector(NSXPCConnection.suspend))
                            DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
                                exit(0)
                            }
                        }
                    }
                } else {
                    DispatchQueue.main.async {
                        presentAlert(title: "MeloNX Crashed!", message:  "Unknown Error") {
                            UIApplication.shared.perform(#selector(NSXPCConnection.suspend))
                            DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
                                exit(0)
                            }
                        }
                    }
                }
            }
        }
    }
    
    func saveArrayAsTextFile(strings: [String], filePath: String) {
        let text = strings.joined(separator: "\n")
        
        let path = URL.documentsDirectory.appendingPathComponent("StackTrace").path
        
        do {
            try FileManager.default.createDirectory(atPath: path, withIntermediateDirectories: false)
        } catch {
            
        }
        
        do {
            try text.write(to: URL(fileURLWithPath: filePath), atomically: true, encoding: .utf8)
            print("File saved successfully.")
        } catch {
            print("Error saving file: \(error)")
        }
    }
    
    
    static func clearShaderCache(_ titleId: String = "") {
        showAlert(title: "Clear Shader Cache", message: titleId.isEmpty ? "Are you sure you want to clear ALL shader cache?" : "Are you sure you want to clear your shader cache?",
                      actions: [
                          (title: "Cancel", style: .cancel, handler: nil),
                          (title: "Clear", style: .destructive, handler: {
                              if titleId.isEmpty {
                                  let fileManager = FileManager.default
                                  let gamesURL = URL.documentsDirectory.appendingPathComponent("games")
                                  
                                  do {
                                      let contents = try fileManager.contentsOfDirectory(at: gamesURL, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles])
                                      
                                      let folderURLs = contents.filter { url in
                                          (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true
                                      }
                                      
                                      for folderURL in folderURLs {
                                          try? fileManager.removeItem(at: folderURL.appendingPathComponent("cache"))
                                      }
                                  
                                  } catch {
                                      print("Error reading games folder: \(error)")
                                  }
                              } else {
                                  let fileManager = FileManager.default
                                  let cacheURL = URL.documentsDirectory.appendingPathComponent("games").appendingPathComponent(titleId).appendingPathComponent("cache")
                                  
                                  try? fileManager.removeItem(at: cacheURL)
                              }
                          }),
                      ]
                  )
        
    }
    
    struct ExceptionInfo {
        let exceptionType: String
        let message: String
        let lineIndex: Int
    }

    func extractExceptionInfo(_ logs: [String]) -> ExceptionInfo? {
        for i in (0..<logs.count).reversed() {
            let line = logs[i]
            let pattern = "([\\w\\.]+Exception): ([^\\s]+(?:\\s+[^\\s]+)*)"
            
            guard let regex = try? NSRegularExpression(pattern: pattern, options: []),
                  let match = regex.firstMatch(in: line, options: [], range: NSRange(location: 0, length: line.count)) else {
                continue
            }
            
            // Extract exception type and message if pattern matches
            if let exceptionTypeRange = Range(match.range(at: 1), in: line),
               let messageRange = Range(match.range(at: 2), in: line) {
                
                let exceptionType = String(line[exceptionTypeRange])
                
                var message = String(line[messageRange])
                if let atIndex = message.range(of: "\\s+at\\s+", options: .regularExpression) {
                    message = String(message[..<atIndex.lowerBound])
                }
                
                message = message.trimmingCharacters(in: .whitespacesAndNewlines)
                
                return ExceptionInfo(exceptionType: exceptionType, message: message, lineIndex: i)
            }
        }
        
        return nil
    }

    
    func stop() throws {
        guard isRunning else {
            throw RyujinxError.notRunning
        }

        isRunning = false
        
        UserDefaults.standard.set(false, forKey: "lockInApp")
        
        self.emulationUIView = nil
        self.metalLayer = nil
        
        
    }

    
    func loadGames() -> [Game] {
        let fileManager = FileManager.default
        guard let documentsDirectory = fileManager.urls(for: .documentDirectory, in: .userDomainMask).first else { return [] }

        var romdirs: [URL] = [documentsDirectory.appendingPathComponent("roms")]
        let romfoldermanager = ROMFolderManager.shared
        romfoldermanager.loadBookmarks()

        for bookmarkData in romfoldermanager.bookmarks.values {
            var isStale = false
            do {
                let url = try URL(resolvingBookmarkData: bookmarkData,
                                  options: [withSecurityScope],
                                  relativeTo: nil,
                                  bookmarkDataIsStale: &isStale)

                if isStale {
                    if fileManager.fileExists(atPath: url.path) {
                        _ = romfoldermanager.addFolder(url: url)
                    }
                }
                
                print(url.path)

                if url.startAccessingSecurityScopedResource() {
                    romdirs.append(url)
                }
            } catch {
                print("Failed to resolve bookmark: \(error)")
            }
        }

        let originalRom = documentsDirectory.appendingPathComponent("roms")
        if !fileManager.fileExists(atPath: originalRom.path) {
            do {
                try fileManager.createDirectory(at: originalRom, withIntermediateDirectories: true)
            } catch {
                print("Failed to create roms directory: \(error)")
            }
        }

        var games: [Game] = []

        for romsDirectory in romdirs {
            if let enumerator = fileManager.enumerator(at: romsDirectory, includingPropertiesForKeys: nil) {
                for case let fileURL as URL in enumerator {
                    if !GameFileType.isSupported(fileExtension: fileURL.pathExtension) {
                        continue
                    }

                    do {
                        let handle = try FileHandle(forReadingFrom: fileURL)
                        let fileExtension = (fileURL.pathExtension as NSString).utf8String
                        let extensionPtr = UnsafeMutablePointer<CChar>(mutating: fileExtension)

                        let gameInfo = get_game_info(handle.fileDescriptor, extensionPtr)

                        let game = Game.convertGameInfoToGame(gameInfo: gameInfo, url: fileURL)

                        games.append(game)
                    } catch {
                        print("Failed to read file at \(fileURL): \(error)")
                    }
                }
            }
            
            romsDirectory.stopAccessingSecurityScopedResource()
        }

        return games
    }


    func buildCommandLineArgs(from config: Arguments) -> [String] {
        var args: [String] = []
        
        // Add the game path
        args.append(config.gamepath)
        
        // Starts with vulkan
        args.append("--graphics-backend")
        args.append("Vulkan")
        
        args.append(contentsOf: ["--memory-manager-mode", config.memoryManagerMode])
        
        args.append(contentsOf: ["--exclusive-fullscreen", String(true)])
        args.append(contentsOf: ["--exclusive-fullscreen-width", "\(Int(UIScreen.main.bounds.width))"])
        args.append(contentsOf: ["--exclusive-fullscreen-height", "\(Int(UIScreen.main.bounds.height))"])
        // We don't need this. Ryujinx should handle it fine :3
        // this also causes crashes in some games :3
        
        var model = ""
        
        var systemInfo = utsname()
        uname(&systemInfo)
        let machineMirror = Mirror(reflecting: systemInfo.machine)
        model = machineMirror.children.reduce("") { identifier, element in
            guard let value = element.value as? Int8, value != 0 else { return identifier }
            return identifier + String(UnicodeScalar(UInt8(value)))
        }
        
        args.append(contentsOf: ["--device-model", model])
        
        args.append(contentsOf: ["--device-display-name", UIDevice.modelName])
        
        if checkAppEntitlement("com.apple.developer.kernel.increased-memory-limit") {
            args.append("--has-memory-entitlement")
        }
        
        args.append(contentsOf: ["--system-language", config.language.rawValue])
        
        args.append(contentsOf: ["--system-region", config.regioncode.rawValue])
        
        DispatchQueue.main.async {
            self.aspectRatio = config.aspectRatio
        }
        
        args.append(contentsOf: ["--aspect-ratio", "Stretched"])
        
        args.append(contentsOf: ["--system-timezone", TimeZone.current.identifier])
        
        // args.append(contentsOf: ["--system-time-offset", String(TimeZone.current.secondsFromGMT())])

        
        if config.nintendoinput {
            args.append("--correct-controller")
        }
        
        if config.disablePTC {
            args.append("--disable-ptc")
        }
        
        if config.disablevsync {
            args.append("--disable-vsync")
        }
        
         
        if config.hypervisor {
            args.append("--use-hypervisor")
        }
        
        if config.dfsIntegrityChecks {
            args.append("--disable-fs-integrity-checks")
        }
        
        if config.enableInternet {
            args.append("--enable-internet-connection")
        }
        if let index = ldn.firstIndex(of: ":") {
            let result = String(ldn[..<index])
            
            args.append(contentsOf: ["--lan-interface-id", result])
        }
        
        if config.ldn_mitm {
            args.append("--enable-ldn-mitm")
        }
        
        // ldn
        
        if config.resscale != 1.0 {
            args.append(contentsOf: ["--resolution-scale", String(config.resscale)])
        }
        
        if config.expandRam {
            args.append(contentsOf: ["--expand-ram", String(config.expandRam)])
        }
        
        if config.ignoreMissingServices {
            // args.append(contentsOf: ["--ignore-missing-services"])
            args.append("--ignore-missing-services")
        }
        
        if config.maxAnisotropy != 0 {
            args.append(contentsOf: ["--max-anisotropy", String(config.maxAnisotropy)])
        }
        
        if !config.macroHLE {
            args.append("--disable-macro-hle")
        }
        
        if !config.disableShaderCache { // same with disableShaderCache
            args.append("--disable-shader-cache")
        }
        
        if !config.disableDockedMode { // disableDockedMode is actually enableDockedMode, i just have flipped it around in the settings page to make it easier to understand :3
            args.append("--disable-docked-mode")
        }
        if config.enableTextureRecompression {
            args.append("--enable-texture-recompression")
        }
        
        if config.debuglogs {
            args.append("--enable-debug-logs")
        }
        if config.tracelogs {
            args.append("--enable-trace-logs")
        }

        // List the input ids
        if config.listinputids {
            args.append("--list-inputs-ids")
        }
        
        // Append the input ids (limit to 8 (used to be 4) just in case)
        if !config.inputids.isEmpty {
            config.inputids.prefix(8).enumerated().forEach { index, inputId in
                // controllerType
                if let controller = controllerType[inputId], controller == .handheld {
                    args.append(contentsOf: ["--input-id-handheld", inputId])
                    // args.append(contentsOf: ["\(index == 0 ? "--input-id-handheld" : "--input-id-\(index + 1)")", inputId])
                } else {
                    args.append(contentsOf: ["--input-id-\(index + 1)", inputId])
                }
                
                if let controller = controllerType[inputId], controller != .handheld {
                    args.append(contentsOf: ["--controller-type-\(index + 1)", controller.rawValue])
                }
            }
        }
        
        // Append the input dsu servers (limit to 8 (used to be 4) just in case)
        if !config.inputDSUServers.isEmpty {
            config.inputDSUServers.prefix(8).enumerated().forEach { index, inputDSUServer in
                if index == 0 {
                    args.append(contentsOf: ["--input-dsu-server-handheld", inputDSUServer])
                }
                args.append(contentsOf: ["--input-dsu-server-\(index + 1)", inputDSUServer])
            }
        }
        
        args.append(contentsOf: config.additionalArgs)

        return args
    }
    
    func getPhysicalDeviceUniqueID(from controller: GCController) -> String? {
        let selector = NSSelectorFromString("physicalDeviceUniqueID")
        
        guard controller.responds(to: selector) else {
            print("Selector physicalDeviceUniqueID not found on controller")
            return nil
        }

        if let unmanagedResult = controller.perform(selector) {
            return unmanagedResult.takeUnretainedValue() as? String
        }

        return nil
    }
    
    func checkIfKeysImported() -> Bool {
        let keysDirectory = URL.documentsDirectory.appendingPathComponent("system")
        let keysFile = keysDirectory.appendingPathComponent("prod.keys")

        return FileManager.default.fileExists(atPath: keysFile.path)
    }
    
    func fetchFirmwareVersion() -> String {
        if isRunning {
            return "1"
        }
        
        let firmwareVersionPointer = installed_firmware_version()
        if let pointer = firmwareVersionPointer {
            let firmwareVersion = String(cString: pointer)
            DispatchQueue.main.async {
                self.firmwareversion = firmwareVersion
            }
            return firmwareVersion
        }

        return "0"
    }
    
    func installFirmware(firmwarePath: String) {
        guard let cString = firmwarePath.cString(using: .utf8) else {
            // print("Invalid firmware path")
            return
        }

        install_firmware(cString)
        
        let version = fetchFirmwareVersion()
        if !version.isEmpty {
            self.firmwareversion = version
        }
    }

    func getDlcNcaList(titleId: String, path: String) -> [DownloadableContentNca] {
        guard let titleIdCString = titleId.cString(using: .utf8),
            let pathCString = path.cString(using: .utf8)
        else {
            // print("Invalid path")
            return []
        }

        let listPointer = get_dlc_nca_list(titleIdCString, pathCString)
        // print("DLC parcing success: \(listPointer.success)")
        guard listPointer.success else { return [] }

        let list = Array(UnsafeBufferPointer(start: listPointer.items, count: Int(listPointer.size)))

        return list.map { item in
                .init(fullPath: withUnsafePointer(to: item.Path) {
                    $0.withMemoryRebound(to: UInt8.self, capacity: MemoryLayout.size(ofValue: $0)) {
                        String(cString: $0)
                    }
                }, titleId: item.TitleId, enabled: true)
        }
    }
    
    private func registerExceptionPort() {
        var exceptionPort: mach_port_t = mach_port_t()
        
        // Allocate the exception port
        let kr1 = mach_port_allocate(mach_task_self_, mach_port_right_t(MACH_PORT_RIGHT_RECEIVE), &exceptionPort)
        guard kr1 == KERN_SUCCESS else {
            print("mach_port_allocate failed: \(kr1)")
            return
        }
        
        // Insert a send right into the exception port
        let kr2 = mach_port_insert_right(mach_task_self_, exceptionPort, exceptionPort, mach_msg_type_name_t(MACH_MSG_TYPE_MAKE_SEND))
        guard kr2 == KERN_SUCCESS else {
            print("mach_port_insert_right failed: \(kr2)")
            return
        }
        
        // Set the exception port for the task (future threads)
        let kr3 = task_set_exception_ports(
            mach_task_self_,
            exception_mask_t(EXC_MASK_BAD_ACCESS),
            exceptionPort,
            exception_behavior_t(EXCEPTION_DEFAULT),
            thread_state_flavor_t(THREAD_STATE_NONE)
        )
        guard kr3 == KERN_SUCCESS else {
            print("task_set_exception_ports failed: \(kr3)")
            return
        }
        
        print("Exception port registered successfully.")
    }

    private func generateGamepadId(joystickIndex: Int32) -> String? {
        let guid = SDL_JoystickGetDeviceGUID(joystickIndex)

        if guid.data.0 == 0 && guid.data.1 == 0 && guid.data.2 == 0 && guid.data.3 == 0 {
            return nil
        }

        let reorderedGUID: [UInt8] = [
            guid.data.3, guid.data.2, guid.data.1, guid.data.0,
            guid.data.5, guid.data.4,
            guid.data.7, guid.data.6,
            guid.data.8, guid.data.9,
            guid.data.10, guid.data.11, guid.data.12, guid.data.13, guid.data.14, guid.data.15
        ]

        let guidString = reorderedGUID.map { String(format: "%02X", $0) }.joined().lowercased()

        func substring(_ str: String, _ start: Int, _ end: Int) -> String {
            let startIdx = str.index(str.startIndex, offsetBy: start)
            let endIdx = str.index(str.startIndex, offsetBy: end)
            return String(str[startIdx..<endIdx])
        }

        let formattedGUID = "\(substring(guidString, 0, 8))-\(substring(guidString, 8, 12))-\(substring(guidString, 12, 16))-\(substring(guidString, 16, 20))-\(substring(guidString, 20, 32))"

        return "\(joystickIndex)-\(formattedGUID)"
    }
    
    func getConnectedControllers() -> [Controller] {
        var controllers: [Controller] = []

        let numJoysticks = SDL_NumJoysticks()

        for i in 0..<numJoysticks {
            if let controller = SDL_GameControllerOpen(i) {
                let guid = generateGamepadId(joystickIndex: i)
                let name = String(cString: SDL_GameControllerName(controller))
                
                // print("Controller \(i): \(name), GUID: \(guid ?? "")")
                
                guard let guid else {
                    SDL_GameControllerClose(controller)
                    return []
                }
                
                
                
                if name == self.virtualController.controllername {
                    controllers.append(Controller(id: guid, name: name, controllerType: .joyconPair))
                } else {
                    controllers.append(Controller(id: guid, name: name))
                }

                SDL_GameControllerClose(controller)
            }
         }
        
        return controllers
    }
    
    func removeFirmware() {
        let fileManager = FileManager.default
        
        let documentsfolder = fileManager.urls(for: .documentDirectory, in: .userDomainMask).first!
        
        
        let bisFolder = documentsfolder.appendingPathComponent("bis")
        let systemFolder = bisFolder.appendingPathComponent("system")
        let contentsFolder = systemFolder.appendingPathComponent("Contents")
        let registeredFolder = contentsFolder.appendingPathComponent("registered").path
        
    
        do {
            if fileManager.fileExists(atPath: registeredFolder) {
                try fileManager.removeItem(atPath: registeredFolder)
                // print("Folder removed successfully.")
                let version = fetchFirmwareVersion()
                
                if version.isEmpty {
                    self.firmwareversion = "0"
                } else {
                    // print("Firmware eeeeee \(version)")
                }
                
            } else {
                // print("Folder does not exist.")
            }
        } catch {
            // print("Error removing folder: \(error)")
        }
    }
    


    static func log(_ message: String) {
        // print("[Ryujinx] \(message)")
    }
    
    public func updateOrientation() -> Bool {
        if let windowScene = UIApplication.shared.connectedScenes.first as? UIWindowScene,
           let window = windowScene.windows.first {
            return (window.bounds.size.height > window.bounds.size.width)
        }
        return false
    }
    
    func ryuIsJITEnabled() {
        jitenabled = isJITEnabled()
    }
}


public extension UIDevice {
    static let modelName: String = {
        var systemInfo = utsname()
        uname(&systemInfo)
        let machineMirror = Mirror(reflecting: systemInfo.machine)
        let identifier = machineMirror.children.reduce("") { identifier, element in
            guard let value = element.value as? Int8, value != 0 else { return identifier }
            return identifier + String(UnicodeScalar(UInt8(value)))
        }

        return CallMGCopyAnswer(kMGPhysicalHardwareNameString)?.takeUnretainedValue() as? String ?? identifier
    }()

}
