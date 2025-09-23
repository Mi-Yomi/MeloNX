//
//  BaseController.swift
//  MeloNX
//
//  Created by MediaMoots on 5/17/2025.
//

//──────────────────────────────────────────────────────────────────────── MARK:- Base Controller Protocol

import GameController

/// Base Controller with motion related functions
protocol BaseController: AnyObject, Equatable {
    var ryujinxController: Controller { get set }
    func tryRegisterMotion(slot: UInt8)
    func tryGetMotionProvider() -> DSUMotionProvider?
    var nativeController: GCController { get set }
}
