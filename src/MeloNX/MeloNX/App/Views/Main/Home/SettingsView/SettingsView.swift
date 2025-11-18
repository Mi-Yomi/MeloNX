//
//  SettingsView.swift
//  MeloNX
//
//  Created by Stossy11 on 25/11/2024.
//

import SwiftUI
import SwiftSVG
import UIKit


struct SettingsViewNew: View {
    @ObservedObject public var settingsManager = SettingsManager.shared
    @ObservedObject public var nativeSettingsManager = NativeSettingsManager.shared
    @ObservedObject var controllerManager = ControllerManager.shared
    @EnvironmentObject var ryujinx: Ryujinx
    @StateObject var metalHudEnabler = MTLHud.shared
    
    @AppStorage("useTrollStore") var useTrollStore: Bool = false
    @AppStorage("stikJIT") var stikJIT: Bool = false
    @AppStorage("OldView") var oldView = true
    @AppStorage("LDN_MITM") var ldn = printAllIPv4Addresses().first ?? "Unknown"
    @AppStorage("portal") var gamepo = false
    
    @Environment(\.colorScheme) var colorScheme
    @Environment(\.verticalSizeClass) var verticalSizeClass: UserInterfaceSizeClass?
    @Environment(\.horizontalSizeClass) var horizontalSizeClass: UserInterfaceSizeClass?
    
    @State private var selectedCategory: SettingsCategory = .graphics
    @State private var showResolutionInfo = false
    @State private var showAnisotropicInfo = false
    @State private var showControllerInfo = false
    @State private var showAppIconSwitcher = false
    @State private var isShowingGameController = false
    @State private var searchText = ""
    @State private var selectedView = "Data Management"
    @State private var sidebar = true
    @FocusState private var isArgumentsKeyboardVisible: Bool
    
    private var config: Binding<Ryujinx.Arguments> {
        $settingsManager.config
    }
    
    private let memoryManagerModes = [
        ("HostMapped", "Host (fast)"),
        ("HostMappedUnsafe", "Host Unchecked (fast, unstable / unsafe)"),
        ("SoftwarePageTable", "Software (slow)"),
    ]
    
    private let totalMemory = ProcessInfo.processInfo.physicalMemory
    
    private var filteredMemoryModes: [(String, String)] {
        guard !searchText.isEmpty else { return memoryManagerModes }
        return memoryManagerModes.filter { $0.1.localizedCaseInsensitiveContains(searchText) }
    }
    
