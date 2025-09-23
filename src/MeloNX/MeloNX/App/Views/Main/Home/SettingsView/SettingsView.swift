//
//  SettingsView.swift
//  MeloNX
//
//  Created by Stossy11 on 25/11/2024.
//

import SwiftUI
import SwiftSVG
import UIKit


class SplitViewController: UISplitViewController {
    private let sidebarViewController: UIViewController
    private let contentViewController: UIViewController
    
    init(sidebarViewController: UIViewController, contentViewController: UIViewController) {
        self.sidebarViewController = sidebarViewController
        self.contentViewController = contentViewController
        super.init(style: .doubleColumn)
        
        self.preferredDisplayMode = .oneBesideSecondary
        self.preferredSplitBehavior = .tile
        self.presentsWithGesture = true
        
        self.setViewController(sidebarViewController, for: .primary)
        self.setViewController(contentViewController, for: .secondary)
    }
    
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }
    
    override func viewDidLoad() {
        super.viewDidLoad()
        
        self.primaryBackgroundStyle = .sidebar
        
        let displayModeButtonItem = self.displayModeButtonItem
        contentViewController.navigationItem.leftBarButtonItem = displayModeButtonItem
    }
    
    func showSidebar() {
        self.preferredDisplayMode = .oneBesideSecondary
    }
    
    func hideSidebar() {
        self.preferredDisplayMode = .secondaryOnly
    }
    
    func toggleSidebar() {
        if self.displayMode == .oneBesideSecondary {
            self.preferredDisplayMode = .secondaryOnly
        } else {
            self.preferredDisplayMode = .oneBesideSecondary
        }
    }
}

struct SidebarView<Content: View>: View {
    var sidebar: () -> AnyView
    var content: () -> Content
    @Binding var showSidebar: Bool
    
    init(sidebar: @escaping () -> AnyView, content: @escaping () -> Content, showSidebar: Binding<Bool>) {
        self.sidebar = sidebar
        self.content = content
        self._showSidebar = showSidebar
    }
    
    var body: some View {
        SidebarViewRepresentable(
            sidebar: sidebar(),
            content: content(),
            showSidebar: $showSidebar
        )
    }
}

struct SidebarViewRepresentable<Sidebar: View, Content: View>: UIViewControllerRepresentable {
    var sidebar: Sidebar
    var content: Content
    @Binding var showSidebar: Bool
    
    func makeUIViewController(context: Context) -> SplitViewController {
        let sidebarVC = UIHostingController(rootView: sidebar)
        let contentVC = UINavigationController(rootViewController: UIHostingController(rootView: content))
        
        let splitVC = SplitViewController(sidebarViewController: sidebarVC, contentViewController: contentVC)
        splitVC.setOverrideTraitCollection(
            UITraitCollection(horizontalSizeClass: .regular),
            forChild: splitVC
        )
        return splitVC
    }
    
    func updateUIViewController(_ uiViewController: SplitViewController, context: Context) {
        if let sidebarVC = uiViewController.viewController(for: .primary) as? UIHostingController<Sidebar> {
            sidebarVC.rootView = sidebar
        }
        if let navController = uiViewController.viewController(for: .secondary) as? UINavigationController,
           let contentVC = navController.topViewController as? UIHostingController<Content> {
            contentVC.rootView = content
        }
        
        if showSidebar {
            uiViewController.showSidebar()
        } else {
            uiViewController.hideSidebar()
        }
    }
    
    static func dismantleUIViewController(_ uiViewController: SplitViewController, coordinator: Coordinator) {
    }
}

class SettingsManager: ObservableObject {
    @Published var config: Ryujinx.Arguments {
        didSet {
            debouncedSave()
        }
    }
    
    private var saveWorkItem: DispatchWorkItem?;
    
    public static var shared = SettingsManager()
    
    private init() {
        self.config = SettingsManager.loadSettings() ?? Ryujinx.Arguments()
    }
    
    func debouncedSave() {
        saveWorkItem?.cancel()
        
        let workItem = DispatchWorkItem { [weak self] in
            guard let self = self else { return }
            self.saveSettings()
        }
        
        saveWorkItem = workItem
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3, execute: workItem)
    }
    
    func saveSettings() {
        do {
            let encoder = JSONEncoder()
            encoder.outputFormatting = .prettyPrinted
            let data = try encoder.encode(config)
            
            let fileURL = URL.documentsDirectory.appendingPathComponent("config.json")
            
            try data.write(to: fileURL)
            print("Settings saved successfully")
        } catch {
            print("Failed to save settings: \(error)")
        }
    }
    
    static func loadSettings() -> Ryujinx.Arguments? {
        do {
            let fileURL = URL.documentsDirectory.appendingPathComponent("config.json")
            
            guard FileManager.default.fileExists(atPath: fileURL.path) else {
                print("Config file does not exist, creating new config")
                return nil
            }
            
            let data = try Data(contentsOf: fileURL)
            
            let decoder = JSONDecoder()
            let configs = try decoder.decode(Ryujinx.Arguments.self, from: data)
            return configs
        } catch {
            print("Failed to load settings: \(error)")
            return nil
        }
    }
    
    func loadSettings() {
        do {
            let fileURL = URL.documentsDirectory.appendingPathComponent("config.json")
            
            guard FileManager.default.fileExists(atPath: fileURL.path) else {
                print("Config file does not exist, creating new config")
                saveSettings()
                return
            }
            
            let data = try Data(contentsOf: fileURL)
            
            let decoder = JSONDecoder()
            let configs = try decoder.decode(Ryujinx.Arguments.self, from: data)
            
            self.config = configs
        } catch {
            print("Failed to load settings: \(error)")
        }
    }
}



struct SettingsViewNew: View {
    @StateObject private var settingsManager = SettingsManager.shared
    
    private var config: Binding<Ryujinx.Arguments> {
        $settingsManager.config
    }
    
    @Binding var MoltenVKSettings: [MoltenVKSettings]
    
    @ObservedObject var controllerManager = ControllerManager.shared
    
    @AppStorage("useTrollStore") var useTrollStore: Bool = false
    
    @AppStorage("jitStreamerEB") var jitStreamerEB: Bool = false
    @AppStorage("stikJIT") var stikJIT: Bool = false
    
    @AppStorage("ignoreJIT") var ignoreJIT: Bool = false
    
    var memoryManagerModes = [
        ("HostMapped", "Host (fast)"),
        ("HostMappedUnsafe", "Host Unchecked (fast, unstable / unsafe)"),
        ("SoftwarePageTable", "Software (slow)"),
    ]
    
    @AppStorage("RyuDemoControls") var ryuDemo: Bool = false

    @AppStorage("showScreenShotButton") var ssb: Bool = false
    
    @AppStorage("MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS") var mVKPreFillBuffer: Bool = false
    @AppStorage("MVK_CONFIG_SYNCHRONOUS_QUEUE_SUBMITS") var syncqsubmits: Bool = false
    @AppStorage("DUAL_MAPPED_JIT") var dualMapped: Bool = false
    
