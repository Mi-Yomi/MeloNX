//
//  ControllerView.swift
//  Pomelo-V2
//
//  Created by Stossy11 on 16/7/2024.
//

import SwiftUI
import GameController
import CoreMotion

// MARK: - Main Controller View

struct LayoutView: View {
    @EnvironmentObject var gameHandler: LaunchGameHandler
    @Environment(\.verticalSizeClass) var verticalSizeClass
    
    var body: some View {
        Text("")
            .onChange(of: verticalSizeClass) { _ in updateOrientation() }
            .onAppear {
                updateOrientation()
            }
    }
    private func updateOrientation() {
        if let windowScene = UIApplication.shared.connectedScenes.first as? UIWindowScene,
           let window = windowScene.windows.first {
            gameHandler.isPortrait = window.bounds.size.height > window.bounds.size.width
        }
    }
}

struct ControllerView: View {
    @AppStorage("On-ScreenControllerScale") private var controllerScale: Double = 1.0
    @AppStorage("stickButton") private var stickButton = false
    @EnvironmentObject var gameHandler: LaunchGameHandler
    @State private var hideDpad = false
    @State private var hideABXY = false
    @Binding var isEditing: Bool
    @State private var selectedButton: String?
    @State private var selectedJoystick: String?
    @Environment(\.verticalSizeClass) var verticalSizeClass
    @State private var showEditControls = true
    
    // Game-specific layout support
    var gameId: String?
    @State private var layout: LayoutConfig = LayoutConfig()
    @State private var showingLayoutOptions = false
    
    var body: some View {
        ZStack {
            Group {
                let isPad = UIDevice.current.userInterfaceIdiom == .pad
                if gameHandler.isPortrait && !isPad {
                    portraitLayout
                } else {
                    landscapeLayout
                }
            }
            .padding()
            .onChange(of: verticalSizeClass) { _ in updateOrientation() }
            .onAppear {
                updateOrientation()
                loadLayout()
            }
            .onChange(of: gameId) { _ in
                loadLayout()
            }

            // Edit Controls
            if isEditing {

                
                if showEditControls {
                    editControls
                        .transition(.move(edge: .top).combined(with: .opacity))
                        .zIndex(1)
                } else {
                    VStack {
                        HStack {
                            Button(action: {
                                withAnimation(.easeInOut(duration: 0.3)) {
                                    showEditControls = true
                                }
                            }) {
                                Image(systemName: showEditControls ? "eye.slash" : "eye")
                                    .padding(12)
                                    .background(.ultraThinMaterial)
                                    .clipShape(Circle())
                            }
                            
                            Spacer()
                        }
                     
                        Spacer()
                    }
                }

            }
        }
    }
    
    private func loadLayout() {
        layout = LayoutManager.shared.load(for: gameId)
        
        // Migration: Convert legacy layout format
        let legacyLayout = LayoutManager.shared.loadLegacy(for: gameId)
        if !legacyLayout.isEmpty && layout.buttons.isEmpty {
            layout.buttons = legacyLayout
            LayoutManager.shared.save(layout, for: gameId)
        }
    }
    
    private func saveLayout() {
        LayoutManager.shared.save(layout, for: gameId)
    }

    // MARK: - Edit Controls
    
