import XCTest
@testable import GuardCore

final class GuardCoreTests: XCTestCase {
    private func process(_ pid: Int32, _ parent: Int32, _ command: String, start: UInt64 = 100, uid: UInt32 = 501, micros: UInt64 = 7) -> ProcessRecord {
        ProcessRecord(identity: ProcessIdentity(pid: pid, parentPID: parent, userID: uid, startedSeconds: start, startedMicroseconds: micros), bytes: 1024 * 1024, command: command)
    }

    func testActiveHelpersAreGroupedAndRememberedButNeverOrphans() {
        let root = process(10, 1, "/Applications/Claude.app/Contents/MacOS/Claude")
        let helper = process(11, 10, "/usr/local/bin/node /tmp/mcp-server/index.js", start: 101)
        let (report, observed) = Analyzer.analyze([root, helper], previouslySeen: [])
        XCTAssertEqual(report.agents.count, 1)
        XCTAssertEqual(report.agents[0].processCount, 2)
        XCTAssertTrue(observed.contains(helper.identity.key))
        XCTAssertTrue(report.leftovers.isEmpty)
    }

    func testOnlyPreviouslyObservedDetachedHelpersBecomeLeftovers() {
        let known = process(11, 1, "/usr/local/bin/node /tmp/mcp-server/index.js", start: 101)
        let unknown = process(12, 1, "/usr/local/bin/node /tmp/mcp-server/other.js", start: 101)
        let (report, _) = Analyzer.analyze([known, unknown], previouslySeen: [known.identity.key])
        XCTAssertEqual(report.leftovers.map(\.identity.pid), [11])
        XCTAssertTrue(Analyzer.canClose(report.leftovers[0], current: [known, unknown], previouslySeen: [known.identity.key], userID: 501))
    }

    func testLiveNonAIOwnerIsNotAStandaloneLeftover() {
        let shell = process(9, 1, "/bin/zsh")
        let helper = process(11, 9, "/usr/local/bin/node /tmp/mcp-server/index.js", start: 101)
        let (report, _) = Analyzer.analyze([shell, helper], previouslySeen: [helper.identity.key])
        XCTAssertTrue(report.leftovers.isEmpty)
    }

    func testDetachedHelperIsKeptWhileItsOriginalAIAppStillRuns() {
        let root = process(10, 1, "/Applications/Claude.app/Contents/MacOS/Claude")
        let helper = process(11, 1, "/usr/local/bin/node /tmp/mcp-server/index.js", start: 101)
        let owners = [helper.identity.key: root.identity.key]
        let target = Analyzer.analyze([helper], previouslySeen: [helper.identity.key], previousOwners: owners).0.leftovers[0]
        let report = Analyzer.analyze([root, helper], previouslySeen: [helper.identity.key], previousOwners: owners).0
        XCTAssertTrue(report.leftovers.isEmpty)
        XCTAssertFalse(Analyzer.canClose(target, current: [root, helper], previouslySeen: [helper.identity.key], previousOwners: owners, userID: 501))
    }

    func testReusedPIDWithinSameSecondIsDifferentProcess() {
        let old = process(11, 1, "/usr/local/bin/node /tmp/mcp-server/index.js", micros: 1)
        let replacement = process(11, 1, "/usr/local/bin/node /tmp/mcp-server/index.js", micros: 2)
        let (report, _) = Analyzer.analyze([old], previouslySeen: [old.identity.key])
        XCTAssertFalse(Analyzer.canClose(report.leftovers[0], current: [replacement], previouslySeen: [old.identity.key], userID: 501))
        XCTAssertTrue(Analyzer.analyze([replacement], previouslySeen: [old.identity.key]).0.leftovers.isEmpty)
    }

    func testOwnerAppearingAgainPreventsClosing() {
        let detached = process(11, 1, "/usr/local/bin/node /tmp/mcp-server/index.js", start: 101)
        let target = Analyzer.analyze([detached], previouslySeen: [detached.identity.key]).0.leftovers[0]
        let root = process(10, 1, "/Applications/Cursor.app/Contents/MacOS/Cursor")
        let attached = process(11, 10, "/usr/local/bin/node /tmp/mcp-server/index.js", start: 101)
        XCTAssertFalse(Analyzer.canClose(target, current: [root, attached], previouslySeen: [target.id], userID: 501))
    }

