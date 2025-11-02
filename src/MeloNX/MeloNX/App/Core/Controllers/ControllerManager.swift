//
//  ControllerManager.swift
//  MeloNX
//
//  Created by Stossy11 on 19/10/2025.
//

import Foundation
import Combine
import GameController

class ControllerManager: ObservableObject {
    static var shared = ControllerManager()
    let virtualController = BaseController(nativeController: nil)
    @Published var allControllers: [BaseController] = []
    @Published var selectedControllers: [String] = []
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
    
    private init() {
        loadControllerTypes()
        
        allControllers.append(virtualController)
    }
    

    func refreshControllersList() {
        let connectedNativeControllers = Set(GCController.controllers())
        
        var controllersToRemove: [BaseController] = []
        for controller in allControllers where !controller.virtual {
            if let native = controller.nativeController, !connectedNativeControllers.contains(native) {
                controllersToRemove.append(controller)
            }
        }
        
        for controller in controllersToRemove {
            controller.cleanup()
            allControllers.removeAll { $0 === controller }
            selectedControllers.removeAll { $0 == controller.id }
        }
        
        for nativeController in connectedNativeControllers {
            if !allControllers.contains(where: { $0.nativeController === nativeController }) {
                let newController = NativeController(nativeController: nativeController)
                allControllers.append(newController)
            }
        }
        
        selectedControllers.removeAll()
        
        let physicalControllers = allControllers.filter { !$0.virtual }.compactMap(\.id) as [String]
        
        if physicalControllers.isEmpty && !ProcessInfo.processInfo.isiOSAppOnMac && !UserDefaults.standard.bool(forKey: "virtualControllerOffDefault") {
            selectedControllers.append(virtualController.id)
        } else {
            selectedControllers.append(contentsOf: physicalControllers)
        }
    }
    
    func controllerAndIndexForString(_ id: String) -> (BaseController, Int) {
        return (allControllers.first(where: { $0.id == id })!, Int(allControllers.firstIndex(where: { $0.id == id })!))
    }
    
    
    func controllerForString(_ id: String) -> BaseController? {
        return allControllers.first(where: { $0.id == id })
    }
    
    func firstControllerForName(_ name: String) -> BaseController? {
        return allControllers.first(where: { $0.nativeController?.vendorName ?? UUID().uuidString == name })
    }
    
    func hasVirtualController() -> Bool {
        return selectedControllers.contains(allControllers.first(where: { $0.virtual })?.id ?? "Failed to find virtual controller!")
    }
    
    func initControllerObservers() {
        NotificationCenter.default.addObserver(
            forName: .GCControllerDidConnect,
            object: nil,
            queue: .main
        ) { notification in
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
                if Ryujinx.shared.isRunning {
                    if let cool = notification.object as? GCController, let name = cool.vendorName, let controller = self.firstControllerForName(name) as? NativeController {
                        controller.setupNewNativeController(cool)
                    }
                } else {
                    self.refreshControllersList()
                }
            }
        }
        
        NotificationCenter.default.addObserver(
            forName: .GCControllerDidDisconnect,
            object: nil,
            queue: .main
        ) { notification in
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
                if Ryujinx.shared.isRunning {
                    
                } else {
                    self.refreshControllersList()
                }
            }
        }
    }
    
    func toggleController(_ baseController: BaseController) {
        if let index = selectedControllers.firstIndex(where: { $0 == baseController.id }) {
            selectedControllers.remove(at: index)
        } else {
            selectedControllers.append(baseController.id)
        }
    }
    
    func registerMotionAndControllerTypeForMatchingControllers() {
        for (index, id) in selectedControllers.enumerated() {
            if let controller = allControllers.first(where: { $0.id == id }) {
                controller.tryRegisterMotion(slot: UInt8(index))
                controller.type = controllerTypes[index] ?? controller.type
            }
        }
    }
}