    private var editControls: some View {
        VStack {
            HStack {
                Button("Hide") {
                    withAnimation(.easeInOut(duration: 0.3)) {
                        showEditControls = false
                    }
                }
                .foregroundColor(.red)
                
                Button("Layout Options") {
                    showingLayoutOptions = true
                }
                .foregroundColor(.blue)
                
                Spacer()
                
                Button("Reset Current") {
                    layout = LayoutConfig()
                    LayoutManager.shared.reset(for: gameId)
                    selectedButton = nil
                    selectedJoystick = nil
                }
                .foregroundColor(.red)
                
                Spacer()
                
                if let selectedButton = selectedButton {
                    Button("Reset Selected") {
                        layout.buttons[selectedButton] = nil
                        self.selectedButton = nil
                    }
                    .foregroundColor(.orange)
                    .padding(.horizontal)
                } else if let selectedJoystick = selectedJoystick {
                    Button("Reset Selected") {
                        layout.joysticks[selectedJoystick] = nil
                        self.selectedJoystick = nil
                    }
                    .foregroundColor(.orange)
                    .padding(.horizontal)
                }
                
                Button(isEditing ? "Done" : "Edit") {
                    if isEditing {
                        saveLayout()
                        selectedButton = nil
                        selectedJoystick = nil
                    }
                    isEditing.toggle()
                }
                .padding(.horizontal)
            }
            .padding()
            .background(.ultraThinMaterial)
            .cornerRadius(12)
            
            // Game indicator
            if let gameId = gameId {
                HStack {
                    Image(systemName: "gamecontroller.fill")
                        .foregroundColor(.blue)
                        .padding(.vertical)
                    Text("Game: \(gameId)")
                        .font(.caption)
                        .foregroundColor(.secondary)
                        .padding(.vertical)
                    if LayoutManager.shared.hasCustomLayout(for: gameId) {
                        Image(systemName: "checkmark.circle.fill")
                            .foregroundColor(.green)
                            .font(.caption)
                    }
                    Spacer()
                }
                .padding(.horizontal)
                .padding(.top, 4)
                .background(.ultraThinMaterial)
                .cornerRadius(8)
            }
            
            if let selectedButton = selectedButton {
                buttonScaleControls(for: selectedButton)
            } else if let selectedJoystick = selectedJoystick {
                joystickScaleControls(for: selectedJoystick)
            }
            
            Spacer()
        }
        .padding()
        .sheet(isPresented: $showingLayoutOptions) {
            LayoutOptionsView(gameId: gameId, layout: $layout)
        }
    }
    
    private func buttonScaleControls(for buttonId: String) -> some View {
        VStack {
            Text("Button Scale: \(String(format: "%.1f", layout.buttons[buttonId]?.scale ?? 1.0))")
                .font(.headline)
            
            HStack {
                Button("-") {
                    let currentScale = layout.buttons[buttonId]?.scale ?? 1.0
                    layout.buttons[buttonId, default: ButtonLayout()].scale = max(0.5, currentScale - 0.1)
                }
                .font(.title2)
                .frame(width: 40, height: 40)
                .background(Color.blue)
                .foregroundColor(.white)
                .cornerRadius(8)
                
                Slider(
                    value: Binding(
                        get: { layout.buttons[buttonId]?.scale ?? 1.0 },
                        set: { layout.buttons[buttonId, default: ButtonLayout()].scale = $0 }
                    ),
                    in: 0.5...2.0,
                    step: 0.1
                )
                
                Button("+") {
                    let currentScale = layout.buttons[buttonId]?.scale ?? 1.0
                    layout.buttons[buttonId, default: ButtonLayout()].scale = min(2.0, currentScale + 0.1)
                }
                .font(.title2)
                .frame(width: 40, height: 40)
                .background(Color.blue)
                .foregroundColor(.white)
                .cornerRadius(8)
            }
            
            Toggle(isOn: Binding(get: { layout.buttons[buttonId]?.hidden ?? false }, set: { layout.buttons[buttonId, default: ButtonLayout()].hidden = $0 })) {
                Text("Hide Button")
            }
            .accentColor(.blue)
            
            Toggle(isOn: Binding(get: { layout.buttons[buttonId]?.toggle ?? false }, set: { layout.buttons[buttonId, default: ButtonLayout()].toggle = $0 })) {
                Text("Make Button Toggle")
            }
            .accentColor(.blue)
        }
        .padding()
        .background(.ultraThinMaterial)
        .cornerRadius(12)
    }
    