    func testWrongUserAndUnrelatedProgramAreNeverClosed() {
        let helper = process(11, 1, "/usr/local/bin/node /tmp/mcp-server/index.js")
        let target = Analyzer.analyze([helper], previouslySeen: [helper.identity.key]).0.leftovers[0]
        XCTAssertFalse(Analyzer.canClose(target, current: [helper], previouslySeen: [target.id], userID: 502))
        let changed = process(11, 1, "/usr/bin/python3 /tmp/ordinary-script.py")
        XCTAssertFalse(Analyzer.canClose(target, current: [changed], previouslySeen: [target.id], userID: 501))
    }

    func testDeadPIDIsPrunedAndCyclesTerminate() {
        let a = process(1_001, 1_002, "/usr/local/bin/node mcp-server")
        let b = process(1_002, 1_001, "/bin/zsh")
        let result = Analyzer.analyze([a, b], previouslySeen: [a.identity.key, "501:999:1:1"])
        XCTAssertEqual(result.1, [a.identity.key])
        XCTAssertTrue(result.0.leftovers.isEmpty)
    }

    func testProcessParserKeepsCommandSpacesAndChecksLiveIdentity() {
        let stamp = ProcessIdentity(pid: 10, parentPID: 1, userID: 501, startedSeconds: 100)
        let parsed = ProcessReader.parseLine(" 10 1 501 2048 /Applications/Visual Studio Code.app/Contents/MacOS/Electron --type=renderer", identity: { _ in stamp }, userID: 501)
        XCTAssertEqual(parsed?.bytes, 2_097_152)
        XCTAssertEqual(parsed.flatMap(Analyzer.agentName), "VS Code")
        XCTAssertNil(ProcessReader.parseLine("10 2 501 2048 /bin/sh", identity: { _ in stamp }, userID: 501))
        XCTAssertNil(ProcessReader.parseLine("10 1 502 2048 /bin/sh", identity: { _ in stamp }, userID: 501))
        XCTAssertNil(ProcessReader.parseLine("10 1 501 18446744073709551615 /bin/sh", identity: { _ in stamp }, userID: 501))
        XCTAssertNil(ProcessReader.parseLine("nonsense", identity: { _ in stamp }, userID: 501))
        XCTAssertNil(Analyzer.agentName(process(20, 1, "/usr/bin/node /Applications/Claude.app/Contents/MacOS/Claude")))
    }

    func testDuplicatesCountCopiesAndSnapshotJSONDoesNotContainCommands() throws {
        let root = process(10, 1, "/Applications/ChatGPT.app/Contents/MacOS/ChatGPT")
        let a = process(11, 10, "/usr/local/bin/node mcp-server --token=SECRET")
        let b = process(12, 10, "/usr/local/bin/node mcp-server --token=SECRET")
        let report = Analyzer.analyze([root, a, b], previouslySeen: []).0
        XCTAssertEqual(report.agents[0].duplicateHelpers["mcp-server"], 2)
        XCTAssertFalse(String(decoding: try JSONEncoder().encode(report), as: UTF8.self).contains("SECRET"))
    }

    func testDiskReaderDoesNotFollowSymlinks() throws {
        let temporary = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let real = temporary.appendingPathComponent(".claude")
        let external = temporary.appendingPathComponent("outside")
        try FileManager.default.createDirectory(at: real, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: external, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporary) }
        try Data(repeating: 1, count: 4096).write(to: external.appendingPathComponent("secret"))
        try FileManager.default.createSymbolicLink(at: real.appendingPathComponent("linked"), withDestinationURL: external)
        let result = DiskReader.scan(home: temporary)
        XCTAssertEqual(result.first(where: { $0.name == "Claude" })?.bytes, 0)
    }
}