    @AppStorage("performacehud") var performacehud: Bool = false
    
    @AppStorage("swapBandA") var swapBandA: Bool = false
    
    @AppStorage("oldWindowCode") var windowCode: Bool = false
    
    @AppStorage("On-ScreenControllerScale") var controllerScale: Double = 1.0
    
    @AppStorage("On-ScreenControllerOpacity") var controllerOpacity: Double = 1.0
    
    @AppStorage("hasbeenfinished") var finishedStorage: Bool = false
    
    @AppStorage("showlogsloading") var showlogsloading: Bool = true
    
    @AppStorage("showlogsgame") var showlogsgame: Bool = false
    
    @AppStorage("toggleGreen") var toggleGreen: Bool = false
    
    @AppStorage("stick-button") var stickButton = false
    @AppStorage("waitForVPN") var waitForVPN = false
    
    @AppStorage("HideButtons") var hideButtonsJoy = false
    
    @AppStorage("checkForUpdate") var checkForUpdate: Bool = true

    @AppStorage("disableTouch") var disableTouch = false
    
    @AppStorage("location-enabled") var locationenabled: Bool = false
    
    @AppStorage("runOnMainThread") var runOnMainThread = false
    
    @AppStorage("oldSettingsUI") var oldSettingsUI = false
    
    @AppStorage("showBatteryPercentage") var showBatteryPercentage: Bool = false
    
    let totalMemory = ProcessInfo.processInfo.physicalMemory
    
    @AppStorage("lockInApp") var restartApp = false
    @AppStorage("showProfileonGame") var showProfileonGame: Bool = false
    
    @AppStorage("OldView") var oldView = true
    @AppStorage("LDN_MITM") var ldn = printAllIPv4Addresses().first ?? "Unknown"
    
    @State private var showResolutionInfo = false
    @State private var showAnisotropicInfo = false
    @State private var showControllerInfo = false
    @State private var showAppIconSwitcher = false
    @State private var searchText = ""
    @AppStorage("portal") var gamepo = false
    @StateObject var ryujinx = Ryujinx.shared
    @Environment(\.colorScheme) var colorScheme
    @Environment(\.verticalSizeClass) var verticalSizeClass: UserInterfaceSizeClass?
    @Environment(\.horizontalSizeClass) var horizontalSizeClass: UserInterfaceSizeClass?
    
    @State private var selectedCategory: SettingsCategory = .graphics
    
    @StateObject var metalHudEnabler = MTLHud.shared
    
    var filteredMemoryModes: [(String, String)] {
        guard !searchText.isEmpty else { return memoryManagerModes }
        return memoryManagerModes.filter { $0.1.localizedCaseInsensitiveContains(searchText) }
    }
    
    var appVersion: String {
        guard let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String else {
            return "Unknown"
        }
        return version
    }
    
    @FocusState private var isArgumentsKeyboardVisible: Bool
    
    
    @State private var selectedView = "Data Management"
    @State private var sidebar = true
    
    enum SettingsCategory: String, CaseIterable, Identifiable {
        case graphics = "Graphics"
        case input = "Input"
        case misc = "Misc"
        case system = "System"
        case advanced = "Advanced"
        
        var id: String { self.rawValue }
        
        var icon: String {
            switch self {
            case .graphics: return "paintbrush.fill"
            case .input: return "gamecontroller.fill"
            case .system: return "gearshape.fill"
            case .misc: return "ellipsis.circle.fill"
            case .advanced: return "terminal.fill"
            }
        }
    }
    
    var body: some View {
        if UIDevice.current.userInterfaceIdiom == .phone {
            iOSSettings
        } else if !oldSettingsUI {
            iPadOSSettings
                .ignoresSafeArea()
                .edgesIgnoringSafeArea(.all)
        } else {
            iOSSettings
        }
    }
    