    private func joystickScaleControls(for joystickId: String) -> some View {
        VStack {
            Text("Joystick Scale: \(String(format: "%.1f", layout.joysticks[joystickId]?.scale ?? 1.0))")
                .font(.headline)
            
            HStack {
                Button("-") {
                    let currentScale = layout.joysticks[joystickId]?.scale ?? 1.0
                    layout.joysticks[joystickId, default: JoystickLayout()].scale = max(0.5, currentScale - 0.1)
                }
                .font(.title2)
                .frame(width: 40, height: 40)
                .background(Color.green)
                .foregroundColor(.white)
                .cornerRadius(8)
                
                Slider(
                    value: Binding(
                        get: { layout.joysticks[joystickId]?.scale ?? 1.0 },
                        set: { layout.joysticks[joystickId, default: JoystickLayout()].scale = $0 }
                    ),
                    in: 0.5...2.0,
                    step: 0.1
                )
                
                Button("+") {
                    let currentScale = layout.joysticks[joystickId]?.scale ?? 1.0
                    layout.joysticks[joystickId, default: JoystickLayout()].scale = min(2.0, currentScale + 0.1)
                }
                .font(.title2)
                .frame(width: 40, height: 40)
                .background(Color.green)
                .foregroundColor(.white)
                .cornerRadius(8)
            }
            
            Toggle(isOn: Binding(get: { layout.joysticks[joystickId]?.hidden ?? false }, set: { layout.joysticks[joystickId, default: JoystickLayout()].hide = $0 })) {
                Text("Hide Joystick")
            }
            .accentColor(.green)
            
            Toggle(isOn: Binding(get: { layout.joysticks[joystickId]?.hide ?? true }, set: { layout.joysticks[joystickId, default: JoystickLayout()].hide = $0 })) {
                Text("Hide ABXY / Arrow Buttons")
            }
            .accentColor(.green)
            
            Toggle(isOn: Binding(get: { layout.joysticks[joystickId]?.background ?? false }, set: { layout.joysticks[joystickId, default: JoystickLayout()].background = $0 })) {
                Text("Always show Joystick Background")
            }
            .accentColor(.green)
        }
        .padding()
        .background(.ultraThinMaterial)
        .cornerRadius(12)
    }

    // MARK: - Layout Views
    
    private var portraitLayout: some View {
        VStack {
            Spacer()
            VStack(spacing: 20) {
                HStack(spacing: 30) {
                    VStack(spacing: 15) {
                        shoulderButtonsLeft
                        ZStack {
                            editableJoystick(id: "leftJoystick", showBackground: $hideDpad)
                            
                            if layout.joysticks["leftJoystick"]?.hide ?? true {
                                dpadView
                                    .opacity(hideDpad ? 0 : 1)
                                    .allowsHitTesting(!hideDpad)
                                    .animation(.easeInOut(duration: 0.2), value: hideDpad)
                            } else {
                                dpadView
                            }
                        }
                    }
                    
                    VStack(spacing: 15) {
                        shoulderButtonsRight
                        ZStack {
                            editableJoystick(id: "rightJoystick", iscool: true, showBackground: $hideABXY)
                            if layout.joysticks["rightJoystick"]?.hide ?? true {
                                abxyView
                                    .opacity(hideABXY ? 0 : 1)
                                    .allowsHitTesting(!hideABXY)
                                    .animation(.easeInOut(duration: 0.2), value: hideABXY)
                            } else {
                                abxyView
                            }
                        }
                    }
                }
                
                HStack(spacing: 60) {
                    HStack {
                        editableButton(.leftStick).padding()
                        editableButton(.back)
                    }
                    HStack {
                        editableButton(.start)
                        editableButton(.rightStick).padding()
                    }
                }
            }
        }
    }
    
    private var landscapeLayout: some View {
        VStack {
            Spacer()
            HStack {
                VStack(spacing: 20) {
                    shoulderButtonsLeft
                    ZStack {
                        editableJoystick(id: "leftJoystick", showBackground: $hideDpad)
                        
                        if layout.joysticks["leftJoystick"]?.hide ?? true {
                            dpadView
                                .opacity(hideDpad ? 0 : 1)
                                .allowsHitTesting(!hideDpad)
                                .animation(.easeInOut(duration: 0.2), value: hideDpad)
                        } else {
                            dpadView
                        }
                    }
                }
                
                Spacer()
                centerButtons
                Spacer()
                
                VStack(spacing: 20) {
                    shoulderButtonsRight
                    ZStack {
                        editableJoystick(id: "rightJoystick", iscool: true, showBackground: $hideABXY)
                        if layout.joysticks["rightJoystick"]?.hide ?? true {
                            abxyView
                                .opacity(hideABXY ? 0 : 1)
                                .allowsHitTesting(!hideABXY)
                                .animation(.easeInOut(duration: 0.2), value: hideABXY)
                        } else {
                            abxyView
                        }
                    }
                }
            }
        }
    }
    
