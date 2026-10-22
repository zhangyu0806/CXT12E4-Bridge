import AppKit
import Darwin

// A fresh process avoids stale NSScreen.visibleFrame state in long-lived apps.
// The first NSScreen is the primary display. This helper never activates an app.
guard let screen = NSScreen.screens.first else {
    fputs("Primary display is unavailable\n", stderr)
    exit(1)
}

let inset = screen.frame.maxY - screen.visibleFrame.maxY
guard inset.isFinite, inset >= 0, inset <= screen.frame.height else {
    fputs("Invalid primary display inset\n", stderr)
    exit(1)
}

print(inset)