    var iPadOSSettings: some View {
        VStack {
            SidebarView(
                sidebar: {
                    AnyView(
                        ScrollView(.vertical) {
                            VStack {
                                VStack(spacing: 16) {
                                    HStack {
                                        Circle()
                                            .fill(ryujinx.jitenabled ? Color.green : Color.red)
                                            .frame(width: 12, height: 12)
                                        
                                        Text(ryujinx.jitenabled ? "JIT Enabled" : "JIT Not Acquired")
                                            .font(.subheadline.weight(.medium))
                                            .foregroundColor(ryujinx.jitenabled ? .green : .red)
                                        
                                        Spacer()
                                        
                                        let memoryText = ProcessInfo.processInfo.isiOSAppOnMac
                                            ? String(format: "%.0f GB", Double(totalMemory) / (1024 * 1024 * 1024))
                                            : String(format: "%.0f GB", Double(totalMemory) / 1_000_000_000)
                                        
                                        Text("\(memoryText) RAM")
                                            .font(.subheadline.weight(.medium))
                                            .foregroundColor(.secondary)
                                        
                                    }
                                    
                                    InfoCard(
                                        title: "Device",
                                        value: UIDevice.modelName,
                                        icon: deviceIcon,
                                        color: .blue
                                    )
                                    
                                    let versionPart = ProcessInfo.processInfo.operatingSystemVersionString.replacingOccurrences(of: "Version ", with: "")

                                    let parts = versionPart.components(separatedBy: " (Build ")
                                    if parts.count == 2 {
                                        let version = parts[0]
                                        let build = parts[1].replacingOccurrences(of: ")", with: "")
                                        InfoCard(
                                            title: "System",
                                            value: "\(ProcessInfo.processInfo.isiOSAppOnMac ? "macOS" : UIDevice.current.systemName) \(version) (\(build))",
                                            icon: "applelogo",
                                            color: .gray
                                        )
                                    } else {
                                        InfoCard(
                                            title: "System",
                                            value: "\(ProcessInfo.processInfo.isiOSAppOnMac ? "macOS" : UIDevice.current.systemName) \(UIDevice.current.systemVersion)",
                                            icon: "applelogo",
                                            color: .gray
                                        )
                                    }
                                    
                                    InfoCard(
                                        title: "Increased Memory Limit",
                                        value: checkAppEntitlement("com.apple.developer.kernel.increased-memory-limit") ? "Enabled" : "Disabled",
                                        icon: "memorychip.fill",
                                        color: .orange
                                    )
                                }
                                .padding()
                                
                                Divider()
                                
                                ForEach(SettingsCategory.allCases, id: \.id) { key in
                                    HStack {
                                        Rectangle()
                                            .frame(width: 2.5, height: 35)
                                            .foregroundStyle(selectedCategory == key ? Color.accentColor : Color.clear)
                                        Text(key.rawValue) // Fix here
                                        Spacer()
                                    }
                                    .foregroundStyle(selectedCategory == key ? Color.accentColor : Color.primary)
                                    .padding(5)
                                    .background(
                                        Color(uiColor: .secondarySystemBackground).opacity(selectedCategory == key ? 1 : 0)
                                    )
                                    .background(
                                        Rectangle()
                                            .stroke(selectedCategory == key ? .teal : .clear, lineWidth: 2.5)
                                    )
                                    .contentShape(Rectangle())
                                    .onTapGesture {
                                        withAnimation(.smooth) {
                                            selectedCategory = key // Uncommented and fixed
                                        }
                                    }
                                }
                            }
                            .padding()
                        }
                    )
                },
                content: {
                    ScrollView {
                        switch selectedCategory {
                        case .graphics:
                            graphicsSettings
                        case .misc:
                            miscSettings
                        case .input:
                            inputSettings
                        case .system:
                            systemSettings
                        case .advanced:
                            advancedSettings
                        }
                    }
                },
                showSidebar: $sidebar
            )
            .onAppear {
                mVKPreFillBuffer = false
                
                
                if let configs = SettingsManager.loadSettings() {
                    settingsManager.loadSettings()
                } else {
                    settingsManager.saveSettings()
                }
            }
        }
    }
    
    
    var iOSSettings: some View {
        iOSNav {
            ZStack {
                // Background color
                Color(UIColor.systemBackground)
                    .ignoresSafeArea()
                
                VStack(spacing: 0) {
                    // Category selector
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: 12) {
                            ForEach(SettingsCategory.allCases, id: \.id) { category in
                                CategoryButton(
                                    title: category.rawValue,
                                    icon: category.icon,
                                    isSelected: selectedCategory == category
                                ) {
                                    selectedCategory = category
                                }
                            }
                        }
                        .padding(.horizontal)
                        .padding(.vertical, 8)
                    }
                    
                    Divider()
                    
                    // Settings content
                    ScrollView {
                        VStack(spacing: 24) {
                            deviceInfoCard
                                .padding(.horizontal)
                                .padding(.top)
                            
                            switch selectedCategory {
                            case .graphics:
                                graphicsSettings
                            case .input:
                                inputSettings
                            case .system:
                                systemSettings
                            case .advanced:
                                advancedSettings
                            case .misc:
                                miscSettings
                            }
                            
                            Spacer(minLength: 50)
                        }
                        .padding(.bottom)
                    }
                    .scrollDismissesKeyboardIfAvailable()
                }
            }
            .navigationTitle("Settings")
            .navigationBarTitleDisplayMode(.large)
            // .searchable(text: $searchText, placement: .navigationBarDrawer(displayMode: .automatic))
            .onAppear {
                mVKPreFillBuffer = false
                
                if let configs = SettingsManager.loadSettings() {
                    settingsManager.loadSettings()
                } else {
                    settingsManager.saveSettings()
                }
            }
        }
    }
    
    // MARK: - Device Info Card
    
    private var deviceInfoCard: some View {
        VStack(spacing: 16) {
            // JIT Status indicator
            HStack {
                Circle()
                    .fill(ryujinx.jitenabled ? Color.green : Color.red)
                    .frame(width: 12, height: 12)
                
                Text(ryujinx.jitenabled ? "JIT Enabled" : "JIT Not Acquired")
                    .font(.subheadline.weight(.medium))
                    .foregroundColor(ryujinx.jitenabled ? .green : .red)
                
                Spacer()
                
                let memoryText = ProcessInfo.processInfo.isiOSAppOnMac
                    ? String(format: "%.0f GB", Double(totalMemory) / (1024 * 1024 * 1024))
                    : String(format: "%.0f GB", Double(totalMemory) / 1_000_000_000)
                
                Text("\(memoryText) RAM")
                    .font(.subheadline)
                    .foregroundColor(.secondary)
                
                Text("·")
                    .font(.subheadline)
                    .foregroundColor(.secondary)
                
                if ProcessInfo.processInfo.isiOSAppOnMac {
                    Text("macOS \(ProcessInfo.processInfo.operatingSystemVersionString)")
                        .font(.subheadline.weight(.medium))
                        .foregroundColor(.secondary)
                    
                    Text("·")
                        .font(.subheadline)
                        .foregroundColor(.secondary)
                }
                
                Text("Version \(appVersion)")
                    .font(.subheadline)
                    .foregroundColor(.secondary)
            }
            
            // Device cards
            if (horizontalSizeClass == .regular && verticalSizeClass == .regular) || (horizontalSizeClass == .regular && verticalSizeClass == .compact) {
                HStack(spacing: 16) {
                    InfoCard(
                        title: "Device",
                        value: UIDevice.modelName,
                        icon: deviceIcon,
                        color: .blue
                    )
                    
                    InfoCard(
                        title: "System",
                        value: "\(UIDevice.current.systemName) \(UIDevice.current.systemVersion)",
                        icon: "applelogo",
                        color: .gray
                    )
                    
                    InfoCard(
                        title: "Increased Memory Limit",
                        value: checkAppEntitlement("com.apple.developer.kernel.increased-memory-limit") ? "Enabled" : "Disabled",
                        icon: "memorychip.fill",
                        color: .orange
                    )
                }
            } else {
                VStack(spacing: 16) {
                    InfoCard(
                        title: "Device",
                        value: UIDevice.modelName,
                        icon: deviceIcon,
                        color: .blue
                    )
                    
                    InfoCard(
                        title: "System",
                        value: "\(UIDevice.current.systemName) \(UIDevice.current.systemVersion)",
                        icon: "applelogo",
                        color: .gray
                    )
                    
                    InfoCard(
                        title: "Increased Memory Limit",
                        value: checkAppEntitlement("com.apple.developer.kernel.increased-memory-limit") ? "Enabled" : "Disabled",
                        icon: "memorychip.fill",
                        color: .orange
                    )
                }
            }
        }
        .padding()
        .background(
            RoundedRectangle(cornerRadius: 16)
                .fill(colorScheme == .dark ? Color(.systemGray6) : Color.white)
                .shadow(color: Color.black.opacity(0.05), radius: 5, x: 0, y: 2)
        )
        .onAppear {
            ryujinx.ryuIsJITEnabled()
        }
    }
    
    private var deviceIcon: String {
        let model = UIDevice.modelName
        if model.contains("iPad") {
            return "ipad"
        } else if model.contains("iPhone") {
            return "iphone"
        } else {
            return "desktopcomputer"
        }
    }
    
    // MARK: - Graphics Settings
    
    private var graphicsSettings: some View {
        SettingsSection(title: "Graphics & Performance") {
            // Resolution scale card
            SettingsCard {
                VStack(alignment: .leading, spacing: 12) {
                    HStack {
                        labelWithIcon("Resolution Scale", iconName: "magnifyingglass")
                            .font(.headline)
                        Spacer()
                        Button {
                            showResolutionInfo.toggle()
                        } label: {
                            Image(systemName: "info.circle")
                                .foregroundColor(.secondary)
                        }
                        .buttonStyle(.plain)
                        .alert(isPresented: $showResolutionInfo) {
                            Alert(
                                title: Text("Resolution Scale"),
                                message: Text("Adjust the internal rendering resolution. Higher values improve visuals but may reduce performance."),
                                dismissButton: .default(Text("OK"))
                            )
                        }
                    }
                    
                    VStack(spacing: 8) {
                        Slider(value: config.resscale, in: 0.1...3.0, step: 0.05)
                        
                        HStack {
                            Text("0.1x")
                                .font(.caption2)
                                .foregroundColor(.secondary)
                            
                            Spacer()
                            
                            Text("\(settingsManager.config.resscale, specifier: "%.2f")x")
                                .font(.headline)
                                .foregroundColor(.blue)
                            
                            Spacer()
                            
                            Text("3.0x")
                                .font(.caption2)
                                .foregroundColor(.secondary)
                        }
                    }
                }
            }
            
            // Anisotropic filtering card
            SettingsCard {
                VStack(alignment: .leading, spacing: 12) {
                    HStack {
                        labelWithIcon("Max Anisotropic Filtering", iconName: "magnifyingglass")
                            .font(.headline)
                        Spacer()
                        Button {
                            showAnisotropicInfo.toggle()
                        } label: {
                            Image(systemName: "info.circle")
                                .foregroundColor(.secondary)
                        }
                        .buttonStyle(.plain)
                        .alert(isPresented: $showAnisotropicInfo) {
                            Alert(
                                title: Text("Max Anisotropic Filtering"),
                                message: Text("Adjust the internal Anisotropic filtering. Higher values improve texture quality at angles but may reduce performance. Default at 0 lets game decide."),
                                dismissButton: .default(Text("OK"))
                            )
                        }
                    }
                    
                    VStack(spacing: 8) {
                        Slider(value: config.maxAnisotropy, in: 0...16.0, step: 0.1)
                        
                        HStack {
                            Text("Off")
                                .font(.caption2)
                                .foregroundColor(.secondary)
                            
                            Spacer()
                            
                            Text("\(settingsManager.config.maxAnisotropy, specifier: "%.1f")x")
                                .font(.headline)
                                .foregroundColor(.blue)
                            
                            Spacer()
                            
                            Text("16x")
                                .font(.caption2)
                                .foregroundColor(.secondary)
                        }
                    }
                }
            }
            
            // Toggle options card
            SettingsCard {
                VStack(spacing: 4) {
                    SettingsToggle(isOn: config.disableShaderCache, icon: "memorychip", label: "Shader Cache")
                    
                    Divider()
                    
                    SettingsToggle(isOn: config.disablevsync.reversed, icon: "arrow.triangle.2.circlepath", label: "VSync")
                    
                    Divider()
                    
                    SettingsToggle(isOn: config.enableTextureRecompression, icon: "rectangle.compress.vertical", label: "Texture Recompression")
                    
                    Divider()
                    
                    SettingsToggle(isOn: config.disableDockedMode, icon: "dock.rectangle", label: "Docked Mode")
                    
                    Divider()
                    
                    SettingsToggle(isOn: config.macroHLE, icon: "gearshape", label: "Macro HLE")
                    
                    Divider()
                    
                    SettingsToggle(isOn: $performacehud, icon: "speedometer", label: "Performance Overlay")
                }
            }
            
            // Aspect ratio card
            SettingsCard {
                VStack(alignment: .leading, spacing: 12) {
                    
                    SettingsToggle(isOn: $oldView, icon: "rectangle.on.rectangle.dashed", label: "Old Display UI")
                    
                    Divider()
                    
                    labelWithIcon("Aspect Ratio", iconName: "rectangle.expand.vertical")
                        .font(.headline)
                    
                    if (horizontalSizeClass == .regular && verticalSizeClass == .regular) || (horizontalSizeClass == .regular && verticalSizeClass == .compact) {
                        Picker(selection: config.aspectRatio) {
                            ForEach(AspectRatio.allCases, id: \.self) { ratio in
                                Text(ratio.displayName).tag(ratio)
                            }
                        } label: {
                            EmptyView()
                        }
                        .pickerStyle(.segmented)
                    } else {
                        Picker(selection: config.aspectRatio) {
                            ForEach(AspectRatio.allCases, id: \.self) { ratio in
                                Text(ratio.displayName).tag(ratio)
                            }
                        } label: {
                            EmptyView()
                        }
                        .pickerStyle(.menu)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.vertical, 4)
                    }
                }
            }
        }
    }
    
    // MARK: - Input Settings
    
    private var inputSettings: some View {
        SettingsSection(title: "Input Configuration") {
            // Controller selection card
            SettingsCard {
                VStack(alignment: .leading, spacing: 12) {
                    Text("Controller Selection")
                        .font(.headline)
                        .foregroundColor(.primary)
                    
                    if controllerManager.currentControllers.isEmpty {
                        emptyControllersView
                    } else {
                        controllerListView
                    }
                    
                    if hasAvailableControllers {
                        Divider()
                        addControllerButton
                    }
                }
            }
            
            // On-screen controls card
            SettingsCard {
                VStack(spacing: 4) {
                    SettingsToggle(isOn: $stickButton, icon: "l.joystick.press.down", label: "Show Stick Buttons")
                    
                    Divider()
                    
                    SettingsToggle(isOn: $ryuDemo, icon: "hand.draw", label: "On-Screen Controller (Demo)")
                        .disabled(true)
                    
                    Divider()
                    
                    SettingsToggle(isOn: $swapBandA, icon: "rectangle.2.swap", label: "Swap Face Buttons (Physical Controller)")
                }
            }
            
            // Controller scale card
            SettingsCard {
                VStack(alignment: .leading, spacing: 12) {
                    Text("On-Screen Controller")
                        .font(.headline)
                        .foregroundColor(.primary)
                    
                    Group {
                        HStack {
                            labelWithIcon("Scale", iconName: "magnifyingglass")
                                .font(.headline)
                            Spacer()
                            Button {
                                showControllerInfo.toggle()
                            } label: {
                                Image(systemName: "info.circle")
                                    .foregroundColor(.secondary)
                            }
                            .buttonStyle(.plain)
                            .alert(isPresented: $showControllerInfo) {
                                Alert(
                                    title: Text("On-Screen Controller Scale"),
                                    message: Text("Adjust the On-Screen Controller size."),
                                    dismissButton: .default(Text("OK"))
                                )
                            }
                        }
                        
                        VStack(spacing: 8) {
                            Slider(value: $controllerScale, in: 0.1...3.0, step: 0.05)
                            
                            HStack {
                                Text("Smaller")
                                    .font(.caption2)
                                    .foregroundColor(.secondary)
                                
                                Spacer()
                                
                                Text("\(controllerScale, specifier: "%.2f")x")
                                    .font(.headline)
                                    .foregroundColor(.blue)
                                
                                Spacer()
                                
                                Text("Larger")
                                    .font(.caption2)
                                    .foregroundColor(.secondary)
                            }
                        }
                    }
                    
                    Divider()
                    
                    Group {
                        HStack {
                            labelWithIcon("Opacity", iconName: "magnifyingglass")
                                .font(.headline)
                            Spacer()
                            Button {
                                showControllerInfo.toggle()
                            } label: {
                                Image(systemName: "info.circle")
                                    .foregroundColor(.secondary)
                            }
                            .buttonStyle(.plain)
                            .alert(isPresented: $showControllerInfo) {
                                Alert(
                                    title: Text("On-Screen Controller Opacity"),
                                    message: Text("Adjust the On-Screen Controller transparency."),
                                    dismissButton: .default(Text("OK"))
                                )
                            }
                        }
                        
                        VStack(spacing: 8) {
                            Slider(value: $controllerOpacity, in: 0.01...1.0, step: 0.05)
                            
                            HStack {
                                Text("More Transparent")
                                    .font(.caption2)
                                    .foregroundColor(.secondary)
                                
                                Spacer()
                                
                                Text("\(controllerOpacity, specifier: "%.2f")x")
                                    .font(.headline)
                                    .foregroundColor(.blue)
                                
                                Spacer()
                                
                                Text("Less Transparent")
                                    .font(.caption2)
                                    .foregroundColor(.secondary)
                            }
                        }
                    }
                }
            }
        }
    }

    // MARK: - Controller Selection Components

    private var hasAvailableControllers: Bool {
        !controllerManager.nativeControllers.map(\.ryujinxController).filter { !contains(controllerManager.currentControllers, value: $0) }.isEmpty
    }

    private var emptyControllersView: some View {
        HStack {
            Text("No controllers selected (Keyboard will be used)")
                .foregroundColor(.secondary)
                .italic()
            Spacer()
        }
        .padding(.vertical, 8)
    }

    private var controllerListView: some View {
        VStack(spacing: 0) {
            Divider()
            
            ForEach(controllerManager.currentControllers.indices, id: \.self) { index in
                let controller = controllerManager.currentControllers[index]
                
                VStack(spacing: 0) {
                    HStack {
                        Image(systemName: "gamecontroller.fill")
                            .foregroundColor(.blue)
                        
                        Text("Player \(index + 1): \(controller.name)")
                            .lineLimit(1)
                        
                        Spacer()
                        
                        Button {
                            toggleController(controllerManager.currentControllers[index])
                        } label: {
                            Image(systemName: "xmark.circle.fill")
                                .foregroundColor(.secondary)
                        }
                        .buttonStyle(.plain)
                    }
                    .padding(.vertical, 8)
                    .onAppear() {
                        print(controllerManager.currentControllers)
                    }
                    
                    if index < controllerManager.currentControllers.count - 1 {
                        Divider()
                    }
                }
                .contextMenu {
                    ForEach(ControllerType.allCases) { type in
                        if controllerManager.currentControllers[index].controllerType == type {
                            Button {
                                controllerManager.currentControllers[index].controllerType = type
                                controllerManager.controllerTypes[index] = controller.controllerType
                            } label: {
                                Label(type.rawValue, systemImage: "checkmark")
                            }
                        } else {
                            Button(type.rawValue) {
                                controllerManager.currentControllers[index].controllerType = type
                                controllerManager.controllerTypes[index] = controller.controllerType
                            }
                            .tag(type)
                        }
                    }
                }
            }
            .onAppear {
                
                for (index, controller) in controllerManager.currentControllers.enumerated() {
                    if controller.isVirtualController {
                        controllerManager.currentControllers[index].controllerType = controllerManager.controllerTypes[index] ?? .joyconPair
                    } else {
                        controllerManager.currentControllers[index].controllerType = controllerManager.controllerTypes[index] ?? .proController
                    }
                }

            }
        }
    }

    private var addControllerButton: some View {
        Menu {
            ForEach(controllerManager.nativeControllers.map(\.ryujinxController).filter { !contains(controllerManager.currentControllers, value: $0) }) { controller in
                Button {
                    controllerManager.currentControllers.append(controller)
                } label: {
                    Text(controller.name)
                }
            }
        } label: {
            Label("Add Controller", systemImage: "plus.circle.fill")
                .foregroundColor(.blue)
                .frame(maxWidth: .infinity, alignment: .center)
                .padding(.vertical, 6)
        }
    }
    
    func contains(_ cool: [Controller], value: Controller) -> Bool {
        for item in cool {
            if item.name == value.name && item.id == value.id {
                return true
            }
        }
        return false
    }
    
    // MARK: - System Settings
    
    private var systemSettings: some View {
        SettingsSection(title: "System Configuration") {
            // Language and region card
            SettingsCard {
                VStack(alignment: .leading, spacing: 16) {
                    VStack(alignment: .leading, spacing: 8) {
                        labelWithIcon("System Language", iconName: "character.bubble")
                            .font(.headline)
                        
                        Picker(selection: config.language) {
                            ForEach(SystemLanguage.allCases, id: \.self) { language in
                                Text(language.displayName).tag(language)
                            }
                        } label: {
                            EmptyView()
                        }
                        .pickerStyle(.menu)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.vertical, 4)
                    }
                    
                    Divider()
                    
                    VStack(alignment: .leading, spacing: 8) {
                        labelWithIcon("Region", iconName: "globe")
                            .font(.headline)
                        
                        Picker(selection: config.regioncode) {
                            ForEach(SystemRegionCode.allCases, id: \.self) { region in
                                Text(region.displayName).tag(region)
                            }
                        } label: {
                            EmptyView()
                        }
                        .pickerStyle(.menu)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.vertical, 4)
                    }
                }
            }
            
            // CPU options card
            SettingsCard {
                VStack(alignment: .leading, spacing: 16) {
                    Text("CPU Configuration")
                        .font(.headline)
                        .foregroundColor(.primary)
                    
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Memory Manager Mode")
                            .font(.subheadline)
                            .foregroundColor(.secondary)
                        
                        Picker(selection: config.memoryManagerMode) {
                            ForEach(filteredMemoryModes, id: \.0) { key, displayName in
                                Text(displayName).tag(key)
                            }
                        } label: {
                            EmptyView()
                        }
                        .pickerStyle(.segmented)
                    }
                    
                    Divider()
                    
                    SettingsToggle(isOn: config.disablePTC.reversed, icon: "cpu", label: "PTC")
                    
                    if let gpuInfo = getGPUInfo(), gpuInfo.hasPrefix("Apple M") {
                        Divider()
                        
                        if #available(iOS 16.4, *) {
                            SettingsToggle(isOn: .constant(false), icon: "bolt", label: "Hypervisor")
                                .disabled(true)
                        } else if checkAppEntitlement("com.apple.private.hypervisor") {
                            SettingsToggle(isOn: config.hypervisor, icon: "bolt", label: "Hypervisor")
                        }
                    }
                }
            }
        }
    }
    
    // MARK: - Advanced Settings
    
    private var advancedSettings: some View {
        SettingsSection(title: "Advanced Options") {
            // Debug options card
            SettingsCard {
                VStack(spacing: 4) {
                    SettingsToggle(isOn: $showlogsloading, icon: "text.alignleft", label: "Show Logs While Loading")
                    
                    Divider()
                    
                    SettingsToggle(isOn: $showlogsgame, icon: "text.magnifyingglass", label: "Show Logs In-Game")
                    
                    Divider()
                    
                    SettingsToggle(isOn: config.debuglogs, icon: "exclamationmark.bubble", label: "Debug Logs")
                    
                    Divider()
                    
                    SettingsToggle(isOn: config.tracelogs, icon: "waveform.path", label: "Trace Logs")
                }
            }
            
            // Advanced toggles card
            SettingsCard {
                VStack(spacing: 4) {
                    SettingsToggle(isOn: $runOnMainThread, icon: "square.stack.3d.up", label: "Run Core on Main Thread")
                    
                    Divider()
                    
                    SettingsToggle(isOn: config.dfsIntegrityChecks, icon: "checkmark.shield", label: "Disable FS Integrity Checks")
                    
                    Divider()
                    
                    
                    if MTLHud.shared.canMetalHud {
                        SettingsToggle(isOn: $metalHudEnabler.metalHudEnabled, icon: "speedometer", label: "Metal Performance HUD")
                        
                        Divider()
                    }
                    
                    SettingsToggle(isOn: $ignoreJIT, icon: "cpu", label: "Ignore JIT Popup")
                    
                    Divider()
                    
                    Button {
                        finishedStorage = false
                    } label: {
                        HStack {
                            Image(systemName: "arrow.triangle.2.circlepath.circle.fill")
                                .foregroundColor(.blue)
                            Text("Show Setup Screen")
                                .foregroundColor(.blue)
                            Spacer()
                        }
                        .padding(8)
                    }
                    
                }
            }
            
            // Memory hacks card
            SettingsCard {
                VStack(spacing: 4) {
                    SettingsToggle(isOn: config.expandRam, icon: "exclamationmark.bubble", label: "Expand Guest RAM")
                        .accentColor(.red)
                        .disabled(totalMemory < 5723)
                    
                    Divider()
                    
                    SettingsToggle(isOn: config.ignoreMissingServices, icon: "waveform.path", label: "Ignore Missing Services")
                        .accentColor(.red)
                }
            }
            
            // Additional args card
            SettingsCard {
                VStack(alignment: .leading, spacing: 12) {
                    Text("Additional Arguments")
                        .font(.headline)
                        .foregroundColor(.primary)
                    
                    let binding = Binding(
                        get: {
                            config.additionalArgs.wrappedValue.joined(separator: ", ")
                        },
                        set: { newValue in
                            settingsManager.config.additionalArgs = newValue
                                .split(separator: ",")
                                .map { $0.trimmingCharacters(in: .whitespaces) }
                        }
                    )
                    
                    
                    if #available(iOS 15.0, *) {
                        TextField("Separate arguments with commas", text: binding)
                            .font(.system(.body, design: .monospaced))
                            .textFieldStyle(.roundedBorder)
                            .textInputAutocapitalization(.none)
                            .disableAutocorrection(true)
                            .padding(.vertical, 4)
                            .toolbar {
                                ToolbarItem(placement: .keyboard) {
                                    Button("Dismiss") {
                                        isArgumentsKeyboardVisible = false
                                    }
                                }
                            }
                            .focused($isArgumentsKeyboardVisible)
                    } else {
                        TextField("Separate arguments with commas", text: binding)
                            .font(.system(.body, design: .monospaced))
                            .textFieldStyle(.roundedBorder)
                            .disableAutocorrection(true)
                            .padding(.vertical, 4)
                    }
                }
            }
            
            // Page size info card
            SettingsCard {
                HStack {
                    labelWithIcon("Page Size", iconName: "textformat.size")
                    Spacer()
                    Text("\(String(Int(getpagesize())))")
                        .font(.system(.body, design: .monospaced))
                        .foregroundColor(.secondary)
                }
                
                HStack {
                    let res = (UIApplication.shared.connectedScenes.first! as! UIWindowScene).windows.first!.bounds.size
                    labelWithIcon("App Resolution", iconName: "display")
                    Spacer()
                    Text("\(Int(res.width))x\(Int(res.height))")
                        .font(.system(.body, design: .monospaced))
                        .foregroundColor(.secondary)
                }
            }
            
            if gamepo {
                SettingsCard {
                    Text("The cake is a lie")
                        .font(.system(.body, design: .monospaced))
                        .foregroundColor(.secondary)
                        .frame(maxWidth: .infinity, alignment: .center)
                }
            }
        }
    }
    
    // MARK: - Miscellaneous Settings
    
    private var miscSettings: some View {
        SettingsSection(title: "Miscellaneous Options") {
            SettingsCard {
                VStack(alignment: .leading, spacing: 16) {
                    Text("Custom ROM folders")
                        .font(.headline)
                        .foregroundColor(.primary)
                    
                    FolderListView()
                }
            }
            
            SettingsCard {
                VStack(alignment: .leading, spacing: 16) {
                    Text("Network Configuration")
                        .font(.headline)
                        .foregroundColor(.primary)
                    
                    VStack(alignment: .leading, spacing: 8) {
                        labelWithIcon("Network Interface", iconName: "wifi")
                            .font(.headline)
                        
                        Picker(selection: $ldn) {
                            ForEach(printAllIPv4Addresses(), id: \.self) { option in
                                Text(option)
                            }
                        } label: {
                            EmptyView()
                        }
                        .pickerStyle(.menu)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.vertical, 4)
                    }
                    
                    Divider()
                    
                    SettingsToggle(isOn: config.enableInternet, icon: "wifi.router.fill", label: "Guest Internet Access / LAN Mode")
                    
                    Divider()
                    
                    SettingsToggle(isOn: config.ldn_mitm, icon: "ipad.sizes", label: "ldn_mitm")
                    
                    // enable-ldn-mitm
                }
            }
            
            
            SettingsCard {
                VStack(spacing: 4) {
                    if UIDevice.current.userInterfaceIdiom == .pad {
                        SettingsToggle(isOn: $toggleGreen, icon: "arrow.clockwise", label: "Toggle Color Green when \"ON\"")
                        
                        Divider()
                    }
            
                    SettingsToggle(isOn: $showBatteryPercentage, icon: "battery.100percent.bolt", label: "Show Battery Percentage (in-game)")
                    
                    Divider()
                
                    // Disable Touch card
                    SettingsToggle(isOn: $disableTouch, icon: "rectangle.and.hand.point.up.left.filled", label: "Disable Touch")
                    
                    Divider()
                    
                    // Select User on Launch card
                    SettingsToggle(isOn: $showProfileonGame, icon: "person.3", label: "Select Profile on Game Launch")
                    
                    Divider()
                    
                    Button {
                        showAppIconSwitcher = true
                    } label: {
                        HStack {
                            Image(systemName: "app.dashed")
                                .foregroundColor(.blue)
                            Text("App Icon Switcher")
                                .foregroundColor(.primary)
                            Spacer()
                        }
                        .padding(.vertical, 8)
                    }
                    .sheet(isPresented: $showAppIconSwitcher) {
                        AppIconSwitcherView()
                    }
                    
                    Divider()
                    
                    // Exit button card
                    SettingsToggle(isOn: $ssb, icon: "arrow.left.circle", label: "Menu Button (in-game)")
                    
                    Divider()
                    
                    // Restarts app when it crashes card
                    SettingsToggle(isOn: $restartApp, icon: "arrow.clockwise", label: "Lock in App")
                    
                    Divider()
                    
                    
                    // Location to keep app in Background
                    SettingsToggle(isOn: $locationenabled, icon: "location.viewfinder", label: "Keep app in background")
                    
                    Divider()
                    
                    if UIDevice.current.userInterfaceIdiom == .pad {
                        // Old Settings UI
                        SettingsToggle(isOn: $oldSettingsUI, icon: "ipad.landscape", label: "Non Switch-like Settings")
                        
                        Divider()
                    }
                    
                    
                    // JIT options
                    if #available(iOS 17.0.1, *) {
                        let checked = stikJITorStikDebug()
                        let stikJIT = checked == 1 ? "StikDebug" : checked == 2 ? "StikJIT" : "StikDebug"
                        
                        SettingsToggle(isOn: $stikJIT, icon: "bolt.heart", label: stikJIT)
                            .contextMenu {
                                Button {
                                    if let windowScene = UIApplication.shared.connectedScenes.first as? UIWindowScene,
                                       let mainWindow = windowScene.windows.last {
                                        let alertController = UIAlertController(title: "About \(stikJIT)", message: "\(stikJIT) is a really amazing iOS Application to Enable JIT on the go on-device, made by the best, most kind, helpful and nice developers of all time jkcoxson and Blu <3", preferredStyle: .alert)
                                        
                                        let learnMoreButton = UIAlertAction(title: "Learn More", style: .default) {_ in
                                            UIApplication.shared.open(URL(string: "https://github.com/StephenDev0/StikJIT")!)
                                        }
                                        alertController.addAction(learnMoreButton)
                                        
                                        let doneButton = UIAlertAction(title: "Done", style: .cancel, handler: nil)
                                        alertController.addAction(doneButton)
                                        
                                        mainWindow.rootViewController?.present(alertController, animated: true)
                                    }
                                } label: {
                                    Text("About")
                                }
                            }
                    } else {
                        SettingsToggle(isOn: $useTrollStore, icon: "troll.svg", label: "TrollStore JIT")
                    }
                    
                    Divider()
                    
                    // MoltenVK Options
                    SettingsToggle(isOn: $syncqsubmits, icon: "line.diagonal", label: "MVK: Synchronous Queue Submits")
                        .contextMenu {
                            Button {
                                if let windowScene = UIApplication.shared.connectedScenes.first as? UIWindowScene,
                                   let mainWindow = windowScene.windows.last {
                                    let alertController = UIAlertController(title: "About MVK: Synchronous Queue Submits", message: "Enable this option if Mario Kart 8 is crashing at Grand Prix mode.", preferredStyle: .alert)
                                    
                                    let doneButton = UIAlertAction(title: "OK", style: .cancel, handler: nil)
                                    alertController.addAction(doneButton)
                                    
                                    mainWindow.rootViewController?.present(alertController, animated: true)
                                }
                            } label: {
                                Text("About")
                            }
                        }
                    
                    Divider()
                    
                    if #available(iOS 19, *), !ProcessInfo.processInfo.isiOSAppOnMac {
                        SettingsToggle(isOn: $dualMapped, icon: "light.strip.2", label: "Dual Mapped JIT")
                            .disabled(true)
                    } else {
                        SettingsToggle(isOn: $dualMapped, icon: "light.strip.2", label: "Dual Mapped JIT")
                    }
                    
                    Divider()
                    
                    SettingsToggle(isOn: $checkForUpdate, icon: "square.and.arrow.down", label: "Check for Updates")
                    
                    Divider()
                    
                    Button {
                        Ryujinx.clearShaderCache()
                    } label: {
                        HStack {
                            Image(systemName: "trash")
                                .foregroundColor(.blue)
                            Text("Clear All Shader Cache")
                                .foregroundColor(.primary)
                            Spacer()
                        }
                        .padding(.vertical, 8)
                    }
                }
            }
        }
    }
    
    // MARK: - Helper Functions
    
    private func toggleController(_ controller: Controller) {
        if controllerManager.currentControllers.contains(where: { $0.id == controller.id }) {
            controllerManager.currentControllers.removeAll(where: { $0.id == controller.id })
        } else {
            controllerManager.currentControllers.append(controller)
        }
    }
    
    
    func getGPUInfo() -> String? {
        let device = MTLCreateSystemDefaultDevice()
        return device?.name
    }
    
    @ViewBuilder
    private func labelWithIcon(_ text: String, iconName: String, flipimage: Bool? = nil) -> some View {
        HStack(spacing: 8) {
            if iconName.hasSuffix(".svg") {
                if let flipimage, flipimage {
                    SVGView(svgName: iconName, color: .blue)
                        // .symbolRenderingMode(.hierarchical)
                        .frame(width: 20, height: 20)
                        .rotation3DEffect(.degrees(180), axis: (x: 0, y: 1, z: 0))
                } else {
                    SVGView(svgName: iconName, color: .blue)
                        // .symbolRenderingMode(.hierarchical)
                        .frame(width: 20, height: 20)
                }
            } else if !iconName.isEmpty {
                Image(systemName: iconName)
                    // .symbolRenderingMode(.hierarchical)
                    .foregroundColor(.blue)
            }
            Text(text)
        }
        .font(.body)
    }
}

