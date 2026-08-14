using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Mods;

namespace CoiCoop;

public sealed class CoiCoopMod : DataOnlyMod {
    public CoiCoopMod(ModManifest manifest) : base(manifest) {
        Log.Info("COI-Coop: constructed");
    }

    public override void RegisterPrototypes(ProtoRegistrator registrator) {
        Log.Info("COI-Coop: network foundation loaded");
    }

    public override void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) {
    }
}
