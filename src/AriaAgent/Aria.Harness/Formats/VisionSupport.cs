namespace Aria.Harness.Formats;

public enum VisionSupport
{
    Unknown,      // not yet probed
    Supported,    // model correctly read a test image
    Unsupported   // model rejected or ignored the image
}