struct SVGView: UIViewRepresentable {
    var svgName: String
    var color: Color = Color.black
    
    func makeUIView(context: Context) -> UIView {
        var svgName = svgName
        let hammock = UIView()
        
        if svgName.hasSuffix(".svg") {
            svgName.removeLast(4)
        }
        
        _ = UIView(svgNamed: svgName) { svgLayer in
            svgLayer.fillColor = UIColor(color).cgColor // Apply the provided color
            svgLayer.resizeToFit(hammock.frame)
            hammock.layer.addSublayer(svgLayer)
        }
        
        return hammock
    }
    
    func updateUIView(_ uiView: UIView, context: Context) {
        // Update the SVG view's fill color when the color changes
        if let svgLayer = uiView.layer.sublayers?.first as? CAShapeLayer {
            svgLayer.fillColor = UIColor(color).cgColor
        }
    }
}

func saveSettings(config: Ryujinx.Arguments) {
    do {
        let encoder = JSONEncoder()
        encoder.outputFormatting = .prettyPrinted
        let data = try encoder.encode(config)
        
        let fileURL = URL.documentsDirectory.appendingPathComponent("config.json")
        
        try data.write(to: fileURL)
        // print("Settings saved to: \(fileURL.path)")
    } catch {
        // print("Failed to save settings: \(error)")
    }
}

