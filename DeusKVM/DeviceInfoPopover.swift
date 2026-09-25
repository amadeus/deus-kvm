import SwiftUI

struct DeviceInfoPopover: View {
    let entry: DeviceEntry
    let status: String
    let companionStatus: String?
    @EnvironmentObject private var names: DeviceNameStore
    @Environment(\.dismiss) private var dismiss
    @State private var isRenaming = false
    @State private var draftName = ""
    @FocusState private var nameFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if isRenaming {
                nameEditor
            } else {
                HStack(alignment: .firstTextBaseline, spacing: 12) {
                    Text(verbatim: names.name(for: entry.id) ?? entry.displayName)
                        .font(.headline)
                        .textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 0)
                    if entry.isHostConnected {
                        Button(L10n.DevicePopover.rename) {
                            draftName = names.name(for: entry.id) ?? entry.name
                            isRenaming = true
                        }
                        .buttonStyle(.link)
                    }
                }
            }
            VStack(alignment: .leading, spacing: 4) {
                Text(verbatim: status)
                if let companionStatus {
                    Text(verbatim: companionStatus)
                }
            }
            .font(.callout)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
            Divider()
            VStack(alignment: .leading, spacing: 4) {
                Text(L10n.DeviceInfo.identifier).font(.caption)
                Text(verbatim: entry.id.uuidString)
                    .font(.system(.caption, design: .monospaced))
                    .textSelection(.enabled)
            }
            .foregroundStyle(.secondary)
            if let manufacturer = entry.manufacturer {
                VStack(alignment: .leading, spacing: 4) {
                    Text(L10n.DeviceInfo.manufacturer).font(.caption).foregroundStyle(.secondary)
                    Text(verbatim: manufacturer).textSelection(.enabled)
                }
            }
        }
        .frame(width: 320, alignment: .leading)
        .padding(16)
        .onExitCommand {
            if isRenaming { isRenaming = false } else { dismiss() }
        }
    }

    private var nameEditor: some View {
        VStack(alignment: .leading, spacing: 8) {
            TextField(L10n.DeviceInfo.name, text: $draftName)
                .textFieldStyle(.roundedBorder)
                .focused($nameFocused)
                .onSubmit(saveName)
                .onAppear { nameFocused = true }
            HStack {
                Spacer()
                Button(L10n.DevicePopover.cancel) { isRenaming = false }
                Button(L10n.DevicePopover.save, action: saveName)
                    .keyboardShortcut(.defaultAction)
            }
            .controlSize(.small)
        }
    }

    private func saveName() {
        names.setName(draftName, for: entry.id)
        isRenaming = false
    }
}
