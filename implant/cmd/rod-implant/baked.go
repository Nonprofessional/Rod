package main

// bakedJSON is set by the Go build unit at generation time via -ldflags
// "-X main.bakedJSON=<base64-config>". architecture.md Sec 5.1 calls for the
// profile being baked into the artifact so each implant is self-contained; this
// is how the build unit embeds it without editing source. Empty when the binary
// was built without ldflags (dev runs), in which case flag/env drive everything.
var bakedJSON string
