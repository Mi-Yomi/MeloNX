//
//  ControllerTypeManager.swift
//  MeloNX
//
//  Created by Stossy11 on 30/07/2025.
//

import Foundation
import SwiftUI
import Combine

class ControllerTypeManager: ObservableObject {
    @Published var controllerTypeForId: [Int: ControllerType] = [:] {
        didSet {
            save()
        }
    }

    private init() {
        load()
    }

    static let shared = ControllerTypeManager()

    func save() {
        if let data = try? JSONEncoder().encode(controllerTypeForId) {
            UserDefaults.standard.set(data, forKey: "ControllerTypesForID")
        }
    }

    func load() {
        if let data = UserDefaults.standard.data(forKey: "ControllerTypesForID") {
            if let decoded = try? JSONDecoder().decode([Int: ControllerType].self, from: data) {
                controllerTypeForId = decoded
            }
        }
    }
}
