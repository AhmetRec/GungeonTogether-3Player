using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;

namespace GungeonTogether.Networking
{
    public static class ModCompatibility
    {
        public static string GetManifest()
        {
            var entries = new List<string>();
            foreach (var pair in Chainloader.PluginInfos)
            {
                if (pair.Value == null || pair.Value.Metadata == null) continue;
                entries.Add("plugin:" + pair.Value.Metadata.GUID + "@" + pair.Value.Metadata.Version);
            }
            if (ETGMod.Databases.Items != null)
                foreach (string key in ETGMod.Databases.Items.ModItemMap.Keys)
                    entries.Add("item:" + key);
            entries.Sort(StringComparer.Ordinal);
            return string.Join(";", entries.ToArray());
        }

        public static string DescribeMismatch(string remoteManifest)
        {
            string local = GetManifest();
            string[] localEntries = local.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            string[] remoteEntries = (remoteManifest ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            int count = Math.Max(localEntries.Length, remoteEntries.Length);
            for (int i = 0; i < count; i++)
            {
                string expected = i < localEntries.Length ? localEntries[i] : "(none)";
                string received = i < remoteEntries.Length ? remoteEntries[i] : "(none)";
                if (!string.Equals(expected, received, StringComparison.Ordinal))
                    return "Mod mismatch at load position " + (i + 1) + ": host=" + expected + ", client=" + received;
            }
            return "Mod list or load order mismatch";
        }

        public static string GetPickupKey(PickupObject pickup)
        {
            if (pickup == null || ETGMod.Databases.Items == null) return "";
            string bestKey = "";
            foreach (var pair in ETGMod.Databases.Items.ModItemMap)
            {
                PickupObject registered = pair.Value;
                if (registered == null) continue;
                if (!ReferenceEquals(registered, pickup) && registered.PickupObjectId != pickup.PickupObjectId) continue;
                if (string.IsNullOrEmpty(bestKey) || string.CompareOrdinal(pair.Key, bestKey) < 0)
                    bestKey = pair.Key;
            }
            return bestKey;
        }

        public static PickupObject ResolvePickup(string key, int fallbackId)
        {
            if (!string.IsNullOrEmpty(key) && ETGMod.Databases.Items != null)
            {
                try
                {
                    PickupObject modded = ETGMod.Databases.Items[key];
                    if (modded != null) return modded;
                }
                catch (Exception e)
                {
                    GungeonTogether.Systems.Logging.Debug.LogWarning("[ModCompatibility] Could not resolve modded pickup '" + key + "': " + e.Message);
                }
            }
            return PickupObjectDatabase.GetById(fallbackId);
        }
    }
}
