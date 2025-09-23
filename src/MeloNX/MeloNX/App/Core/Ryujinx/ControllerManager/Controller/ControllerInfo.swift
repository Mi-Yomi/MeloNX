//
//  ControllerInfo.swift
//  MeloNX
//
//  Created by Stossy11 on 28/06/2025.
//

import GameController

struct Controller: Identifiable, Hashable {
    var id: String
    var name: String
    var controllerType: ControllerType = .proController
    var isVirtualController: Bool = false
}
