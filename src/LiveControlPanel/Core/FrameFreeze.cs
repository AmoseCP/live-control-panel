namespace LiveControlPanel.Core;

/// <summary>
/// Decides whether two frames of the same source are the same picture.
///
/// This is what lets the pre-flight tell a working camera from a capture card's "No Signal"
/// placeholder — a distinction OBS itself cannot make, because to OBS the placeholder is a
/// perfectly good picture and <c>GetSourceActive</c> reports the source as active.
///
/// The test is strict byte equality, not a similarity score. Two reasons:
/// <list type="bullet">
/// <item>The frames arrive PNG-encoded. A single changed pixel cascades through deflate, so a
/// "percentage of bytes that differ" would mean nothing at all.</item>
/// <item>A live camera never produces two identical frames. Sensor noise guarantees it, and it
/// guarantees it more in a dark room, not less. So equality is a very strong signal — which is what
/// a check that must not cry wolf at 04:40 needs.</item>
/// </list>
///
/// What it therefore does NOT catch: a placeholder that animates. That limit is accepted; the
/// alternative is a heuristic that produces false alarms, and an operator who has learned to ignore
/// the checklist is worse off than one with no check at all.
/// </summary>
public static class FrameFreeze
{
    /// <summary>
    /// True only when both samples exist and are identical. A missing sample answers false —
    /// "cannot tell" must never read as "no picture".
    /// </summary>
    public static bool LooksFrozen(byte[]? first, byte[]? second)
    {
        if (first is null || second is null) return false;
        if (first.Length == 0 || second.Length == 0) return false;

        return first.AsSpan().SequenceEqual(second);
    }
}
