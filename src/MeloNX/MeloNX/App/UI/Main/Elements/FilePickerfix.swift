//
//  FilePickerfix.swift
//  MeloNX
//
//  Created by Stossy11 on 03/08/2025.
//

import UIKit
import UniformTypeIdentifiers
import Foundation
import ObjectiveC.runtime
import Security

@_silgen_name("_NSGetExecutablePath")
func NSGetExecutablePath(_ buf: UnsafeMutablePointer<CChar>?,
                          _ bufsize: UnsafeMutablePointer<UInt32>?) -> Int32

var shouldAsCopy = false
var isInLiveContainer: (Bool, Bundle?, Bool) = (false, nil, false)
var hostIdentifier = Bundle.main.bundleIdentifier ?? ""

extension Bundle {
    @objc dynamic var swizzled_bundleIdentifier: String? {
        if self == Bundle.main {
            return hostIdentifier
        } else {
            return self.swizzled_bundleIdentifier
        }
    }
    
    var originalBundleID: String? {
        var bufferSize = UInt32(MAXPATHLEN)
        var buffer = [CChar](repeating: 0, count: Int(bufferSize))

        _ = NSGetExecutablePath(&buffer, &bufferSize)

        let execPath = URL(fileURLWithPath: String(cString: buffer))
        let appPath = execPath.deletingLastPathComponent()
        
        let infoPlistURL = appPath.appendingPathComponent("Info.plist")

        let info = NSDictionary(contentsOf: infoPlistURL)
        let bundleID = info?["CFBundleIdentifier"] as? String
        
        return bundleID
    }

    static func swizzleBundleIdentifier() -> Bool {
        guard let originalMethod = class_getInstanceMethod(Bundle.self, #selector(getter: Bundle.bundleIdentifier)),
              let swizzledMethod = class_getInstanceMethod(Bundle.self, #selector(getter: Bundle.swizzled_bundleIdentifier)) else {
            return false
        }
        let bundle = Bundle.main.bundleIdentifier

        hostIdentifier = getModifiedHostIdentifier(originalHostIdentifier: "")
        if let liveContainerBundle = liveContainer() {
            shouldAsCopy = true
            isInLiveContainer = (true, liveContainerBundle.0, liveContainerBundle.1)
            hostIdentifier = liveContainerBundle.0?.bundleIdentifier ?? ""
            method_exchangeImplementations(originalMethod, swizzledMethod)
            return true
        } else if FileManager.default.fileExists(atPath: Bundle.main.bundleURL.appendingPathComponent("LCAppInfo.plist").path) {
            shouldAsCopy = true
            isInLiveContainer = (true, nil, false)
            method_exchangeImplementations(originalMethod, swizzledMethod)
            return true
        }

        print("Host Identifier: \(hostIdentifier)")
        
        isInLiveContainer = (false, nil, false)

        if hostIdentifier != bundle {
            shouldAsCopy = true
            method_exchangeImplementations(originalMethod, swizzledMethod)
            return true
        }
        
        return false
    }

}

func relaunchLiveContainer() -> Bool {
    if let cls = NSClassFromString("LCSharedUtils") as? NSObject.Type {
        let selector = NSSelectorFromString("launchToGuestApp")
        if cls.responds(to: selector) {
            cls.perform(selector)
        }
    }
    return true
}



func liveContainer() -> (Bundle?, Bool)? {
    if let cls = NSClassFromString("NSUserDefaults") as? NSObject.Type {
        let selector = NSSelectorFromString("lcMainBundle")
        var bundle: Bundle?
        var isDone: Bool = false

        if cls.responds(to: selector),
           let result = cls.perform(selector)?.takeUnretainedValue() as? Bundle {
            bundle = result
        } else {
            return nil
        }
        
        if let result = cls.value(forKey: "isLiveProcess") as? Bool {
            isDone = result
        } else {
            return (bundle, false)
        }
        
        
        return (bundle, isDone)
    }
    return nil
}


func getModifiedHostIdentifier(originalHostIdentifier: String) -> String {
    guard let task = SecTaskCreateFromSelf(nil) else {
        return originalHostIdentifier
    }

    var error: NSError?
    let appIdRef = SecTaskCopyValueForEntitlement(task, "application-identifier" as NSString, &error)
    releaseSecTask(task)

    guard let appId = appIdRef as? String, CFGetTypeID(appIdRef) == CFStringGetTypeID() else {
        return originalHostIdentifier
    }

    if let dotRange = appId.range(of: ".") {
        return String(appId[dotRange.upperBound...])
    }

    return appId
}


func swizzleInstanceMethod(for cls: AnyClass, original: Selector, swizzled: Selector) {
    guard
        let originalMethod = class_getInstanceMethod(cls, original),
        let swizzledMethod = class_getInstanceMethod(cls, swizzled)
    else { return }

    method_exchangeImplementations(originalMethod, swizzledMethod)
}

func swizzleClassMethod(for cls: AnyClass, original: Selector, swizzled: Selector) {
    guard
        let originalMethod = class_getClassMethod(cls, original),
        let swizzledMethod = class_getClassMethod(cls, swizzled)
    else { return }

    method_exchangeImplementations(originalMethod, swizzledMethod)
}


extension UIDocumentPickerViewController {
    // make this closer to LiveContainers implementation
    @objc(hook_initForOpeningContentTypes:asCopy:)
    func hook_initForOpeningContentTypes(
        _ contentTypes: [UTType],
        asCopy: Bool
    ) -> Self {

        // prevent crash when selecting only folder
        var shouldMultiselect = false
        if contentTypes.count == 1,
           contentTypes.first == .folder {
            shouldMultiselect = true
        }

        // if app is going to choose any unrecognized file type,
        // replace it with [.item, .folder]
        let contentTypesNew: [UTType] = [.item, .folder]

        let picker = hook_initForOpeningContentTypes(
            contentTypesNew,
            asCopy: true
        )

        if shouldMultiselect {
            picker.allowsMultipleSelection = true
        }

        return picker
    }