func loadSettings() -> Ryujinx.Arguments? {
    do {
        let fileURL = URL.documentsDirectory.appendingPathComponent("config.json")
        
        guard FileManager.default.fileExists(atPath: fileURL.path) else {
            // print("Config file does not exist at: \(fileURL.path)")
            return nil
        }
        
        let data = try Data(contentsOf: fileURL)
        
        let decoder = JSONDecoder()
        let configs = try decoder.decode(Ryujinx.Arguments.self, from: data)
        return configs
    } catch {
        // print("Failed to load settings: \(error)")
        return nil
    }
}


// MARK: - Supporting Views

struct CategoryButton: View {
    let title: String
    let icon: String
    let isSelected: Bool
    let action: () -> Void
    
    var body: some View {
        Button(action: action) {
            VStack(spacing: 6) {
                Image(systemName: icon)
                    .font(.system(size: 16, weight: isSelected ? .semibold : .regular))
                Text(title)
                    .font(.system(size: 12, weight: isSelected ? .semibold : .regular))
            }
            .foregroundColor(isSelected ? .blue : .secondary)
            .frame(width: 70, height: 56)
            .background(
                RoundedRectangle(cornerRadius: 12)
                    .fill(isSelected ? Color.blue.opacity(0.15) : Color.clear)
            )
            .animation(.bouncy(duration: 0.3), value: isSelected)
        }
    }
}

