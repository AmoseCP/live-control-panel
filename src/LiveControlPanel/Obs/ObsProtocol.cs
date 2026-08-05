using System.Security.Cryptography;
using System.Text;

namespace LiveControlPanel.Obs;

/// <summary>obs-websocket v5 opcodes.</summary>
public static class ObsOp
{
    public const int Hello = 0;
    public const int Identify = 1;
    public const int Identified = 2;
    public const int Event = 5;
    public const int Request = 6;
    public const int RequestResponse = 7;
}

/// <summary>
/// obs-websocket v5 event subscription bitmask. The high-volume groups (from bit 16 up) are
/// deliberately excluded from <see cref="All"/> by the protocol, which is why
/// <see cref="InputVolumeMeters"/> must be requested explicitly — FR 4.4 needs it for the
/// audio-level check.
/// </summary>
[Flags]
public enum ObsEventSubscription
{
    None = 0,
    General = 1 << 0,
    Config = 1 << 1,
    Scenes = 1 << 2,
    Inputs = 1 << 3,
    Transitions = 1 << 4,
    Filters = 1 << 5,
    Outputs = 1 << 6,
    SceneItems = 1 << 7,
    MediaInputs = 1 << 8,
    Vendors = 1 << 9,
    Ui = 1 << 10,
    All = General | Config | Scenes | Inputs | Transitions | Filters | Outputs | SceneItems | MediaInputs | Vendors | Ui,
    InputVolumeMeters = 1 << 16,
    InputActiveStateChanged = 1 << 17,
    InputShowStateChanged = 1 << 18,
    SceneItemTransformChanged = 1 << 19,
}

public static class ObsAuth
{
    /// <summary>
    /// v5 challenge: base64(sha256(base64(sha256(password + salt)) + challenge)).
    /// </summary>
    public static string BuildAuthentication(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }
}
