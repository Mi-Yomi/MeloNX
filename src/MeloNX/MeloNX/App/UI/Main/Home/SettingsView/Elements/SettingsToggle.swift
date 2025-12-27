//
//  SettingsToggle.swift
//  MeloNX
//
//  Created by Stossy11 on 10/11/2025.
//

import SwiftUI

struct SettingsToggle: View {
    @Binding var isOn: Bool
    let icon: String
    let label: LocalizedStringKey
    var infoMessage: LocalizedStringKey = ""
    var disabled: Bool = false
    @AppStorage("toggleGreen") var toggleGreen: Bool = false
    @AppStorage("oldSettingsUI") var oldSettingsUI = false
    @State var isPresented: Bool = false
    
    var body: some View {
        Group {
            if UIDevice.current.userInterfaceIdiom == .phone || oldSettingsUI {
                Toggle(isOn: $isOn) {
                    HStack(spacing: 8) {
                        if icon.hasSuffix(".svg") {
                            SVGView(svgName: icon, color: .blue)
                                .frame(width: 20, height: 20)
                        } else {
                            Image(systemName: icon)
                            // .symbolRenderingMode(.hierarchical)
                                .foregroundColor(.blue)
                        }
                        
                        Text(label)
                            .font(.body)
                    }
                }
                .toggleStyle(SwitchToggleStyle(tint: .blue))
                .disabled(disabled)
                .padding(.vertical, 6)
            } else {
                Group {
                    HStack(spacing: 8) {
                        HStack {
                            if icon.hasSuffix(".svg") {
                                SVGView(svgName: icon, color: .blue)
                                    .frame(width: 20, height: 20)
                            } else {
                                Image(systemName: icon)
                                // .symbolRenderingMode(.hierarchical)
                                    .foregroundStyle(.blue)
                            }
                            
                            Text(label)
                                .font(.body)
                        }
                        
                        Spacer()
                        
                        if disabled {
                            Text(isOn ? "ON" : "OFF")
                                .foregroundStyle(.gray)
                        } else {
                            Text(isOn ? "ON" : "Off")
                                .foregroundStyle(isOn ? (toggleGreen ? .green : .blue) : .blue)
                        }
                    }
                    .padding()
                    .onTapGesture {
                        isOn.toggle()
                    }
                }
            }
        }
        .contextMenu {
            if !"\(infoMessage)".isEmpty {
                Button("About") {
                    isPresented = true
                }
            }
        }
        .alert(isPresented: $isPresented) {
            Alert(
                title: Text(label),
                message: Text(infoMessage),
                dismissButton: .default(Text("OK"))
            )
        }
    }
    
    func disabled(_ disabled: Bool) -> SettingsToggle {
        var view = self
        view.disabled = disabled
        return view
    }
}
