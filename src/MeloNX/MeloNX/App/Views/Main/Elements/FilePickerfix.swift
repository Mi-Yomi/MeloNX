//
//  FilePickerfix.swift
//  MeloNX
//
//  Created by Stossy11 on 03/08/2025.
//

import UIKit
import UniformTypeIdentifiers
import Foundation

extension UIDocumentPickerViewController {

    static func swizzleInitWithContentTypes() {
        let originalSelector = #selector(UIDocumentPickerViewController.init(forOpeningContentTypes:asCopy:))
        let swizzledSelector = #selector(UIDocumentPickerViewController.init(forOpeningContentTypes2:asCopy:))

        guard let originalMethod = class_getInstanceMethod(UIDocumentPickerViewController.self, originalSelector),
              let swizzledMethod = class_getInstanceMethod(UIDocumentPickerViewController.self, swizzledSelector) else {
            return
        }

        if getModifiedHostIdentifier(originalHostIdentifier: "") != Bundle.main.bundleIdentifier {
            method_exchangeImplementations(originalMethod, swizzledMethod)
        }
    }

    @objc convenience init(forOpeningContentTypes2: [UTType], asCopy: Bool) {
        self.init(forOpeningContentTypes2: forOpeningContentTypes2, asCopy: true)
    }
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
