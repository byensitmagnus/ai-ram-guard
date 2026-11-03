import Foundation
import Darwin
import ProcessProbe

public struct ProcessIdentity: Codable, Hashable, Sendable {
    public let pid: Int32
    public let parentPID: Int32
    public let userID: UInt32
    public let startedSeconds: UInt64
    public let startedMicroseconds: UInt64
    public init(pid: Int32, parentPID: Int32, userID: UInt32, startedSeconds: UInt64, startedMicroseconds: UInt64 = 0) {
        self.pid = pid; self.parentPID = parentPID; self.userID = userID
        self.startedSeconds = startedSeconds; self.startedMicroseconds = startedMicroseconds
    }
    public var key: String { "\(userID):\(pid):\(startedSeconds):\(startedMicroseconds)" }
    public static func read(_ pid: Int32) -> ProcessIdentity? {
        let p = guard_process_identity(pid)
        guard p.valid != 0 else { return nil }
        return ProcessIdentity(pid: p.pid, parentPID: p.parent_pid, userID: p.user_id, startedSeconds: p.started_seconds, startedMicroseconds: p.started_microseconds)
    }
    public func isSameProcess(as other: ProcessIdentity) -> Bool { key == other.key }
}

public struct ProcessRecord: Sendable {
    public let identity: ProcessIdentity
    public let bytes: UInt64
    // Kept in memory only: command lines may contain credentials and are never persisted or emitted as JSON.
    public let command: String
    public init(identity: ProcessIdentity, bytes: UInt64, command: String) { self.identity = identity; self.bytes = bytes; self.command = command }
}

public struct AgentUsage: Codable, Identifiable, Sendable {
    public var id: String { name }
    public let name: String
    public let bytes: UInt64
    public let processCount: Int
    public let duplicateHelpers: [String: Int]
}

public struct Leftover: Codable, Identifiable, Sendable {
    public var id: String { identity.key }
    public let identity: ProcessIdentity
    public let bytes: UInt64
    public let helper: String
}

public struct ScanReport: Codable, Sendable {
    public let measuredAt: Date
    public let totalMemory: UInt64
    public let reclaimableMemory: UInt64
    public let agents: [AgentUsage]
    public let leftovers: [Leftover]
    public var aiBytes: UInt64 { agents.reduce(0) { $0 + $1.bytes } + leftovers.reduce(0) { $0 + $1.bytes } }
    public static let empty = ScanReport(measuredAt: .distantPast, totalMemory: 0, reclaimableMemory: 0, agents: [], leftovers: [])
}

public enum GuardError: LocalizedError {
    case processList(String)
    case unsafeDataFolder
    public var errorDescription: String? {
        switch self {
        case .processList(let message): return "Could not read running processes. \(message)"
        case .unsafeDataFolder: return "The AI RAM Guard data folder is a symbolic link. Choose a regular user folder."
        }
    }
}

public enum SystemCommand {
    public static func run(_ executable: String, _ arguments: [String]) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        process.environment = ProcessInfo.processInfo.environment.merging(["LC_ALL": "C"]) { _, fresh in fresh }
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        try process.run()
        let deadline = DispatchWorkItem { if process.isRunning { process.terminate() } }
        DispatchQueue.global().asyncAfter(deadline: .now() + 15, execute: deadline)
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        deadline.cancel()
        guard process.terminationStatus == 0 else { throw GuardError.processList("Try Refresh again.") }
        return String(decoding: data, as: UTF8.self)
    }
}

public enum ProcessReader {
    public static func parseLine(_ line: String, identity: (Int32) -> ProcessIdentity?, userID: UInt32) -> ProcessRecord? {
        let columns = line.split(maxSplits: 4, omittingEmptySubsequences: true, whereSeparator: { $0 == " " || $0 == "\t" })
        guard columns.count == 5, let pid = Int32(columns[0]), let ppid = Int32(columns[1]),
              let uid = UInt32(columns[2]), uid == userID, let rss = UInt64(columns[3]),
              rss <= UInt64.max / 1024, let stamp = identity(pid), stamp.parentPID == ppid, stamp.userID == uid else { return nil }
        return ProcessRecord(identity: stamp, bytes: rss * 1024, command: String(columns[4]))
    }
    public static func read() throws -> [ProcessRecord] {
        let listing = try SystemCommand.run("/bin/ps", ["-axo", "pid=,ppid=,uid=,rss=,args="])
        return listing.split(separator: "\n").compactMap { parseLine(String($0), identity: ProcessIdentity.read, userID: getuid()) }
    }
}

