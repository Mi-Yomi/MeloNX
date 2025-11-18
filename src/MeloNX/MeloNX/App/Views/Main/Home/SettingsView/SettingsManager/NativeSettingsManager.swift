//
//  NativeSettingsManager.swift
//  MeloNX
//
//  Created by Stossy11 on 07/11/2025.
//

import Foundation
import SwiftUI
import Combine

@dynamicMemberLookup
class NativeSettingsManager: ObservableObject {
    var settings: Set<AnyHashable> = []
    static var shared = NativeSettingsManager()
    
    subscript<T: Any>(dynamicMember member: String) -> (T) -> Setting<T> {
        return { [weak self] input in
            Setting<T>.getOrCreateSetting(named: member, default: input, self: self)
        }
    }
    
    subscript<T: Any>(dynamicMember member: String) -> Setting<T> {
        if T.self == Bool.self {
            return Setting<T>.getOrCreateSetting(named: member, default: false as? T, self: self)
        } else if T.self == Double.self {
            return Setting<T>.getOrCreateSetting(named: member, default: 0 as? T, self: self)
        }
        
        return Setting<T>.getOrCreateSetting(named: member, default: nil, self: self)
    }
    
    
    func setting<T: Any>(forKey key: String, default defaultValue: T) -> Setting<T> {
        Setting<T>.getOrCreateSetting(named: key, default: defaultValue, self: self)
    }
}

class Setting<T: Any>: Hashable, DynamicProperty {
    static func == (lhs: Setting<T>, rhs: Setting<T>) -> Bool {
        lhs.name == rhs.name && lhs.parent === rhs.parent
    }
    
    var name: String
    var uddefault: Any
    weak var parent: NativeSettingsManager?
    
    func hash(into hasher: inout Hasher) {
        if let parent {
            hasher.combine(ObjectIdentifier(parent))
        }
        hasher.combine(name)
        hasher.combine(ObjectIdentifier(Mirror(reflecting: uddefault).subjectType))
    }
    
    var projectedValue: Binding<T> {
        Binding(get: { self.value }, set: { self.value = $0 })
    }
    
    init(name: String, defaultAny: T?, parent: NativeSettingsManager? = nil) {
        self.name = name
        self.uddefault = defaultAny ?? UUID()
        self.parent = parent
        
        if UserDefaults.standard.object(forKey: name) == nil {
            UserDefaults.standard.set(defaultAny, forKey: name)
        }
    }
    
    func binding(default defaultValue: T) -> Binding<T> {
        Binding(
            get: {
                (UserDefaults.standard.object(forKey: self.name) as? T) ?? defaultValue
            },
            set: { newValue in
                self.set(newValue)
            }
        )
    }
    
    var value: T {
        get { UserDefaults.standard.object(forKey: self.name) as! T }
        set { self.set(newValue) }
    }

    private func set(_ value: Any?) {
        UserDefaults.standard.set(value, forKey: name)
        parent?.objectWillChange.send()
    }
    
    static func getOrCreateSetting(named name: String, default defaultValue: T?, self: NativeSettingsManager?) -> Setting<T> {
        if let setting = self?.settings.first(where: { ($0 as? Setting<T>)?.name == name}) {
            return setting as? Setting ?? Setting(name: name, defaultAny: defaultValue, parent: self)
        }
        
        let setting = Setting<T>(name: name, defaultAny: defaultValue, parent: self)
        self?.settings.insert(setting)
        return setting
    }
}

extension Setting where T == Bool {
    static prefix func ! (setting: Setting<Bool>) -> Bool {
        return !setting.value
    }

    static func == (lhs: Setting<Bool>, rhs: Bool) -> Bool {
        lhs.value == rhs
    }

    var wrappedValue: Bool { value }
}
