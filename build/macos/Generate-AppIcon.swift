// Regenerate src/LemonNeo.Desktop/Resources/Icons/App.icns from the shared music geometry in
// Icons.axaml. The Windows .ico is a different canvas with different rules and stays with
// build/Generate-DesktopIcon.ps1; this script only writes the macOS icon.
//
// macOS 26 confines app icons to Apple's icon grid — an 824px body on a 1024px canvas with a
// 185px corner — and draws a grey plate behind any icon that does not sit on it. The artwork is
// therefore laid out on that grid rather than edge to edge, which is why it does not match the
// 56px brand tile the app draws in its own sidebar.
//
// Usage: swift build/macos/Generate-AppIcon.swift [output.icns]
// Must run on macOS: AppKit and CoreGraphics do the drawing.

import AppKit

// --- geometry --------------------------------------------------------------------------

let canvasUnits = 64.0                          // design space, matching the PS1
let brandTileUnits = 56.0                       // the tile Icon.AppMusic is centred on
let tileSize = canvasUnits * 824.0 / 1024.0     // Apple's grid: the icon body
let tileInset = (canvasUnits - tileSize) / 2
let tileCorner = canvasUnits * 185.4 / 1024.0

let accent = NSColor(srgbRed: 220 / 255.0, green: 236 / 255.0, blue: 126 / 255.0, alpha: 1)
let foreground = NSColor(srgbRed: 23 / 255.0, green: 32 / 255.0, blue: 39 / 255.0, alpha: 1)

func fail(_ message: String) -> Never {
    FileHandle.standardError.write("error: \(message)\n".data(using: .utf8)!)
    exit(1)
}

// --- shared artwork --------------------------------------------------------------------