struct SettingsSection<Content: View>: View {
    let title: String
    let content: Content
    
    init(title: String, @ViewBuilder content: () -> Content) {
        self.title = title
        self.content = content()
    }
    
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(title)
                .font(.title2.weight(.bold))
                .padding(.horizontal)
            
            content
        }
    }
}

struct SettingsCard<Content: View>: View {
    @Environment(\.colorScheme) var colorScheme
    @AppStorage("oldSettingsUI") var oldSettingsUI = false
    let content: Content
    
    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }
    
    var body: some View {
        if UIDevice.current.userInterfaceIdiom == .phone || oldSettingsUI {
            content
                .padding()
                .background(
                    RoundedRectangle(cornerRadius: 12)
                        .fill(colorScheme == .dark ? Color(.systemGray6) : Color.white)
                        .shadow(color: Color.black.opacity(0.05), radius: 5, x: 0, y: 2)
                )
                .padding(.horizontal)
        } else {
            VStack {
                Divider()
                content
                Divider()
            }
            .padding()
        }
    }
}

struct SettingsToggle: View {
    @Binding var isOn: Bool
    let icon: String
    let label: String
    var disabled: Bool = false
    @AppStorage("toggleGreen") var toggleGreen: Bool = false
    @AppStorage("oldSettingsUI") var oldSettingsUI = false
    