    private var appVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "Unknown"
    }
    
    private var isRegularLayout: Bool {
        (horizontalSizeClass == .regular && verticalSizeClass == .regular) ||
        (horizontalSizeClass == .regular && verticalSizeClass == .compact)
    }
    
    private var deviceIcon: String {
        let model = UIDevice.modelName
        if model.contains("iPad") { return "ipad" }
        if model.contains("iPhone") { return "iphone" }
        return "desktopcomputer"
    }
    
    private var memoryText: String {
        let divisor = ProcessInfo.processInfo.isiOSAppOnMac ? (1024 * 1024 * 1024) : 1_000_000_000
        return String(format: "%.0f GB", Double(totalMemory) / Double(divisor))
    }
    
    private var systemVersionString: String {
        let versionPart = ProcessInfo.processInfo.operatingSystemVersionString
            .replacingOccurrences(of: "Version ", with: "")
        let parts = versionPart.components(separatedBy: " (Build ")
        
        if parts.count == 2 {
            let version = parts[0]
            let build = parts[1].replacingOccurrences(of: ")", with: "")
            let osName = ProcessInfo.processInfo.isiOSAppOnMac ? "macOS" : UIDevice.current.systemName
            return "\(osName) \(version) (\(build))"
        }
        
        let osName = ProcessInfo.processInfo.isiOSAppOnMac ? "macOS" : UIDevice.current.systemName
        return "\(osName) \(UIDevice.current.systemVersion)"
    }
    
    
    enum SettingsCategory: String, CaseIterable, Identifiable {
        case graphics = "Graphics"
        case input = "Input"
        case misc = "Misc"
        case system = "System"
        case advanced = "Advanced"
        
        var id: String { rawValue }
        
        var icon: String {
            switch self {
            case .graphics: return "paintbrush.fill"
            case .input: return "gamecontroller.fill"
            case .system: return "gearshape.fill"
            case .misc: return "ellipsis.circle.fill"
            case .advanced: return "terminal.fill"
            }
        }
        
        @ViewBuilder
        func view(for parent: SettingsViewNew) -> some View {
            switch self {
            case .graphics: parent.graphicsSettings
            case .misc: parent.miscSettings
            case .input: parent.inputSettings
            case .system: parent.systemSettings
            case .advanced: parent.advancedSettings
            }
        }
    }
    
    // i keep loosing where it is so i added MARK fr
    // MARK: - Body
    
    var body: some View {
        if UIDevice.current.userInterfaceIdiom == .phone {
            iOSSettings
        } else if !nativeSettingsManager.oldSettingsUI.value {
            iPadOSSettings
                .ignoresSafeArea()
                .edgesIgnoringSafeArea(.all)
        } else {
            iOSSettings
        }
    }
    
    var allBody: some View {
        ZStack {
            graphicsSettings
            miscSettings
            inputSettings
            systemSettings
            advancedSettings
        }
        .frame(width: 2, height: 2)
    }
    
    // MARK: - iPadOS Layout
    
    var iPadOSSettings: some View {
        VStack {
            SidebarView(
                sidebar: { AnyView(sidebarContent) },
                content: {
                    ScrollView {
                        selectedCategory.view(for: self)
                    }
                },
                showSidebar: $sidebar
            )
            .onAppear(perform: loadSettings)
        }
    }
    
    private var sidebarContent: some View {
        ScrollView(.vertical) {
            VStack {
                sidebarHeaderSection
                
                Divider()
                
                sidebarCategoryList
            }
            .padding()
        }
    }
    
    private var sidebarHeaderSection: some View {
        VStack(spacing: 16) {
            HStack {
                jitStatusIndicator
                
                if isInLiveContainer.0, isInLiveContainer.1 == nil {
                    Text("LiveContainer")
                        .font(.system(size: 14))
                        .foregroundColor(.indigo)
                }
                
                Spacer()
                
                Text("v\(appVersion)")
                    .font(.subheadline)
                    .foregroundColor(.secondary)
                
                Text("\(memoryText) RAM")
                    .font(.subheadline.weight(.medium))
                    .foregroundColor(.secondary)
            }
            
            deviceInfoCards
        }
        .padding()
    }
    
    private var sidebarCategoryList: some View {
        ForEach(SettingsCategory.allCases, id: \.id) { category in
            CategoryRow(
                category: category,
                isSelected: selectedCategory == category
            ) {
                withAnimation(.smooth) {
                    selectedCategory = category
                }
            }
        }
    }
    
    // MARK: - iOS Layout
    
    var iOSSettings: some View {
        iOSNav {
            ZStack {
                Color(UIColor.systemBackground)
                    .ignoresSafeArea()
                
                VStack(spacing: 0) {
                    categoryScrollView
                    Divider()
                    
                    ScrollView {
                        VStack(spacing: 24) {
                            deviceInfoCard
                                .padding(.horizontal)
                                .padding(.top)
                            
                            selectedCategory.view(for: self)
                            
                            Spacer(minLength: 50)
                        }
                        .padding(.bottom)
                    }
                    .scrollDismissesKeyboardIfAvailable()
                }
            }
            .navigationTitle("Settings")
            .navigationBarTitleDisplayMode(.large)
            .onAppear(perform: loadSettings)
        }
    }
    
    private var categoryScrollView: some View {
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
    }
    
    
    private var jitStatusIndicator: some View {
        HStack {
            Circle()
                .fill(ryujinx.jitenabled ? Color.green : Color.red)
                .frame(width: 12, height: 12)
            
            if !checkAppEntitlement("get-task-allow") &&
                !checkAppEntitlement("com.apple.security.cs.allow-jit") &&
                !checkAppEntitlement("dynamic-codesigning") &&
                !ryujinx.jitenabled {
                Text("No JIT Support. (no get-task-allow)")
                    .font(.subheadline.weight(.medium))
                    .foregroundColor(.red)
            } else {
                Text(ryujinx.jitenabled ? "JIT Enabled" : "JIT Not Acquired")
                    .font(.subheadline.weight(.medium))
                    .foregroundColor(ryujinx.jitenabled ? .green : .red)
            }
        }
    }
    
    @ViewBuilder
    private var deviceInfoCards: some View {
        InfoCard(
            title: "Device",
            value: UIDevice.modelName,
            icon: deviceIcon,
            color: .blue
        )
        
        InfoCard(
            title: "System",
            value: systemVersionString,
            icon: "applelogo",
            color: .gray
        )
        
        InfoCard(
            title: "Increased Memory Limit",
            value: checkAppEntitlement("com.apple.developer.kernel.increased-memory-limit") ? "Enabled" : "Disabled",
            icon: "memorychip.fill",
            color: .orange
        )
        
        if checkAppEntitlement("com.apple.developer.kernel.extended-virtual-addressing") {
            InfoCard(
                title: "Extended Virtual Addressing",
                value: "Enabled",
                icon: "memorychip",
                color: .yellow
            )
        }
        
        if let lc = isInLiveContainer.1, !isInLiveContainer.2 {
            InfoCard(
                title: "LiveContainer",
                value: "v\(lc.infoDictionary?["CFBundleShortVersionString"] as? String ?? (lc.infoDictionary?["CFBundleVersion"] as? String ?? "Unknown")) \(lc.infoDictionary?["LCVersionInfo"] as? String ?? "")",
                icon: "app.fill",
                color: .indigo
            )
        } else if isInLiveContainer.2 {
            InfoCard(
                title: "LiveContainer",
                value: "Multitask",
                icon: "app.fill",
                color: .indigo
            )
        }
    }
    
    private var deviceInfoCard: some View {
        VStack(spacing: 16) {
            HStack {
                VStack {
                    HStack {
                        jitStatusIndicator
                        if isInLiveContainer.0, isInLiveContainer.1 == nil {
                            Spacer()
                        }
                    }
                    if isInLiveContainer.0, isInLiveContainer.1 == nil {
                        HStack {
                            Text("Installed With LiveContainer")
                                .font(.subheadline.weight(.medium))
                                .foregroundColor(.cyan)
                            Spacer()
                        }
                    }
                }
                Spacer()
                
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
            
            if isRegularLayout {
                HStack(spacing: 16) { deviceInfoCards }
            } else {
                VStack(spacing: 16) { deviceInfoCards }
            }
        }
        .padding()
        .liquidGlass(cornerRadius: 16) {
            RoundedRectangle(cornerRadius: 16)
                .fill(colorScheme == .dark ? Color(.systemGray6) : Color.white)
                .shadow(color: Color.black.opacity(0.05), radius: 5, x: 0, y: 2)
        }
        .onAppear {
            ryujinx.checkForJIT()
        }
    }
    
    // MARK: - Graphics Settings
    
    private var graphicsSettings: some View {
        SettingsSection(title: "Graphics & Performance") {
            resolutionScaleCard
            anisotropicFilteringCard
            graphicsTogglesCard
            performanceOverlayCard
            aspectRatioCard
        }
    }
    
    private var resolutionScaleCard: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    labelWithIcon("Resolution Scale", iconName: "magnifyingglass")
                        .font(.headline)
                    Spacer()
                    infoButton(
                        title: "Resolution Scale",
                        message: "Adjust the internal rendering resolution. Higher values improve visuals but may reduce performance.",
                        isPresented: $showResolutionInfo
                    )
                }
                
                sliderWithLabels(
                    value: config.resscale,
                    range: 0.1...3.0,
                    step: 0.05,
                    currentValue: settingsManager.config.resscale,
                    minLabel: "0.1x",
                    maxLabel: "3.0x"
                )
            }
        }
    }
    
    private var anisotropicFilteringCard: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    labelWithIcon("Max Anisotropic Filtering", iconName: "magnifyingglass")
                        .font(.headline)
                    Spacer()
                    infoButton(
                        title: "Max Anisotropic Filtering",
                        message: "Adjust the internal Anisotropic filtering. Higher values improve texture quality at angles but may reduce performance. Default at 0 lets game decide.",
                        isPresented: $showAnisotropicInfo
                    )
                }
                
                sliderWithLabels(
                    value: config.maxAnisotropy,
                    range: 0...16.0,
                    step: 0.1,
                    currentValue: settingsManager.config.maxAnisotropy,
                    minLabel: "Off",
                    maxLabel: "16x",
                    format: "%.1f"
                )
            }
        }
    }
    
    private var graphicsTogglesCard: some View {
        SettingsCard {
            VStack(spacing: 4) {
                SettingsToggle(isOn: config.disableShaderCache, icon: "memorychip", label: "Shader Cache")
                Divider()
                SettingsToggle(isOn: config.disablevsync.reversed, icon: "arrow.triangle.2.circlepath", label: "VSync")
                Divider()
                SettingsToggle(isOn: config.disableDockedMode, icon: "dock.rectangle", label: "Docked Mode")
                Divider()
                SettingsToggle(isOn: config.macroHLE, icon: "gearshape", label: "Macro HLE")
            }
        }
    }
    
    private var performanceOverlayCard: some View {
        SettingsCard {
            VStack(spacing: 4) {
                SettingsToggle(isOn: nativeSettingsManager.performacehud.projectedValue, icon: "speedometer", label: "Performance Overlay")
                
                if nativeSettingsManager.performacehud.value {
                    Divider()
                    SettingsToggle(isOn: nativeSettingsManager.showBatteryPercentage.projectedValue, icon: "battery.100percent.bolt", label: "Show Battery Percentage")
                }
                
                Divider()
                SettingsToggle(isOn: nativeSettingsManager.horizontalorvertical.projectedValue, icon: "rotate.right", label: "Horizontal Performance Overlay")
            }
        }
    }
    
    private var aspectRatioCard: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 12) {
                // SettingsToggle(isOn: $oldView, icon: "rectangle.on.rectangle.dashed", label: "Old Display UI")
                // Divider()
                
                labelWithIcon("Aspect Ratio", iconName: "rectangle.expand.vertical")
                    .font(.headline)
                
                    Picker(selection: config.aspectRatio) {
                        ForEach(AspectRatio.allCases, id: \.self) { ratio in
                            Text(ratio.displayName).tag(ratio)
                        }
                    } label: {
                        EmptyView()
                    }
                    .if(!isRegularLayout) { view in
                        view.frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.vertical, 4)
                            .pickerStyle(.inline)
                    }
                    .if(isRegularLayout) { view in
                        view.pickerStyle(.menu)
                    }
                    .onAppear() {
                        oldView = true
                    }
            }
        }
    }
    
    // MARK: - Input Settings
    
    private var inputSettings: some View {
        SettingsSection(title: "Input Configuration") {
            controllerSelectionCard
            controllerTogglesCard
            onScreenControllerCard
        }
    }
    
    private var controllerSelectionCard: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 12) {
                Text("Controller Selection")
                    .font(.headline)
                    .foregroundColor(.primary)
                
                if controllerManager.allControllers.isEmpty {
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
    }
    
    private var controllerTogglesCard: some View {
        SettingsCard {
            SettingsToggle(isOn: nativeSettingsManager.swapBandA.projectedValue, icon: "rectangle.2.swap", label: "Swap Face Buttons (Physical Controller)")
        }
    }
    
    private var onScreenControllerCard: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 12) {
                Text("Global On-Screen Controller Configuration")
                    .font(.headline)
                    .foregroundColor(.primary)
                
                Button {
                    isShowingGameController = true
                } label: {
                    HStack {
                        Image(systemName: "formfitting.gamecontroller")
                            .foregroundColor(.blue)
                        Text("On-Screen Controller Layout")
                            .foregroundColor(.primary)
                        Spacer()
                    }
                    .padding(.vertical, 8)
                }
                .padding(.horizontal)
                .fullScreenCover(isPresented: $isShowingGameController) {
                    ControllerView(isEditing: $isShowingGameController, gameId: nil)
                }
                
                Divider().padding(.horizontal)
                
                controllerScaleSection
                Divider()
                controllerOpacitySection
                Divider()
                
                SettingsToggle(isOn: nativeSettingsManager.stickButton.projectedValue, icon: "l.joystick.press.down", label: "Show Stick Buttons")
                Divider()
                SettingsToggle(isOn: nativeSettingsManager.virtualControllerOffDefault.projectedValue, icon: "formfitting.gamecontroller.fill", label: "Deselected By Default (Virtual Controller)")
            }
        }
    }
    
    private var controllerScaleSection: some View {
        Group {
            HStack {
                labelWithIcon("Scale", iconName: "magnifyingglass")
                    .font(.headline)
                Spacer()
                infoButton(
                    title: "On-Screen Controller Scale",
                    message: "Adjust the On-Screen Controller size.",
                    isPresented: $showControllerInfo
                )
            }
            
            sliderWithLabels(
                value: nativeSettingsManager.setting(forKey: "On-ScreenControllerScale", default: 1.0).projectedValue,
                range: 0.1...3.0,
                step: 0.05,
                currentValue: nativeSettingsManager.setting(forKey: "On-ScreenControllerScale", default: 1.0).value,
                minLabel: "Smaller",
                maxLabel: "Larger"
            )
        }
        .padding(.horizontal)
    }
    
    private var controllerOpacitySection: some View {
        Group {
            HStack {
                labelWithIcon("Opacity", iconName: "magnifyingglass")
                    .font(.headline)
                Spacer()
                infoButton(
                    title: "On-Screen Controller Opacity",
                    message: "Adjust the On-Screen Controller transparency.",
                    isPresented: $showControllerInfo
                )
            }
            
            sliderWithLabels(
                value: nativeSettingsManager.setting(forKey: "On-ScreenControllerOpacity", default: 1.0).projectedValue,
                range: 0.05...1.0,
                step: 0.05,
                currentValue: nativeSettingsManager.setting(forKey: "On-ScreenControllerOpacity", default: 1.0).value,
                minLabel: "More Transparent",
                maxLabel: "Less Transparent"
            )
        }
        .padding(.horizontal)
    }
    
    // MARK: - Controller List
    
    private var hasAvailableControllers: Bool {
        !ControllerManager.shared.allControllers
            .filter { !contains(ControllerManager.shared.selectedControllers, value: $0) }
            .isEmpty
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
            
            ForEach(Array(controllerManager.selectedControllers.enumerated()), id: \.offset) { index, id in
                ControllerRow(
                    index: index,
                    controllerId: id,
                    controllerManager: controllerManager
                )
            }
            .onAppear {
                setupControllerTypes()
            }
        }
    }
    
    private var addControllerButton: some View {
        Menu {
            ForEach(controllerManager.allControllers.filter({ !contains(controllerManager.selectedControllers, value: $0) })) { controller in
                Button {
                    controllerManager.selectedControllers.append(controller.id)
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
    
    private func setupControllerTypes() {
        for (index, controllerId) in ControllerManager.shared.selectedControllers.enumerated() {
            let (controller, idx) = controllerManager.controllerAndIndexForString(controllerId)!
            let defaultType: ControllerType = controller.virtual ? .joyconPair : .proController
            ControllerManager.shared.allControllers[idx].type = ControllerManager.shared.controllerTypes[index] ?? defaultType
        }
    }
    
    func contains(_ array: [String], value: BaseController) -> Bool {
        array.contains { $0 == value.id }
    }
    
    // MARK: - System Settings
    
    private var systemSettings: some View {
        SettingsSection(title: "System Configuration") {
            languageRegionCard
            cpuConfigCard
        }
    }
    
    private var languageRegionCard: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 16) {
                VStack(alignment: .leading, spacing: 8) {
                    labelWithIcon("System Language", iconName: "character.bubble")
                        .font(.headline)
                    
                    pickerView(
                        selection: config.language,
                        options: SystemLanguage.self,
                        displayName: \.displayName
                    )
                }
                
                Divider()
                
                VStack(alignment: .leading, spacing: 8) {
                    labelWithIcon("Region", iconName: "globe")
                        .font(.headline)
                    
                    pickerView(
                        selection: config.regioncode,
                        options: SystemRegionCode.self,
                        displayName: \.displayName
                    )
                }
            }
        }
    }
    
    private var cpuConfigCard: some View {
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
    
    // MARK: - Advanced Settings
    
    private var advancedSettings: some View {
        SettingsSection(title: "Advanced Options") {
            debugOptionsCard
            advancedTogglesCard
            memoryHacksCard
            additionalArgsCard
            systemInfoCard
            memorialCard
        }
    }
    
    private var debugOptionsCard: some View {
        SettingsCard {
            VStack(spacing: 4) {
                SettingsToggle(isOn: nativeSettingsManager.showlogsloading.projectedValue, icon: "text.alignleft", label: "Show Logs While Loading")
                Divider()
                SettingsToggle(isOn: nativeSettingsManager.showlogsgame.projectedValue, icon: "text.magnifyingglass", label: "Show Logs In-Game")
                Divider()
                SettingsToggle(isOn: nativeSettingsManager.showFullLogs.projectedValue, icon: "waveform.path", label: "Show Full Logs")
                Divider()
                SettingsToggle(isOn: config.debuglogs, icon: "exclamationmark.bubble", label: "Debug Logs")
                Divider()
                SettingsToggle(isOn: config.tracelogs, icon: "waveform.path", label: "Trace Logs")
            }
        }
    }
    
    private var advancedTogglesCard: some View {
        SettingsCard {
            VStack(spacing: 4) {
                SettingsToggle(isOn: nativeSettingsManager.runOnMainThread.projectedValue, icon: "square.stack.3d.up", label: "Run Core on Main Thread")
                Divider()
                SettingsToggle(isOn: config.dfsIntegrityChecks, icon: "checkmark.shield", label: "Disable FS Integrity Checks")
                Divider()
                
                if MTLHud.shared.canMetalHud {
                    SettingsToggle(isOn: $metalHudEnabler.metalHudEnabled, icon: "speedometer", label: "Metal Performance HUD")
                    Divider()
                }
                
                SettingsToggle(isOn: nativeSettingsManager.ignoreJIT.projectedValue, icon: "cpu", label: "Ignore JIT Popup")
                Divider()
                
                Button {
                    nativeSettingsManager.hasBeenFinished.value = true
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
    }
    
    private var memoryHacksCard: some View {
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
    }
    
    private var additionalArgsCard: some View {
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
    }
    
    private var systemInfoCard: some View {
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
    }
    
    private var memorialCard: some View {
        SettingsCard {
            if gamepo {
                Text("The cake is a lie")
                    .font(.system(.body, design: .monospaced))
                    .foregroundColor(.secondary)
                    .frame(maxWidth: .infinity, alignment: .center)
                Divider()
            }
            
            HStack {
                Text("In memoriam of 'Lily'")
                    .font(.system(.body, design: .monospaced))
                    .foregroundColor(.secondary)
                Image(systemName: "heart")
                    .font(.system(.body, design: .monospaced))
                    .foregroundColor(.purple)
            }
            .frame(maxWidth: .infinity, alignment: .center)
        }
    }
    
    // MARK: - Miscellaneous Settings
    
    private var miscSettings: some View {
        SettingsSection(title: "Miscellaneous Options") {
            customRomFoldersCard
            networkConfigCard
            uiTogglesCard
            jitAndMiscCard
        }
    }
    
    private var customRomFoldersCard: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 16) {
                Text("Custom ROM folders")
                    .font(.headline)
                    .foregroundColor(.primary)
                FolderListView()
            }
        }
    }
    
    private var networkConfigCard: some View {
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
            }
        }
    }
    
    private var uiTogglesCard: some View {
        SettingsCard {
            VStack(spacing: 4) {
                if UIDevice.current.userInterfaceIdiom == .pad {
                    SettingsToggle(isOn: nativeSettingsManager.toggleGreen.projectedValue, icon: "arrow.clockwise", label: "Toggle Color Green when \"ON\"")
                    Divider()
                }
                
                SettingsToggle(isOn: nativeSettingsManager.disableTouch.projectedValue, icon: "rectangle.and.hand.point.up.left.filled", label: "Disable Touch")
                Divider()
                SettingsToggle(isOn: nativeSettingsManager.enableGridLayout(true).projectedValue, icon: "rectangle.portrait", label: "Games List Grid")
                Divider()
                SettingsToggle(isOn: nativeSettingsManager.showProfileonGame.projectedValue, icon: "person.3", label: "Select Profile on Game Launch")
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
                .padding(.horizontal)
                .sheet(isPresented: $showAppIconSwitcher) {
                    AppIconSwitcherView()
                }
                
                Divider()
                SettingsToggle(isOn: nativeSettingsManager.showScreenShotButton.projectedValue, icon: "arrow.left.circle", label: "Menu Button (in-game)")
                Divider()
                SettingsToggle(isOn: nativeSettingsManager.setting(forKey: "location-enabled", default: false).projectedValue, icon: "location.viewfinder", label: "Keep app in background")
                
                if UIDevice.current.userInterfaceIdiom == .pad {
                    Divider()
                    SettingsToggle(isOn: nativeSettingsManager.oldSettingsUI.projectedValue, icon: "ipad.landscape", label: "Non Switch-like Settings")
                }
            }
        }
    }
    
    private var jitAndMiscCard: some View {
        SettingsCard {
            VStack(spacing: 4) {
                jitToggleView
                Divider()
                
                SettingsToggle(isOn: nativeSettingsManager.setting(forKey: "MVK_CONFIG_SYNCHRONOUS_QUEUE_SUBMITS", default: true).projectedValue, icon: "line.diagonal", label: "MVK: Synchronous Queue Submits")
                    .contextMenu {
                        Button {
                            showAlert(
                                title: "About MVK: Synchronous Queue Submits",
                                message: "Enable this option if Mario Kart 8 is crashing at Grand Prix mode."
                            )
                        } label: {
                            Text("About")
                        }
                    }
                
                Divider()
                
                if !ProcessInfo.processInfo.isiOSAppOnMac {
                    if #available(iOS 19, *) {
                        SettingsToggle(isOn: .constant(true), icon: "light.strip.2", label: "Dual Mapped JIT")
                            .disabled(true)
                    } else {
                        SettingsToggle(isOn: nativeSettingsManager.setting(forKey: "DUAL_MAPPED_JIT", default: false).projectedValue, icon: "light.strip.2", label: "Dual Mapped JIT")
                            .disabled(ProcessInfo.processInfo.hasTXM)
                    }
                } else {
                    SettingsToggle(isOn: nativeSettingsManager.setting(forKey: "DUAL_MAPPED_JIT", default: false).projectedValue, icon: "light.strip.2", label: "Dual Mapped JIT")
                }
                
                Divider()
                SettingsToggle(isOn: nativeSettingsManager.checkForUpdate(true).projectedValue, icon: "square.and.arrow.down", label: "Check for Updates")
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
                .padding(.horizontal)
            }
        }
    }
    
    private var jitToggleView: some View {
        Group {
            if #available(iOS 17.0.1, *) {
                let checked = stikJITorStikDebug()
                let stikJIT = checked == 1 ? "StikDebug" : checked == 2 ? "StikJIT" : "StikDebug"
                
                SettingsToggle(isOn: $stikJIT, icon: "bolt.heart", label: stikJIT)
                    .contextMenu {
                        Button {
                            showAlert(
                                title: "About \(stikJIT)",
                                message: "\(stikJIT) is a really amazing iOS Application to Enable JIT on the go on-device, made by the best, most kind, helpful and nice developers of all time jkcoxson and Blu <3",
                                learnMoreURL: "https://github.com/StephenDev0/StikJIT"
                            )
                        } label: {
                            Text("About")
                        }
                    }
            } else {
                SettingsToggle(isOn: $useTrollStore, icon: "troll.svg", label: "TrollStore JIT")
            }
        }
    }
    
    
    @ViewBuilder
    private func labelWithIcon(_ text: String, iconName: String, flipimage: Bool? = nil) -> some View {
        HStack(spacing: 8) {
            if iconName.hasSuffix(".svg") {
                if let flipimage, flipimage {
                    SVGView(svgName: iconName, color: .blue)
                        .frame(width: 20, height: 20)
                        .rotation3DEffect(.degrees(180), axis: (x: 0, y: 1, z: 0))
                } else {
                    SVGView(svgName: iconName, color: .blue)
                        .frame(width: 20, height: 20)
                }
            } else if !iconName.isEmpty {
                Image(systemName: iconName)
                    .foregroundColor(.blue)
            }
            Text(text)
        }
        .font(.body)
    }
    
    private func infoButton(title: String, message: String, isPresented: Binding<Bool>) -> some View {
        Button {
            isPresented.wrappedValue.toggle()
        } label: {
            Image(systemName: "info.circle")
                .foregroundColor(.secondary)
        }
        .buttonStyle(.plain)
        .alert(isPresented: isPresented) {
            Alert(
                title: Text(title),
                message: Text(message),
                dismissButton: .default(Text("OK"))
            )
        }
    }
    
    private func sliderWithLabels(
        value: Binding<Double>,
        range: ClosedRange<Double>,
        step: Double,
        currentValue: Double,
        minLabel: String,
        maxLabel: String,
        format: String = "%.2f"
    ) -> some View {
        VStack(spacing: 8) {
            Slider(value: value, in: range, step: step)
            
            HStack {
                Text(minLabel)
                    .font(.caption2)
                    .foregroundColor(.secondary)
                Spacer()
                Text("\(String(format: format, currentValue))x")
                    .font(.headline)
                    .foregroundColor(.blue)
                Spacer()
                Text(maxLabel)
                    .font(.caption2)
                    .foregroundColor(.secondary)
            }
        }
    }
    
    private func pickerView<T: Hashable & CaseIterable>(
        selection: Binding<T>,
        options: T.Type,
        displayName: KeyPath<T, String>
    ) -> some View {
        Picker(selection: selection) {
            ForEach(Array(options.allCases), id: \.self) { option in
                Text(option[keyPath: displayName]).tag(option)
            }
        } label: {
            EmptyView()
        }
        .pickerStyle(.menu)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.vertical, 4)
    }
    
    
    private func loadSettings() {
        if let _ = SettingsManager.loadSettings() {
            settingsManager.loadSettings()
        } else {
            settingsManager.saveSettings()
        }
    }
    
    private func getGPUInfo() -> String? {
        MTLCreateSystemDefaultDevice()?.name
    }
    
    private func showAlert(title: String, message: String, learnMoreURL: String? = nil) {
        guard let windowScene = UIApplication.shared.connectedScenes.first as? UIWindowScene,
              let mainWindow = windowScene.windows.last else { return }
        
        let alertController = UIAlertController(title: title, message: message, preferredStyle: .alert)
        
        if let urlString = learnMoreURL, let url = URL(string: urlString) {
            let learnMoreButton = UIAlertAction(title: "Learn More", style: .default) { _ in
                UIApplication.shared.open(url)
            }
            alertController.addAction(learnMoreButton)
        }
        
        let doneButton = UIAlertAction(title: learnMoreURL != nil ? "Done" : "OK", style: .cancel)
        alertController.addAction(doneButton)
        
        mainWindow.rootViewController?.present(alertController, animated: true)
    }
}