    @objc func hook_initWithDocumentTypes(_ contentTypes: [String], inMode mode: UIDocumentPickerMode) -> UIDocumentPickerViewController {
        let asCopy = mode != .import
        return type(of: self).init(forOpeningContentTypes: contentTypes.compactMap { UTType($0) }, asCopy: asCopy)
    }
}


extension UIDocumentBrowserViewController {
    @objc(hook_initForOpeningContentTypes:)
    func hook_initForOpeningContentTypes(_ contentTypes: [UTType]) -> UIDocumentBrowserViewController {
        if MeloNX.shouldAsCopy {
            let newTypes: [UTType] = [.item]
            return self.hook_initForOpeningContentTypes(newTypes)
        } else {
            return self.hook_initForOpeningContentTypes(contentTypes)
        }
    }
}


extension NSURL {
    @objc func hook_startAccessingSecurityScopedResource() -> Bool {
        _ = self.hook_startAccessingSecurityScopedResource()
        return true
    }
}

@objc class UTTypeHook: NSObject {
    @objc class func hook_typeWithIdentifier(_ identifier: String) -> Any? {
        if let cls = NSClassFromString("UTType") as? NSObject.Type {
            let selector = NSSelectorFromString("typeWithIdentifier:")
            let imp = cls.method(for: selector)
            typealias Func = @convention(c) (AnyObject, Selector, NSString) -> AnyObject?
            let function = unsafeBitCast(imp, to: Func.self)
            if let result = function(cls, selector, identifier as NSString) {
                return result
            }
        }
        return nil
    }
}



@objc class DOCConfiguration2: NSObject {
    @objc func hook_setHostIdentifier(_ ignored: String?) {
        let value = getModifiedHostIdentifier(originalHostIdentifier: "")

        if value != "" {
            self.hook_setHostIdentifier(hostIdentifier)
        } else {
            NSLog("Error fetching entitlement:")
            self.hook_setHostIdentifier(ignored)
        }

    }
}


@objc public class EarlyInit: NSObject {
    @objc public static func entryPoint() {

        if Bundle.swizzleBundleIdentifier() {


            swizzleInstanceMethod(for: UIDocumentPickerViewController.self,
                                  original: #selector(UIDocumentPickerViewController.init(forOpeningContentTypes:asCopy:)),
                                  swizzled: #selector(UIDocumentPickerViewController.hook_initForOpeningContentTypes(_:asCopy:)))

            swizzleInstanceMethod(for: UIDocumentPickerViewController.self,
                                  original: #selector(UIDocumentPickerViewController.init(documentTypes:in:)),
                                  swizzled: #selector(UIDocumentPickerViewController.hook_initWithDocumentTypes(_:inMode:)))

            swizzleInstanceMethod(for: NSURL.self,
                                  original: #selector(NSURL.startAccessingSecurityScopedResource),
                                  swizzled: #selector(NSURL.hook_startAccessingSecurityScopedResource))

            if let docConfigClass = NSClassFromString("DOCConfiguration") {
                let originalSelector = NSSelectorFromString("setHostIdentifier:")
                let swizzledSelector = #selector(DOCConfiguration2.hook_setHostIdentifier(_:))
                swizzleInstanceMethod(for: docConfigClass,
                                      original: originalSelector,
                                      swizzled: swizzledSelector)
                print("DOCConfiguration.setHostIdentifier: swizzled")
            } else {
                print("DOCConfiguration not found")
            }


            if let utTypeClass = NSClassFromString("UTType") {
                let originalSelector = NSSelectorFromString("typeWithIdentifier:")
                let swizzledSelector = #selector(UTTypeHook.hook_typeWithIdentifier(_:))
                swizzleClassMethod(for: utTypeClass, original: originalSelector, swizzled: swizzledSelector)
            }


            swizzleInstanceMethod(for: UIDocumentBrowserViewController.self,
                                  original: #selector(UIDocumentBrowserViewController.init(forOpening:)),
                                  swizzled: #selector(UIDocumentBrowserViewController.hook_initForOpeningContentTypes(_:)))
        }
    }
}