    private var centerButtons: some View {
        Group {
            if stickButton {
                VStack {
                    HStack(spacing: 50) {
                        editableButton(.leftStick).padding()
                        Spacer()
                        editableButton(.rightStick).padding()
                    }
                    .padding(.top, 30)
                    
                    HStack(spacing: 50) {
                        editableButton(.back)
                        Spacer()
                        editableButton(.start)
                    }
                }
                .padding(.bottom, 20)
            } else {
                HStack(spacing: 50) {
                    editableButton(.back)
                    Spacer()
                    editableButton(.start)
                }
                .padding(.bottom, 20)
            }
        }
    }

    // MARK: - Button Groups
    
    private var shoulderButtonsLeft: some View {
        HStack(spacing: 20) {
            editableButton(.leftTrigger)
            editableButton(.leftShoulder)
        }
        .frame(width: 160 * CGFloat(controllerScale), height: 20 * CGFloat(controllerScale))
    }
    
    private var shoulderButtonsRight: some View {
        HStack(spacing: 20) {
            editableButton(.rightShoulder)
            editableButton(.rightTrigger)
        }
        .frame(width: 160 * CGFloat(controllerScale), height: 20 * CGFloat(controllerScale))
    }
    
    private var dpadView: some View {
        VStack(spacing: 7) {
            editableButton(.dPadUp)
            HStack(spacing: 22) {
                editableButton(.dPadLeft)
                Spacer(minLength: 22)
                editableButton(.dPadRight)
            }
            editableButton(.dPadDown)
        }
        .frame(width: 145 * CGFloat(controllerScale), height: 145 * CGFloat(controllerScale))
    }
    
    private var abxyView: some View {
        VStack(spacing: 7) {
            editableButton(.X)
            HStack(spacing: 22) {
                editableButton(.Y)
                Spacer(minLength: 22)
                editableButton(.A)
            }
            editableButton(.B)
        }
        .frame(width: 145 * CGFloat(controllerScale), height: 145 * CGFloat(controllerScale))
    }

    // MARK: - Helper Methods
    
    private func editableButton(_ button: VirtualControllerButton) -> some View {
        EditableButtonView(
            button: button,
            layout: $layout,
            isEditing: isEditing,
            selectedButton: $selectedButton,
            selectedJoystick: $selectedJoystick
        )
    }
    
    private func editableJoystick(
        id: String,
        iscool: Bool = false,
        showBackground: Binding<Bool>,
    ) -> some View {
        EditableJoystickView(
            id: id,
            iscool: iscool,
            showBackground: showBackground,
            layout: $layout,
            isEditing: isEditing,
            selectedJoystick: $selectedJoystick,
            selectedButton: $selectedButton,
        )
    }

    private func updateOrientation() {
        if let windowScene = UIApplication.shared.connectedScenes.first as? UIWindowScene,
           let window = windowScene.windows.first {
            gameHandler.isPortrait = window.bounds.size.height > window.bounds.size.width
        }
    }
}

// MARK: - Editable Joystick View

struct EditableJoystickView: View {
    let id: String
    let iscool: Bool
    @Binding var showBackground: Bool
    @Binding var layout: LayoutConfig
    var isEditing: Bool
    @Binding var selectedJoystick: String?
    @Binding var selectedButton: String?
    @GestureState private var dragOffset = CGSize.zero
    @AppStorage("On-ScreenControllerScale") var controllerScale: Double = 1.0
    
    var body: some View {
        ZStack {
            if isEditing {
                Circle()
                    .fill(Color.gray.opacity(0.3))
                    .frame(width: 160, height: 160)
                    .overlay(
                        Text("Joystick")
                            .font(.caption)
                            .foregroundColor(.white)
                    )
                    .scaleEffect((layout.joysticks[id]?.scale ?? 1.0) * controllerScale)
                    .border(selectedJoystick == id ? Color.green : Color.clear, width: 3)
                    .offset(
                        x: (layout.joysticks[id]?.offset.width ?? 0) + dragOffset.width,
                        y: (layout.joysticks[id]?.offset.height ?? 0) + dragOffset.height
                    )
                    .onTapGesture {
                        selectedJoystick = selectedJoystick == id ? nil : id
                        selectedButton = nil
                    }
                    .gesture(
                        DragGesture()
                            .updating($dragOffset) { value, state, _ in
                                state = value.translation
                                selectedJoystick = id
                                selectedButton = nil
                            }
                            .onEnded { value in
                                layout.joysticks[id, default: JoystickLayout()].offset.width += value.translation.width
                                layout.joysticks[id, default: JoystickLayout()].offset.height += value.translation.height
                            }
                    )
            } else {
                if layout.joysticks[id]?.background ?? false {
                    JoystickController(iscool: iscool, showBackground: .constant(true))
                        .scaleEffect(layout.joysticks[id]?.scale ?? 1.0)
                        .offset(layout.joysticks[id]?.offset ?? .zero)
                } else {
                    JoystickController(iscool: iscool, showBackground: $showBackground)
                        .scaleEffect(layout.joysticks[id]?.scale ?? 1.0)
                        .offset(layout.joysticks[id]?.offset ?? .zero)
                }
            }
        }
    }
}