let repositoryRoot = URL(fileURLWithPath: #filePath, relativeTo: URL(fileURLWithPath: FileManager.default.currentDirectoryPath))
    .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
let iconsPath = repositoryRoot.appendingPathComponent("src/LemonNeo.Desktop/Resources/Themes/Icons.axaml")
guard let axaml = try? String(contentsOf: iconsPath, encoding: .utf8) else {
    fail("could not read \(iconsPath.path)")
}

func firstCapture(_ pattern: String, in text: String) -> String {
    let expression = try! NSRegularExpression(pattern: pattern)
    let range = NSRange(text.startIndex..., in: text)
    guard let match = expression.firstMatch(in: text, range: range), match.numberOfRanges > 1,
          let capture = Range(match.range(at: 1), in: text) else {
        fail("\(pattern) not found in Icons.axaml")
    }
    return String(text[capture])
}

let musicGeometry = firstCapture(#"x:Key="Icon\.Music">([^<]+)</StreamGeometry>"#, in: axaml)
let placement = firstCapture(#"x:Key="Icon\.AppMusic">[\s\S]*?Matrix="([^"]+)""#, in: axaml)
    .split(separator: ",").map { Double($0.trimmingCharacters(in: .whitespaces))! }
guard placement.count == 6 else { fail("Icon.AppMusic matrix has \(placement.count) values, expected 6") }

// Icon.AppMusic centres the glyph on the 56px brand tile; carry it onto the smaller icon tile so
// it keeps the same optical position.
let glyphScale = tileSize / brandTileUnits
let glyphMatrix = CGAffineTransform(
    a: placement[0] * glyphScale, b: placement[1] * glyphScale,
    c: placement[2] * glyphScale, d: placement[3] * glyphScale,
    tx: tileInset + placement[4] * glyphScale, ty: tileInset + placement[5] * glyphScale)

/// Parses the subset of SVG path syntax the icon geometry uses, in the same 64-unit, y-down
/// design space as the PowerShell generator.
func parsePath(_ data: String) -> CGPath {
    let expression = try! NSRegularExpression(pattern: "[MLCZ]|-?(?:\\d*\\.)?\\d+")
    let range = NSRange(data.startIndex..., in: data)
    let tokens = expression.matches(in: data, range: range).map { String(data[Range($0.range, in: data)!]) }
    let path = CGMutablePath()
    var index = 0
    func next() -> Double { defer { index += 1 }; return Double(tokens[index])! }
    while index < tokens.count {
        let command = tokens[index]
        index += 1
        switch command {
        case "M": path.move(to: CGPoint(x: next(), y: next()))
        case "L": path.addLine(to: CGPoint(x: next(), y: next()))
        case "C":
            let first = CGPoint(x: next(), y: next())
            let second = CGPoint(x: next(), y: next())
            path.addCurve(to: CGPoint(x: next(), y: next()), control1: first, control2: second)
        case "Z": path.closeSubpath()
        default: fail("unsupported music geometry command: \(command)")
        }
    }
    return path
}

var transform = glyphMatrix
let glyph = parsePath(musicGeometry).copy(using: &transform)!

// --- rendering -------------------------------------------------------------------------

/// Draws one frame at 4x and downsamples it, the same way the PowerShell generator avoids
/// jagged edges on the small sizes.
func frame(size: Int) -> Data {
    let supersample = size * 4
    let large = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: supersample, pixelsHigh: supersample,
                                 bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                 colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: large)
    let scale = Double(supersample) / canvasUnits
    NSGraphicsContext.current!.cgContext.translateBy(x: 0, y: CGFloat(supersample))
    NSGraphicsContext.current!.cgContext.scaleBy(x: 1, y: -1)   // design space is y-down
    NSGraphicsContext.current!.cgContext.scaleBy(x: CGFloat(scale), y: CGFloat(scale))
    NSGraphicsContext.current!.shouldAntialias = true

    accent.setFill()
    NSGraphicsContext.current!.cgContext.addPath(CGPath(
        roundedRect: CGRect(x: tileInset, y: tileInset, width: tileSize, height: tileSize),
        cornerWidth: tileCorner, cornerHeight: tileCorner, transform: nil))
    NSGraphicsContext.current!.cgContext.fillPath()

    foreground.setFill()
    NSGraphicsContext.current!.cgContext.addPath(glyph)
    NSGraphicsContext.current!.cgContext.fillPath()
    NSGraphicsContext.current!.flushGraphics()
    NSGraphicsContext.restoreGraphicsState()

    let small = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size,
                                 bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                 colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: small)
    NSGraphicsContext.current!.imageInterpolation = .high
    large.draw(in: NSRect(x: 0, y: 0, width: size, height: size))
    NSGraphicsContext.current!.flushGraphics()
    NSGraphicsContext.restoreGraphicsState()
    return small.representation(using: .png, properties: [:])!
}

// --- .icns container -------------------------------------------------------------------

// PNG-backed entries, including both the 1x and 2x slot of every Retina size.
let entries: [(type: String, size: Int)] = [
    ("icp4", 16), ("ic11", 32), ("icp5", 32), ("ic12", 64), ("ic07", 128),
    ("ic13", 256), ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024),
]

var frames: [Int: Data] = [:]
for size in Set(entries.map(\.size)) { frames[size] = frame(size: size) }

func bigEndian(_ value: UInt32) -> [UInt8] {
    [UInt8(value >> 24 & 0xFF), UInt8(value >> 16 & 0xFF), UInt8(value >> 8 & 0xFF), UInt8(value & 0xFF)]
}

var payload: [UInt8] = Array("icns".utf8)
payload += bigEndian(UInt32(8 + entries.reduce(0) { $0 + 8 + frames[$1.size]!.count }))
for entry in entries {
    let data = frames[entry.size]!
    payload += Array(entry.type.utf8)
    payload += bigEndian(UInt32(data.count + 8))
    payload += [UInt8](data)
}

let output = CommandLine.arguments.count > 1
    ? URL(fileURLWithPath: CommandLine.arguments[1])
    : repositoryRoot.appendingPathComponent("src/LemonNeo.Desktop/Resources/Icons/App.icns")
try! Data(payload).write(to: output)
print("Wrote \(output.path) (\(payload.count) bytes, \(entries.count) sizes)")
