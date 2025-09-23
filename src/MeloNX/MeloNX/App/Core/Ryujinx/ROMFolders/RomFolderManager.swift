//
//  RomFolderManager.swift
//  MeloNX
//
//  Created by Stossy11 on 31/07/2025.
//

import Foundation
import SwiftUI

let withSecurityScope = URL.BookmarkResolutionOptions(rawValue: 1 << 10)

class ROMFolderManager: ObservableObject {
    
    private let bookmarksKey = "ROMFolderManagerBookmarks"
    @Published var bookmarks: [String: Data] = [:] {
        didSet {
            saveBookmarks()
        }
    }
    
    private init() {
        loadBookmarks()
    }
    
    static var shared = ROMFolderManager()
    
    func addFolder(url: URL) -> Bool {
        let options = URL.BookmarkCreationOptions(rawValue: 1 << 11)
        do {
            let bookmark = try url.bookmarkData(options: options,
                                                includingResourceValuesForKeys: nil,
                                                relativeTo: nil)
            bookmarks[url.path] = bookmark
            saveBookmarks()
            return true
        } catch {
            print("Failed to create bookmark: \(error)")
            return false
        }
    }
    
    
    func stopAccessingFolder(url: URL) {
        url.stopAccessingSecurityScopedResource()
    }
    
    private func saveBookmarks() {
        UserDefaults.standard.set(bookmarks, forKey: bookmarksKey)
    }
    
    func loadBookmarks() {
        if let saved = UserDefaults.standard.dictionary(forKey: bookmarksKey) as? [String: Data] {
            bookmarks = saved
        }
    }
}