// MARK: - Layout Options View

struct LayoutOptionsView: View {
    let gameId: String?
    @Binding var layout: LayoutConfig
    @Environment(\.presentationMode) var presentationMode
    @State private var showingResetAlert = false
    @State private var showingCopySheet = false
    
    var body: some View {
        NavigationView {
            VStack(spacing: 20) {
                if let gameId = gameId {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Current Game")
                            .font(.headline)
                        HStack {
                            Image(systemName: "gamecontroller.fill")
                                .foregroundColor(.blue)
                            Text(gameId)
                                .font(.subheadline)
                            Spacer()
                            if LayoutManager.shared.hasCustomLayout(for: gameId) {
                                HStack {
                                    Image(systemName: "checkmark.circle.fill")
                                        .foregroundColor(.green)
                                    Text("Custom Layout")
                                        .font(.caption)
                                        .foregroundColor(.green)
                                }
                            } else {
                                Text("Using Default")
                                    .font(.caption)
                                    .foregroundColor(.secondary)
                            }
                        }
                        .padding()
                        .background(Color.gray.opacity(0.1))
                        .cornerRadius(8)
                    }
                }
                
                VStack(alignment: .leading, spacing: 12) {
                    Text("Layout Actions")
                        .font(.headline)
                    
                    Button(action: {
                        showingCopySheet = true
                    }) {
                        HStack {
                            Image(systemName: "doc.on.doc")
                            Text("Copy Layout From...")
                            Spacer()
                            Image(systemName: "chevron.right")
                        }
                        .padding()
                        .background(Color.blue.opacity(0.1))
                        .foregroundColor(.blue)
                        .cornerRadius(8)
                    }
                    
                    Button(action: {
                        layout = LayoutManager.shared.load(for: nil)
                    }) {
                        HStack {
                            Image(systemName: "arrow.clockwise")
                            Text("Reset to Default Layout")
                            Spacer()
                            Image(systemName: "chevron.right")
                        }
                        .padding()
                        .background(Color.orange.opacity(0.1))
                        .foregroundColor(.orange)
                        .cornerRadius(8)
                    }
                    
                    Button(action: {
                        showingResetAlert = true
                    }) {
                        HStack {
                            Image(systemName: "trash")
                            Text("Delete Custom Layout")
                            Spacer()
                            Image(systemName: "chevron.right")
                        }
                        .padding()
                        .background(Color.red.opacity(0.1))
                        .foregroundColor(.red)
                        .cornerRadius(8)
                    }
                }
                
                Spacer()
            }
            .padding()
            .navigationTitle("Layout Options")
            .navigationBarTitleDisplayMode(.inline)
            .navigationBarItems(trailing: Button("Done") {
                presentationMode.wrappedValue.dismiss()
            })
        }
        .alert("Delete Custom Layout", isPresented: $showingResetAlert) {
            Button("Cancel", role: .cancel) { }
            Button("Delete", role: .destructive) {
                LayoutManager.shared.reset(for: gameId)
                layout = LayoutManager.shared.load(for: gameId)
            }
        } message: {
            Text("This will delete the custom layout for this game and revert to the default layout.")
        }
        .sheet(isPresented: $showingCopySheet) {
            CopyLayoutView(targetGameId: gameId, layout: $layout)
        }
    }
}

// MARK: - Copy Layout View

struct CopyLayoutView: View {
    let targetGameId: String?
    @Binding var layout: LayoutConfig
    @Environment(\.presentationMode) var presentationMode
    @State private var availableLayouts: [String] = []
    
