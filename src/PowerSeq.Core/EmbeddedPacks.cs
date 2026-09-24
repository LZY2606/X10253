using System.Reflection;

namespace PowerSeq.Core;

/// <summary>仓库自带的离线案例包（嵌入资源），不访问公网即可载入。</summary>
public static class EmbeddedPacks
{
    public const string EvidenceRoomResource = "PowerSeq.samples.power-sequencing-evidence-room.json";

    public static string ReadEvidenceRoom()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(EvidenceRoomResource)
            ?? throw new InvalidOperationException($"embedded resource {EvidenceRoomResource} not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
