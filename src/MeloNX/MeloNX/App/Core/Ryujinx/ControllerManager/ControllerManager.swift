//
//  ControllerManager.swift
//  MeloNX
//
//  Created by Stossy11 on 22/09/2025.
//

import Foundation
import SwiftUI
import Combine
import GameController

class ControllerManager: ObservableObject {
    static let shared = ControllerManager()
    
    // MARK: - Properties
    static var virtualController = VirtualController()
    
    @Published var waitingForController: [GCController] = []
    @Published var currentControllers: [Controller] = []
    @Published var nativeControllers: [any BaseController] = []
    
    private init() {
        loadControllerTypes()
    }
    
    // MARK: - SDL2 Conrollers
    
    static func generateGamepadId(from controller: OpaquePointer) -> String? {
        guard let joystick = SDL_GameControllerGetJoystick(controller) else {
            return nil
        }

        let instanceID = SDL_JoystickInstanceID(joystick)
        let guid = SDL_JoystickGetGUID(joystick)

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

        return "\(instanceID)-\(formattedGUID)"
    }
    
    func refreshControllersList() {
        currentControllers = []
        // nativeControllers = []
        
        if !nativeControllers.contains(where: { $0 as? VirtualController == Self.virtualController }) {
            nativeControllers.append(Self.virtualController)
        }
        
        for controller in GCController.controllers() {
            if !nativeControllers.contains(where: { $0.nativeController == controller }) {
                nativeControllers.append(NativeController(controller))
            }
        }
        
        // controllersList = getConnectedControllers()

        // controllersList.removeAll(where: { $0.id == "0" || (!$0.name.starts(with: "GC - ") && $0 != onscreencontroller) })
        // controllersList.mutableForEach { $0.name = $0.name.replacingOccurrences(of: "GC - ", with: "") }
        
        if nativeControllers.count == 1 {
            if ProcessInfo.processInfo.isiOSAppOnMac {
                currentControllers.append(Self.virtualController.ryujinxController)
            }
        } else if (nativeControllers.count - 1) >= 1 {
            for controller in nativeControllers.map(\.ryujinxController) {
                if controller.id != Self.virtualController.ryujinxController.id && !currentControllers.contains(where: { $0.id == controller.id }) {
                    currentControllers.append(controller)
                }
            }
        }
        
        print(currentControllers)
    }
    
    func registerMotionForMatchingControllers() {
        // Loop through currentControllers with index
        for (index, controller) in currentControllers.enumerated() {
            let slot = UInt8(index)
            
            if controller.isVirtualController && Self.virtualController.tryGetMotionProvider() == nil {
                Self.virtualController.tryRegisterMotion(slot: slot)
                continue
            }
            
            // Check native controllers
            for nativeController in nativeControllers where nativeController.ryujinxController.id == controller.id && nativeController.tryGetMotionProvider() == nil && nativeController.ryujinxController.isVirtualController == false {
                nativeController.tryRegisterMotion(slot: slot)
                continue
            }
            
        }
    }
    
    // MARK: - Controller Reconnection