    var body: some View {
        NavigationView {
            List {
                Section(header: Text("Available Layouts")) {
                    Button(action: {
                        copyLayout(from: nil)
                    }) {
                        HStack {
                            Image(systemName: "gear")
                                .foregroundColor(.blue)
                            Text("Default Layout")
                            Spacer()
                            Image(systemName: "chevron.right")
                                .foregroundColor(.gray)
                        }
                    }
                    
                    ForEach(availableLayouts, id: \.self) { gameId in
                        if gameId != targetGameId {
                            Button(action: {
                                copyLayout(from: gameId)
                            }) {
                                HStack {
                                    Image(systemName: "gamecontroller.fill")
                                        .foregroundColor(.green)
                                    Text(gameId)
                                    Spacer()
                                    Image(systemName: "chevron.right")
                                        .foregroundColor(.gray)
                                }
                            }
                        }
                    }
                }
            }
            .navigationTitle("Copy Layout")
            .navigationBarTitleDisplayMode(.inline)
            .navigationBarItems(
                leading: Button("Cancel") {
                    presentationMode.wrappedValue.dismiss()
                }
            )
        }
        .onAppear {
            loadAvailableLayouts()
        }
    }
    
    private func loadAvailableLayouts() {
        availableLayouts = LayoutManager.shared.getAllGameLayouts()
    }
    
    private func copyLayout(from sourceGameId: String?) {
        let sourceLayout = LayoutManager.shared.load(for: sourceGameId)
        layout = sourceLayout
        LayoutManager.shared.save(layout, for: targetGameId)
        presentationMode.wrappedValue.dismiss()
    }
}

// MARK: - Editable Button View

struct EditableButtonView: View {
    let button: VirtualControllerButton
    @Binding var layout: LayoutConfig
    var isEditing: Bool
    @Binding var selectedButton: String?
    @Binding var selectedJoystick: String?
    @GestureState private var dragOffset = CGSize.zero

    var body: some View {
        Group {
            if isEditing {
                ExtButtonIconView(button: button)
                    .scaleEffect(layout.buttons[button.id]?.scale ?? 1.0)
                    .border(selectedButton == button.id ? Color.blue : Color.clear, width: 3)
                    .offset(
                        x: (layout.buttons[button.id]?.offset.width ?? 0) + dragOffset.width,
                        y: (layout.buttons[button.id]?.offset.height ?? 0) + dragOffset.height
                    )
                    .onTapGesture {
                        selectedButton = selectedButton == button.id ? nil : button.id
                        selectedJoystick = nil
                    }
                    .gesture(
                        DragGesture()
                            .updating($dragOffset) { value, state, _ in
                                state = value.translation
                                selectedButton = button.id
                                selectedJoystick = nil
                            }
                            .onEnded { value in
                                layout.buttons[button.id, default: ButtonLayout()].offset.width += value.translation.width
                                layout.buttons[button.id, default: ButtonLayout()].offset.height += value.translation.height
                            }
                    )
            } else {
                if layout.buttons[button.id, default: ButtonLayout()].hidden {
                    ButtonView(button: button, layout: $layout)
                        .scaleEffect(layout.buttons[button.id]?.scale ?? 1.0)
                        .offset(layout.buttons[button.id]?.offset ?? .zero)
                        .opacity(0)
                } else {
                    ButtonView(button: button, layout: $layout)
                        .scaleEffect(layout.buttons[button.id]?.scale ?? 1.0)
                        .offset(layout.buttons[button.id]?.offset ?? .zero)
                }
            }
        }
    }
}


// MARK: - Supporting Views (Simplified ButtonView and ExtButtonIconView)

struct ButtonView: View {
    var button: VirtualControllerButton
    @Binding var layout: LayoutConfig
    
    @AppStorage("onscreenhandheld") var onscreenjoy: Bool = false
    @AppStorage("On-ScreenControllerScale") var controllerScale: Double = 1.0
    @Environment(\.presentationMode) var presentationMode
    
    @State private var istoggle = false
    @State private var isPressed = false
    @State private var toggleState = false
    @State private var size: CGSize = .zero
    