public enum Analyzer {
    public static func agentName(_ process: ProcessRecord) -> String? {
        let command = process.command
        let first = String(command.split(separator: " ").first ?? "")
        var name = URL(fileURLWithPath: first).lastPathComponent.lowercased()
        if command.hasPrefix("/"), let marker = command.range(of: ".app/Contents/MacOS/") {
            let bundle = String(command[..<marker.lowerBound])
            // Only a main executable: nested Helper.app processes belong to their ancestor instead.
            name = URL(fileURLWithPath: bundle).lastPathComponent.lowercased()
        }
        switch name {
        case "claude": return "Claude"
        case "codex", "chatgpt": return "Codex / ChatGPT"
        case "cursor": return "Cursor"
        case "windsurf": return "Windsurf"
        case "code", "visual studio code", "code - insiders": return "VS Code"
        case "ollama": return "Ollama"
        case "lm studio": return "LM Studio"
        default:
            if ["node", "bun", "deno"].contains(name) && command.contains("@google/gemini-cli") { return "Gemini CLI" }
            return nil
        }
    }

    public static func helperName(_ process: ProcessRecord) -> String? {
        let command = process.command.lowercased()
        let known = ["desktop-commander", "@modelcontextprotocol/", "mcp-server", "codex-app-tools", "cua-repl", "node_repl", "npx-cli.js"]
        if let name = known.first(where: { command.contains($0) }) { return name }
        if command.range(of: #"(?:^|[\s/@_-])mcp(?:[\s/_-]|$)"#, options: .regularExpression) != nil { return "MCP server" }
        return nil
    }

    public static func ownerProcess(of process: ProcessRecord, in byPID: [Int32: ProcessRecord]) -> ProcessRecord? {
        var current: ProcessRecord? = process
        var seen = Set<Int32>()
        for _ in 0..<128 {
            guard let p = current, seen.insert(p.identity.pid).inserted else { break }
            if agentName(p) != nil { return p }
            guard p.identity.parentPID > 1, let parent = byPID[p.identity.parentPID],
                  parent.identity.startedSeconds <= p.identity.startedSeconds else { break }
            current = parent
        }
        return nil
    }

    public static func owner(of process: ProcessRecord, in byPID: [Int32: ProcessRecord]) -> String? {
        ownerProcess(of: process, in: byPID).flatMap(agentName)
    }

    public static func analyze(_ processes: [ProcessRecord], previouslySeen: Set<String>, previousOwners: [String: String] = [:], totalMemory: UInt64 = 0, reclaimableMemory: UInt64 = 0) -> (ScanReport, Set<String>) {
        let byPID = Dictionary(processes.map { ($0.identity.pid, $0) }, uniquingKeysWith: { first, _ in first })
        let alive = Set(processes.map { $0.identity.key })
        var observed = previouslySeen.intersection(alive)
        var groups: [String: [ProcessRecord]] = [:]
        var leftovers: [Leftover] = []
        for process in processes {
            if let owner = owner(of: process, in: byPID) {
                groups[owner, default: []].append(process)
                if helperName(process) != nil { observed.insert(process.identity.key) }
            } else if observed.contains(process.identity.key), let helper = helperName(process),
                      previousOwners[process.identity.key].map({ !alive.contains($0) }) ?? true,
                      process.identity.parentPID <= 1 || byPID[process.identity.parentPID] == nil {
                // A live non-AI parent can still be a launcher/session host. Leave it alone.
                leftovers.append(Leftover(identity: process.identity, bytes: process.bytes, helper: helper))
            }
        }
        let usages = groups.map { name, members in
            let helpers = Dictionary(grouping: members.compactMap(helperName), by: { $0 }).mapValues(\.count).filter { $0.value > 1 }
            return AgentUsage(name: name, bytes: members.reduce(0) { $0 + $1.bytes }, processCount: members.count, duplicateHelpers: helpers)
        }.sorted { $0.bytes > $1.bytes }
        return (ScanReport(measuredAt: Date(), totalMemory: totalMemory, reclaimableMemory: reclaimableMemory, agents: usages, leftovers: leftovers), observed)
    }

    public static func canClose(_ target: Leftover, current: [ProcessRecord], previouslySeen: Set<String>, previousOwners: [String: String] = [:], userID: UInt32) -> Bool {
        guard target.identity.userID == userID, previouslySeen.contains(target.id),
              let currentProcess = current.first(where: { $0.identity.isSameProcess(as: target.identity) }),
              helperName(currentProcess) != nil, agentName(currentProcess) == nil,
              previousOwners[target.id].map({ origin in !current.contains(where: { $0.identity.key == origin }) }) ?? true else { return false }
        let byPID = Dictionary(current.map { ($0.identity.pid, $0) }, uniquingKeysWith: { first, _ in first })
        return owner(of: currentProcess, in: byPID) == nil && (currentProcess.identity.parentPID <= 1 || byPID[currentProcess.identity.parentPID] == nil)
    }
}

public actor GuardEngine {
    private var observed = Set<String>()
    private var originalOwners: [String: String] = [:]
    private struct LineageState: Codable { let seen: [String]; let owners: [String: String] }
    private var loaded = false
    private let dataFolder: URL
    public init(dataFolder: URL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/AiRamGuard", isDirectory: true)) { self.dataFolder = dataFolder }
    private var lineageFile: URL { dataFolder.appendingPathComponent("mac-lineage.json") }
    private func load() throws {
        guard !loaded else { return }
        try FileManager.default.createDirectory(at: dataFolder, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        guard dataFolder.resolvingSymlinksInPath().standardizedFileURL == dataFolder.standardizedFileURL else { throw GuardError.unsafeDataFolder }
        if let data = try? Data(contentsOf: lineageFile), let state = try? JSONDecoder().decode(LineageState.self, from: data) {
            observed = Set(state.seen); originalOwners = state.owners
        }
        loaded = true
    }
    public func scan() throws -> ScanReport {
        try load()
        let processes = try ProcessReader.read()
        let byPID = Dictionary(processes.map { ($0.identity.pid, $0) }, uniquingKeysWith: { first, _ in first })
        for process in processes where Analyzer.helperName(process) != nil {
            if let owner = Analyzer.ownerProcess(of: process, in: byPID) { originalOwners[process.identity.key] = owner.identity.key }
        }
        let (report, seen) = Analyzer.analyze(processes, previouslySeen: observed, previousOwners: originalOwners, totalMemory: ProcessInfo.processInfo.physicalMemory, reclaimableMemory: guard_available_memory())
        observed = seen
        originalOwners = originalOwners.filter { seen.contains($0.key) }
        try JSONEncoder().encode(LineageState(seen: Array(seen).sorted(), owners: originalOwners)).write(to: lineageFile, options: .atomic)
        return report
    }
    public func closeLeftovers(_ targets: [Leftover]) throws -> Int {
        try load()
        var closed = 0
        for target in targets {
            let current = try ProcessReader.read()
            guard originalOwners[target.id] != nil,
                  Analyzer.canClose(target, current: current, previouslySeen: observed, previousOwners: originalOwners, userID: getuid()),
                  let final = ProcessIdentity.read(target.identity.pid), final.isSameProcess(as: target.identity),
                  final.userID == getuid(), final.parentPID <= 1 || !current.contains(where: { $0.identity.pid == final.parentPID }) else { continue }
            // macOS does not expose pidfd: identity is checked immediately before SIGTERM, never SIGKILL.
            if kill(target.identity.pid, SIGTERM) == 0 { closed += 1 }
        }
        return closed
    }
}

public struct DiskUsage: Identifiable, Sendable {
    public var id: String { path }
    public let name: String
    public let path: String
    public let bytes: UInt64
    public let incomplete: Bool
}

public enum DiskReader {
    public static func scan(home: URL = FileManager.default.homeDirectoryForCurrentUser) -> [DiskUsage] {
        let folders = [("Claude", ".claude"), ("Codex", ".codex"), ("npm cache", ".npm"), ("uv cache", ".cache/uv"), ("pip cache", "Library/Caches/pip"), ("Hugging Face", ".cache/huggingface"), ("Ollama models", ".ollama"), ("Playwright", "Library/Caches/ms-playwright")]
        return folders.compactMap { name, relative in
            let folder = home.appendingPathComponent(relative, isDirectory: true)
            guard FileManager.default.fileExists(atPath: folder.path), folder.resolvingSymlinksInPath().standardizedFileURL == folder.standardizedFileURL else { return nil }
            let keys: Set<URLResourceKey> = [.isRegularFileKey, .isSymbolicLinkKey, .totalFileAllocatedSizeKey, .fileAllocatedSizeKey]
            var incomplete = false
            guard let walker = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: Array(keys), options: [.skipsPackageDescendants], errorHandler: { _, _ in incomplete = true; return true }) else { return nil }
            var total: UInt64 = 0
            var visited = 0
            for case let file as URL in walker {
                visited += 1
                // Keep pathological caches bounded. Measurement only; no cache is ever deleted or moved.
                if visited > 500_000 { incomplete = true; break }
                guard let values = try? file.resourceValues(forKeys: keys) else { incomplete = true; continue }
                if values.isSymbolicLink == true { walker.skipDescendants(); continue }
                if values.isRegularFile == true { total += UInt64(max(0, values.totalFileAllocatedSize ?? values.fileAllocatedSize ?? 0)) }
            }
            return DiskUsage(name: name, path: folder.path, bytes: total, incomplete: incomplete)
        }.sorted { $0.bytes > $1.bytes }
    }
}