extension View {
    @ViewBuilder
    func `if`<Content: View>(_ condition: Bool, transform: (Self) -> Content) -> some View {
        if condition {
            transform(self)
        } else {
            self
        }
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
    } catch {
    }
}

func loadSettings() -> Ryujinx.Arguments? {
    do {
        let fileURL = URL.documentsDirectory.appendingPathComponent("config.json")
        
        guard FileManager.default.fileExists(atPath: fileURL.path) else {
            saveSettings(config: Ryujinx.Arguments())
            if FileManager.default.fileExists(atPath: fileURL.path) {
                return loadSettings()
            }
            
            return nil
        }
        
        let data = try Data(contentsOf: fileURL)
        
        let decoder = JSONDecoder()
        let configs = try decoder.decode(Ryujinx.Arguments.self, from: data)
        return configs
    } catch {
        return nil
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
// this became perm. whoops
struct FolderListView: View {
    @StateObject private var folderManager = ROMFolderManager.shared
    
    var body: some View {
        VStack(spacing: 0) {
            Divider()
            
            ForEach(folderManager.bookmarks, id: \.self) { path in
                VStack(spacing: 0) {
                    HStack {
                        Image(systemName: "folder.fill")
                            .foregroundColor(.blue)
                        
                        Text(folderManager.getUrl(from: path)?.lastPathComponent ?? "Unknown")
                            .lineLimit(1)
                            .truncationMode(.middle)
                        
                        Spacer()
                        
                        Button {
                            folderManager.bookmarks.removeAll(where: { $0 == path })
                        } label: {
                            Image(systemName: "xmark.circle.fill")
                                .foregroundColor(.secondary)
                        }
                        .buttonStyle(.plain)
                    }
                    .padding(.vertical, 8)
                    
                    if path != folderManager.bookmarks.last {
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