    var body: some View {
        Circle()
            .foregroundStyle(.clear.opacity(0))
            .overlay {
                Image(systemName: buttonConfig.iconName)
                    .resizable()
                    .scaledToFit()
                    .frame(width: size.width, height: size.height)
                    .foregroundStyle(.white)
                    .opacity(isPressed ? 0.6 : 0.8)
                    .allowsHitTesting(false)
            }
            .frame(width: size.width, height: size.height)
            .background(buttonBackground)
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { _ in handleButtonPress() }
                    .onEnded { _ in handleButtonRelease() }
            )
            .onAppear {
                istoggle = layout.buttons[button.id]?.toggle ?? false
                size = calculateButtonSize()
            }
            .onChange(of: controllerScale) { _ in
                size = calculateButtonSize()
            }
    }
    
    private var buttonBackground: some View {
        Group {
            if !button.isTrigger && button != .leftStick && button != .rightStick {
                Circle()
                    .fill(Color.gray.opacity(0.4))
                    .frame(width: size.width * 1.25, height: size.height * 1.25)
            } else if button == .leftStick || button == .rightStick {
                Image(systemName: buttonConfig.iconName)
                    .resizable()
                    .scaledToFit()
                    .frame(width: size.width * 1.25, height: size.height * 1.25)
                    .foregroundColor(Color.gray.opacity(0.4))
            } else if button.isTrigger {
                Image(systemName: convertTriggerIconToButton(buttonConfig.iconName))
                    .resizable()
                    .scaledToFit()
                    .frame(width: size.width * 1.25, height: size.height * 1.25)
                    .foregroundColor(Color.gray.opacity(0.4))
            }
        }
    }
    
    private func convertTriggerIconToButton(_ iconName: String) -> String {
        if iconName.hasPrefix("zl") || iconName.hasPrefix("zr") {
            var converted = String(iconName.dropFirst(3))
            converted = converted.replacingOccurrences(of: "rectangle", with: "button")
            converted = converted.replacingOccurrences(of: ".fill", with: ".horizontal.fill")
            return converted
        } else {
            var converted = String(iconName.dropFirst(2))
            converted = converted.replacingOccurrences(of: "rectangle", with: "button")
            converted = converted.replacingOccurrences(of: ".fill", with: ".horizontal.fill")
            return converted
        }
    }
    
    let virtualController = ControllerManager.shared.virtualController
    
    private func handleButtonPress() {
        DispatchQueue.global(qos: .userInteractive).async {
            guard !isPressed || istoggle else { return }
            
            if istoggle {
                toggleState.toggle()
                isPressed = toggleState
                let value = toggleState ? 1 : 0
                virtualController.setButtonState(Uint8(value), for: button)
                Haptics.shared.play(.soft)
            } else {
                isPressed = true
                virtualController.setButtonState(1, for: button)
                Haptics.shared.play(.soft)
            }
        }
    }
    
    private func handleButtonRelease() {
        if istoggle { return }
        guard isPressed else { return }

        isPressed = false
        DispatchQueue.global(qos: .userInteractive).async {
            virtualController.setButtonState(0, for: button)
        }
    }
    
    private func calculateButtonSize() -> CGSize {
        let baseWidth: CGFloat
        let baseHeight: CGFloat
        
        if button.isTrigger {
            baseWidth = 70
            baseHeight = 40
        } else if button.isSmall {
            baseWidth = 35
            baseHeight = 35
        } else {
            baseWidth = 45
            baseHeight = 45
        }
        
        let deviceMultiplier = UIDevice.current.userInterfaceIdiom == .pad ? 1.2 : 1.0
        let scaleMultiplier = CGFloat(controllerScale)
        
        return CGSize(
            width: baseWidth * deviceMultiplier * scaleMultiplier,
            height: baseHeight * deviceMultiplier * scaleMultiplier
        )
    }
    
    private var buttonConfig: ButtonConfiguration {
        ButtonConfiguration.config(for: button)
    }
}

struct ExtButtonIconView: View {
    var button: VirtualControllerButton
    var opacity = 0.8
    @AppStorage("On-ScreenControllerScale") var controllerScale: Double = 1.0
    @State private var size: CGSize = .zero