    var body: some View {
        if UIDevice.current.userInterfaceIdiom == .phone || oldSettingsUI {
            Toggle(isOn: $isOn) {
                HStack(spacing: 8) {
                    if icon.hasSuffix(".svg") {
                        SVGView(svgName: icon, color: .blue)
                            .frame(width: 20, height: 20)
                    } else {
                        Image(systemName: icon)
                        // .symbolRenderingMode(.hierarchical)
                            .foregroundColor(.blue)
                    }
                    
                    Text(label)
                        .font(.body)
                }
            }
            .toggleStyle(SwitchToggleStyle(tint: .blue))
            .disabled(disabled)
            .padding(.vertical, 6)
        } else {
            Group {
                HStack(spacing: 8) {
                    HStack {
                        if icon.hasSuffix(".svg") {
                            SVGView(svgName: icon, color: .blue)
                                .frame(width: 20, height: 20)
                        } else {
                            Image(systemName: icon)
                            // .symbolRenderingMode(.hierarchical)
                                .foregroundStyle(.blue)
                        }
                        
                        Text(label)
                            .font(.body)
                    }
                    
                    Spacer()
                    
                    
                    Text(isOn ? "ON" : "Off")
                        .foregroundStyle(isOn ? (toggleGreen ? .green : .blue) : .blue)
                }
                .padding()
                .onTapGesture {
                    isOn.toggle()
                }
            }
        }
    }
    
