using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CoiCoop.Networking;

internal static class LiveBuildIdentity {
    internal static string Calculate(string gameRoot, string modDll) {
        var managed = Path.Combine(gameRoot, "Captain of Industry_Data", "Managed");
        var names = new[] { "Mafi.dll", "Mafi.Core.dll", "Mafi.Base.dll", "Mafi.Unity.dll" };
        var text = new StringBuilder("COI-LIVE-1\n");
        foreach (var name in names) text.Append(FileHash(Path.Combine(managed, name))).Append('\n');
        text.Append(FileHash(modDll));
        return LiveWorldServer.Hash(Encoding.UTF8.GetBytes(text.ToString()));
    }

    internal static string FileHash(string path) {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }
}