    var body: some View {
        Circle()
            .foregroundStyle(.clear.opacity(0))
            .overlay {
                Image(systemName: buttonConfig.iconName)
                    .resizable()
                    .scaledToFit()
                    .frame(width: size.width / 1.5, height: size.height / 1.5)
                    .foregroundStyle(.white)
                    .opacity(opacity)
                    .allowsHitTesting(false)
            }
            .frame(width: size.width, height: size.height)
            .background(buttonBackground)
            .onAppear {
                size = calculateButtonSize()
            }
            .onChange(of: controllerScale) { _ in
                size = calculateButtonSize()
            }
    }

    private var buttonBackground: some View {
        Group {
            if !button.isTrigger && button != .leftStick && button != .rightStick {
                Circle()
                    .fill(Color.gray.opacity(0.3))
                    .frame(width: size.width * 1.25, height: size.height * 1.25)
            } else if button == .leftStick || button == .rightStick {
                Image(systemName: buttonConfig.iconName)
                    .resizable()
                    .scaledToFit()
                    .frame(width: size.width * 1.25, height: size.height * 1.25)
                    .foregroundColor(Color.gray.opacity(0.4))
            } else if button.isTrigger {
                Image(systemName: convertTriggerIconToButton(buttonConfig.iconName))
                    .resizable()
                    .scaledToFit()
                    .frame(width: size.width * 1.25, height: size.height * 1.25)
                    .foregroundColor(Color.gray.opacity(0.4))
            }
        }
    }

    private func convertTriggerIconToButton(_ iconName: String) -> String {
        var converted = iconName
        if iconName.hasPrefix("zl") || iconName.hasPrefix("zr") {
            converted = String(iconName.dropFirst(3))
        } else {
            converted = String(iconName.dropFirst(2))
        }
        converted = converted
            .replacingOccurrences(of: "rectangle", with: "button")
            .replacingOccurrences(of: ".fill", with: ".horizontal.fill")
        return converted
    }

    private func calculateButtonSize() -> CGSize {
        let baseWidth: CGFloat
        let baseHeight: CGFloat

        if button.isTrigger {
            baseWidth = 70
            baseHeight = 40
        } else if button.isSmall {
            baseWidth = 35
            baseHeight = 35
        } else {
            baseWidth = 45
            baseHeight = 45
        }

        let deviceMultiplier = UIDevice.current.userInterfaceIdiom == .pad ? 1.2 : 1.0
        let scaleMultiplier = CGFloat(controllerScale)

        return CGSize(
            width: baseWidth * deviceMultiplier * scaleMultiplier,
            height: baseHeight * deviceMultiplier * scaleMultiplier
        )
    }

    private var buttonConfig: ButtonConfiguration {
        ButtonConfiguration.config(for: button)
    }
}

// MARK: - Button Configuration

struct ButtonConfiguration {
    let iconName: String
    
    static func config(for button: VirtualControllerButton) -> ButtonConfiguration {
        switch button {
        case .A: return ButtonConfiguration(iconName: "a.circle.fill")
        case .B: return ButtonConfiguration(iconName: "b.circle.fill")
        case .X: return ButtonConfiguration(iconName: "x.circle.fill")
        case .Y: return ButtonConfiguration(iconName: "y.circle.fill")
        case .leftStick: return ButtonConfiguration(iconName: "l.joystick.press.down.fill")
        case .rightStick: return ButtonConfiguration(iconName: "r.joystick.press.down.fill")
        case .dPadUp: return ButtonConfiguration(iconName: "arrowtriangle.up.circle.fill")
        case .dPadDown: return ButtonConfiguration(iconName: "arrowtriangle.down.circle.fill")
        case .dPadLeft: return ButtonConfiguration(iconName: "arrowtriangle.left.circle.fill")
        case .dPadRight: return ButtonConfiguration(iconName: "arrowtriangle.right.circle.fill")
        case .leftTrigger: return ButtonConfiguration(iconName: "zl.rectangle.roundedtop.fill")
        case .rightTrigger: return ButtonConfiguration(iconName: "zr.rectangle.roundedtop.fill")
        case .leftShoulder: return ButtonConfiguration(iconName: "l.rectangle.roundedbottom.fill")
        case .rightShoulder: return ButtonConfiguration(iconName: "r.rectangle.roundedbottom.fill")
        case .start: return ButtonConfiguration(iconName: "plus.circle.fill")
        case .back: return ButtonConfiguration(iconName: "minus.circle.fill")
        case .guide: return ButtonConfiguration(iconName: "gearshape.fill")
        }
    }
}
