import SwiftUI
import AppKit
import ServiceManagement
import GuardCore

private let brand = Color(red: 186.0 / 255, green: 1.0 / 255, blue: 196.0 / 255)

@MainActor
final class GuardModel: ObservableObject {
    @Published var report = ScanReport.empty
    @Published var busy = false
    @Published var paused = false
    @Published var message = "Checking your AI apps…"
    @Published var error: String?
    @Published var disk: [DiskUsage] = []
    @Published var measuringDisk = false
    @Published var loginEnabled = SMAppService.mainApp.status == .enabled
    @Published var automaticCleanup: Bool {
        didSet { UserDefaults.standard.set(automaticCleanup, forKey: "automaticCleanup") }
    }
    let engine = GuardEngine()
    private var timer: Timer?

    init() {
        automaticCleanup = UserDefaults.standard.object(forKey: "automaticCleanup") as? Bool ?? true
        timer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, !self.paused else { return }
                await self.refresh()
            }
        }
        Task { await refresh() }
    }

    func refresh() async {
        guard !busy else { return }
        busy = true
        error = nil
        defer { busy = false }
        do {
            report = try await engine.scan()
            if automaticCleanup && !paused && !report.leftovers.isEmpty {
                let count = try await engine.closeLeftovers(report.leftovers)
                if count > 0 { report = try await engine.scan(); message = "Closed \(count) leftover server\(count == 1 ? "" : "s"). Your active apps keep running." }
                else { updateSummary() }
            } else { updateSummary() }
        } catch { self.error = error.localizedDescription; message = "The last check could not finish." }
    }

    private func updateSummary() {
        message = report.agents.isEmpty ? "No AI apps running right now." : "Your AI apps use \(size(report.aiBytes))."
    }

    func closeLeftovers() async {
        guard !busy else { return }
        busy = true
        defer { busy = false }
        do {
            let count = try await engine.closeLeftovers(report.leftovers)
            report = try await engine.scan()
            message = "Closed \(count) leftover server\(count == 1 ? "" : "s")."
        } catch { self.error = error.localizedDescription }
    }

    func measureDisk() async {
        guard !measuringDisk else { return }
        measuringDisk = true
        disk = await Task.detached(priority: .utility) { DiskReader.scan() }.value
        measuringDisk = false
    }

    func setLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            loginEnabled = SMAppService.mainApp.status == .enabled
            if enabled && !loginEnabled { error = "Allow AI RAM Guard in System Settings → General → Login Items, then try again." }
        } catch { self.error = "Could not change login startup. \(error.localizedDescription)"; loginEnabled = SMAppService.mainApp.status == .enabled }
    }
}

private func size(_ bytes: UInt64) -> String {
    ByteCountFormatter.string(fromByteCount: Int64(min(bytes, UInt64(Int64.max))), countStyle: .memory)
}

@main
@MainActor
struct AiRamGuardMacApp: App {
    @StateObject private var model: GuardModel

    init() {
        if CommandLine.arguments.contains("--smoke-test") {
            // Also expires if SwiftUI never creates the first window or the UI thread stalls.
            DispatchQueue.global().asyncAfter(deadline: .now() + 40) {
                FileHandle.standardError.write(Data("UI startup smoke test timed out\n".utf8))
                exit(2)
            }
        }
        if CommandLine.arguments.contains("--scan-json") {
            let semaphore = DispatchSemaphore(value: 0)
            Task.detached {
                do {
                    let report = try await GuardEngine().scan()
                    let encoder = JSONEncoder()
                    encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
                    encoder.dateEncodingStrategy = .iso8601
                    print(String(decoding: try encoder.encode(report), as: UTF8.self))
                    semaphore.signal()
                } catch {
                    FileHandle.standardError.write(Data((error.localizedDescription + "\n").utf8))
                    exit(1)
                }
            }
            guard semaphore.wait(timeout: .now() + 25) == .success else { exit(2) }
            exit(0)
        }
        _model = StateObject(wrappedValue: GuardModel())
    }