    func disabled(_ disabled: Bool) -> SettingsToggle {
        var view = self
        view.disabled = disabled
        return view
    }
    
    func accentColor(_ color: Color) -> SettingsToggle {
        var view = self
        return view
    }
}

struct InfoCard: View {
    let title: String
    let value: String
    let icon: String
    let color: Color
    
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Image(systemName: icon)
                    .foregroundColor(color)
                Text(title)
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            
            Text(value)
                .font(.system(size: 14, weight: .medium))
                .lineLimit(1)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(10)
        .background(color.opacity(0.1))
        .cornerRadius(8)
    }
}

// this code is used to enable the keyboard to be dismissed when scrolling if available on iOS 16+
extension View {
    @ViewBuilder
    func scrollDismissesKeyboardIfAvailable() -> some View {
        if #available(iOS 16.0, *) {
            self.scrollDismissesKeyboard(.interactively)
        } else {
            self
        }
    }
}

// seperate view for testing™
struct FolderListView: View {
    @StateObject private var folderManager = ROMFolderManager.shared
    
    var body: some View {
        VStack(spacing: 0) {
            Divider()
            
            ForEach(Array(folderManager.bookmarks.keys).sorted(), id: \.self) { path in
                VStack(spacing: 0) {
                    HStack {
                        Image(systemName: "folder.fill")
                            .foregroundColor(.blue)
                        
                        Text(path)
                            .lineLimit(1)
                            .truncationMode(.middle)
                        
                        Spacer()
                        
                        Button {
                            folderManager.bookmarks.removeValue(forKey: path)
                        } label: {
                            Image(systemName: "xmark.circle.fill")
                                .foregroundColor(.secondary)
                        }
                        .buttonStyle(.plain)
                    }
                    .padding(.vertical, 8)
                    
                    if path != folderManager.bookmarks.keys.sorted().last {
                        Divider()
                    }
                }
            }
            
            Button(action: {
                FileImporterManager.shared.importFiles(types: [.folder]) { result in
                    switch result {
                    case .success(let paths):
                        for url in paths {
                            let wow = folderManager.addFolder(url: url)
                            print(wow)
                            Ryujinx.shared.games = Ryujinx.shared.loadGames()
                        }
                    case .failure:
                        break
                    }
                }
            }) {
                HStack {
                    Image(systemName: "plus.circle.fill")
                    Text("Add Folder")
                }
                .padding(.vertical, 10)
            }
        }
        .padding(.horizontal)
    }
    
}
