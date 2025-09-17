//
//  AppCodableStorage.swift
//  MeloNX
//
//  Created by Stossy11 on 12/04/2025.
//

import SwiftUI

@propertyWrapper
struct AppCodableStorage<Value: Codable & Equatable>: DynamicProperty {
    @State private var value: Value

    private let key: String
    private let defaultValue: Value
    private let storage: UserDefaults

    init(wrappedValue defaultValue: Value, _ key: String, store: UserDefaults = .standard) {
        self._value = State(initialValue: {
            if let data = store.data(forKey: key),
               let decoded = try? JSONDecoder().decode(Value.self, from: data) {
                return decoded
            }
            return defaultValue
        }())
        self.key = key
        self.defaultValue = defaultValue
        self.storage = store
    }

    var wrappedValue: Value {
        get { value }
        nonmutating set {
            value = newValue
            if let data = try? JSONEncoder().encode(newValue) {
                storage.set(data, forKey: key)
            }
        }
    }

    var projectedValue: Binding<Value> {
        Binding(
            get: { self.wrappedValue },
            set: { newValue in self.wrappedValue = newValue }
        )
    }
}

@propertyWrapper
struct ArrayStorage<T: Codable> {
    private let key: String
    private let defaultValue: [T]
    private let userDefaults: UserDefaults

    init(wrappedValue: [T], _ key: String, userDefaults: UserDefaults = .standard) {
        self.key = key
        self.defaultValue = wrappedValue
        self.userDefaults = userDefaults
    }

    var wrappedValue: [T] {
        get {
            guard let data = userDefaults.data(forKey: key) else {
                return defaultValue
            }
            do {
                return try JSONDecoder().decode([T].self, from: data)
            } catch {
                print("Failed to decode [\(T.self)] from UserDefaults: \(error)")
                return defaultValue
            }
        }
        set {
            do {
                let data = try JSONEncoder().encode(newValue)
                userDefaults.set(data, forKey: key)
            } catch {
                print("Failed to encode [\(T.self)] to UserDefaults: \(error)")
            }
        }
    }
}