    var body: some Scene {
        WindowGroup("AI RAM Guard", id: "main") {
            GuardWindow(model: model)
                .frame(minWidth: 680, minHeight: 520)
                .tint(brand)
                .onAppear {
                    NSApplication.shared.activate(ignoringOtherApps: true)
                    if CommandLine.arguments.contains("--smoke-test") {
                        Task { @MainActor in
                            await model.refresh()
                            while model.busy { try? await Task.sleep(nanoseconds: 50_000_000) }
                            guard model.report.totalMemory > 0, model.error == nil else { exit(1) }
                            print("AI RAM Guard UI and process scan started successfully")
                            NSApplication.shared.terminate(nil)
                        }
                    }
                }
        }
        .defaultSize(width: 800, height: 620)
        MenuBarExtra("AI RAM Guard", systemImage: "memorychip") {
            MenuContent(model: model)
        }
    }
}

struct MenuContent: View {
    @ObservedObject var model: GuardModel
    @Environment(\.openWindow) private var openWindow
    var body: some View {
        Text(model.paused ? "Paused" : "AI apps: \(size(model.report.aiBytes))")
        Button("Open AI RAM Guard") { openWindow(id: "main"); NSApplication.shared.activate(ignoringOtherApps: true) }
        Button("Check now") { Task { await model.refresh() } }.disabled(model.busy)
        Button(model.paused ? "Resume guarding" : "Pause guarding") { model.paused.toggle() }
        Divider()
        Button("Quit AI RAM Guard") { NSApplication.shared.terminate(nil) }.keyboardShortcut("q")
    }
}

struct GuardWindow: View {
    @ObservedObject var model: GuardModel
    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            HStack(alignment: .center, spacing: 14) {
                Image(systemName: "memorychip.fill").font(.system(size: 28)).foregroundStyle(brand).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 4) {
                    Text("AI RAM Guard").font(.title2.weight(.semibold))
                    Text(model.paused ? "Paused · checks resume when you choose" : "Watching your AI apps · checks every minute").foregroundStyle(.secondary)
                }
                Spacer()
                Button(model.paused ? "Resume" : "Pause") { model.paused.toggle() }
            }
            if let error = model.error {
                HStack(alignment: .top) {
                    Image(systemName: "exclamationmark.triangle").foregroundStyle(.orange)
                    Text(error).textSelection(.enabled)
                    Spacer()
                    Button("Try again") { Task { await model.refresh() } }.disabled(model.busy)
                }.padding(12).background(.quaternary, in: RoundedRectangle(cornerRadius: 8))
            }
            TabView {
                Overview(model: model).tabItem { Label("Overview", systemImage: "memorychip") }
                DiskView(model: model).tabItem { Label("Disk", systemImage: "internaldrive") }
                SettingsView(model: model).tabItem { Label("Settings", systemImage: "gearshape") }
            }
        }
        .padding(28)
    }
}