    func initControllerObservers() {
        NotificationCenter.default.addObserver(
            forName: .GCControllerDidConnect,
            object: nil,
            queue: .main
        ) { [self] notification in
            guard let controller = notification.object as? GCController else {
                return
            }

            let waitingControllersWithSameName = waitingForController.filter { $0.vendorName == controller.vendorName }
            
            if waitingControllersWithSameName.count > 1 && Ryujinx.shared.isRunning {
                showControllerSelectionAlert(newController: controller, waitingControllers: waitingControllersWithSameName)
                return
            }

            if let index = waitingForController.firstIndex(where: { $0.vendorName == controller.vendorName }), Ryujinx.shared.isRunning {
                waitingForController.remove(at: index)
                
                reconnectController(newController: controller, oldController: waitingControllersWithSameName.first)
            } else if nativeControllers.firstIndex(where: { $0.nativeController == controller }) == nil {
                nativeControllers.append(NativeController(controller))
                self.refreshControllersList()
            }
        }

        NotificationCenter.default.addObserver(
            forName: .GCControllerDidDisconnect,
            object: nil,
            queue: .main
        ) { [self] notification in
            guard let controller = notification.object as? GCController else {
                return
            }
            
            if !Ryujinx.shared.isRunning {
                self.currentControllers = []
                // ?.cleanup()
                if let ncIndex = self.nativeControllers.firstIndex(where: { $0.nativeController == controller }), let nccontroller = self.nativeControllers[ncIndex] as? NativeController {
                    nccontroller.cleanup()
                }
                self.nativeControllers.removeAll(where: { $0.nativeController == controller })
                self.refreshControllersList()
                return
            }

            if !self.waitingForController.contains(where: { $0 === controller }) {
                self.waitingForController.append(controller)
            }

            DispatchQueue.main.asyncAfter(deadline: .now() + 60) {
                if let _ = self.waitingForController.firstIndex(where: { $0.vendorName == controller.vendorName }) {
                } else {
                    self.waitingForController.removeAll(where: { $0.vendorName == controller.vendorName })
                    self.currentControllers = []
                    if let ncIndex = self.nativeControllers.firstIndex(where: { $0.nativeController == controller }), let nccontroller = self.nativeControllers[ncIndex] as? NativeController {
                        nccontroller.cleanup()
                    }
                    self.nativeControllers.removeAll(where: { $0.nativeController == controller })
                    self.refreshControllersList()
                }
            }
        }
    }

    private func showControllerSelectionAlert(newController: GCController, waitingControllers: [GCController]) {
        guard let windowScene = UIApplication.shared.connectedScenes.first as? UIWindowScene,
              let window = windowScene.windows.first else {
            print("[Alert] Unable to get main window")
            return
        }
        
        let alert = UIAlertController(
            title: "Multiple Controllers Detected",
            message: "Multiple controllers with the name '\(newController.vendorName ?? "Unknown")' are waiting to reconnect. Which one would you like to reconnect?",
            preferredStyle: .alert
        )
        

        for (index, waitingController) in waitingControllers.enumerated() {
            let title = "Controller \(index + 1)"
            let action = UIAlertAction(title: title, style: .default) { [self] _ in
                self.handleControllerSelection(newController: newController, selectedOldController: waitingController)
            }
            alert.addAction(action)
        }
        
        let cancelAction = UIAlertAction(title: "Cancel", style: .cancel) { [self] _ in
            self.nativeControllers.append(NativeController(newController))
            self.refreshControllersList()
        }
        alert.addAction(cancelAction)
        
        if let presentedViewController = window.rootViewController?.presentedViewController {
            presentedViewController.present(alert, animated: true)
        } else {
            window.rootViewController?.present(alert, animated: true)
        }
    }

    private func handleControllerSelection(newController: GCController, selectedOldController: GCController) {
        if let index = waitingForController.firstIndex(where: { $0 === selectedOldController }) {
            waitingForController.remove(at: index)
        }
        
        reconnectController(newController: newController, oldController: selectedOldController)
    }

    private func reconnectController(newController: GCController, oldController: GCController?) {
        if let oldEntry = nativeControllers.first(where: { $0.nativeController == oldController }) {
            print("[GCControllerDidConnect] Updating native controller with new gamepad")
            let nativeController = oldEntry as? NativeController
            // nativeControllers.removeValue(forKey: oldEntry.key)
           // nativeControllers.removeAll { $0 == nativeController }
            
            nativeController?.changeGamepad(newController)

            // nativeControllers[newController] = nativeController
        } else {
            print("[GCControllerDidConnect] Initializing new native controller")
            nativeControllers.append(NativeController(newController))
        }
        
        self.refreshControllersList()
    }
    
    
    // MARK: - Controller Types
    @Published var controllerTypes: [Int: ControllerType] = [:] {
        didSet {
            saveControllerTypes()
        }
    }
    
    func saveControllerTypes() {
        if let data = try? JSONEncoder().encode(controllerTypes) {
            UserDefaults.standard.set(data, forKey: "ControllerTypesForID")
        }
    }

    func loadControllerTypes() {
        if let data = UserDefaults.standard.data(forKey: "ControllerTypesForID") {
            if let decoded = try? JSONDecoder().decode([Int: ControllerType].self, from: data) {
                controllerTypes = decoded
            }
        }
    }
    
}