struct Overview: View {
    @ObservedObject var model: GuardModel
    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 8) {
                    Text(model.message).font(.title2.weight(.semibold))
                    if model.report.totalMemory > 0 {
                        Text("\(size(model.report.totalMemory)) installed · ~\(size(model.report.reclaimableMemory)) reclaimable").foregroundStyle(.secondary)
                    }
                }
                Spacer()
                if model.busy { ProgressView().controlSize(.small).accessibilityLabel("Checking AI apps") }
                Button("Refresh") { Task { await model.refresh() } }.disabled(model.busy).keyboardShortcut("r", modifiers: .command)
            }
            if model.report.agents.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    Text(model.busy ? "Checking running processes…" : "You’re all set.").font(.headline)
                    Text("Open Claude, Codex, Cursor or another supported AI app. It will appear here automatically.").foregroundStyle(.secondary)
                }.frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            } else {
                List(model.report.agents) { agent in
                    HStack(alignment: .top) {
                        VStack(alignment: .leading, spacing: 5) {
                            Text(agent.name).font(.headline)
                            Text("\(agent.processCount) processes").foregroundStyle(.secondary)
                            if !agent.duplicateHelpers.isEmpty {
                                Text(agent.duplicateHelpers.sorted(by: { $0.key < $1.key }).map { "\($0.key) ×\($0.value)" }.joined(separator: " · "))
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        Spacer()
                        Text(size(agent.bytes)).font(.headline).monospacedDigit()
                    }.padding(.vertical, 9)
                }.listStyle(.inset)
            }
            if !model.report.leftovers.isEmpty {
                HStack {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("\(model.report.leftovers.count) leftover MCP servers").font(.headline)
                        Text("Previously seen under an AI app that has now closed.").foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("Close leftovers") { Task { await model.closeLeftovers() } }.disabled(model.busy).buttonStyle(.borderedProminent)
                }
            }
            Text("On Mac, macOS manages memory compression and reclaiming. Guard safely closes verified leftover servers; it never trims active apps.")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }.padding(20)
    }
}

struct DiskView: View {
    @ObservedObject var model: GuardModel
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                VStack(alignment: .leading, spacing: 5) {
                    Text("Where your AI tools keep files").font(.title2.weight(.semibold))
                    Text("Models, sessions and caches. Nothing is deleted or moved.").foregroundStyle(.secondary)
                }
                Spacer()
                Button("Measure again") { Task { await model.measureDisk() } }.disabled(model.measuringDisk)
            }
            if model.measuringDisk { ProgressView("Measuring folders…").frame(maxWidth: .infinity, maxHeight: .infinity) }
            else if model.disk.isEmpty { Text("No supported AI folders found yet.").foregroundStyle(.secondary).frame(maxHeight: .infinity, alignment: .top) }
            else {
                List(model.disk) { item in
                    HStack {
                        VStack(alignment: .leading, spacing: 5) {
                            Text(item.name).font(.headline)
                            Text(item.path.replacingOccurrences(of: FileManager.default.homeDirectoryForCurrentUser.path, with: "~")).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                        }
                        Spacer()
                        Text((item.incomplete ? "≥ " : "") + size(item.bytes)).monospacedDigit()
                        Button("Open") { NSWorkspace.shared.open(URL(fileURLWithPath: item.path, isDirectory: true)) }
                    }.padding(.vertical, 8)
                }.listStyle(.inset)
            }
            Text("Linked folders are skipped. ≥ means a large or unreadable folder was only partly measured.").font(.caption).foregroundStyle(.secondary)
        }.padding(20).task { if model.disk.isEmpty { await model.measureDisk() } }
    }
}

struct SettingsView: View {
    @ObservedObject var model: GuardModel
    var body: some View {
        Form {
            Section {
                Toggle("Close verified leftover MCP servers automatically", isOn: $model.automaticCleanup)
                Text("Only servers Guard previously saw under an AI app, after that app and their parent process have gone. Active apps are never closed.")
                    .font(.caption).foregroundStyle(.secondary)
                Toggle("Start with my Mac", isOn: Binding(get: { model.loginEnabled }, set: { model.setLogin($0) }))
                Text("If macOS asks, allow AI RAM Guard in System Settings → General → Login Items.").font(.caption).foregroundStyle(.secondary)
            }
            Section("About") {
                Text("AI RAM Guard 1.4.1 for Mac · Preview")
                Text("Free and open source · Byens IT").foregroundStyle(.secondary)
                Link("Source code and help", destination: URL(string: "https://github.com/byensitmagnus/ai-ram-guard")!)
                Text("This first Mac version monitors RAM and disk and closes verified leftovers. Config editing and Windows-style memory trimming are not available on Mac.").font(.caption).foregroundStyle(.secondary)
            }
        }.formStyle(.grouped).padding(8)
    }
}
