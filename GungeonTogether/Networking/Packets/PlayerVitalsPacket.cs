using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    public class PlayerVitalsPacket : INetworkPacket
    {
        public PacketType Type => PacketType.PlayerVitals;
        public ulong PlayerId;
        public float Health;
        public float Armor;
        public bool IsDowned;
        public int Currency;
        public int Keys;
        public int Blanks;
        public int CurrentGunAmmo;
        // Extra fields for the corner HUD (protocol v12).
        public float MaxHealth;
        public float MaxArmor;
        public int MaxAmmo;          // -1 when unknown
        public bool InfiniteAmmo;
        public string ActiveItemKey = "";
        public int ActiveItemId = -1;
        public int PingMs;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write(Health);
            writer.Write(Armor);
            writer.Write(IsDowned);
            writer.Write(Currency);
            writer.Write(Keys);
            writer.Write(Blanks);
            writer.Write(CurrentGunAmmo);
            writer.Write(MaxHealth);
            writer.Write(MaxArmor);
            writer.Write(MaxAmmo);
            writer.Write(InfiniteAmmo);
            writer.Write(ActiveItemKey ?? "");
            writer.Write(ActiveItemId);
            writer.Write(PingMs);
        }

        public void Deserialize(BinaryReader reader)
        {
            PlayerId = reader.ReadUInt64();
            Health = reader.ReadSingle();
            Armor = reader.ReadSingle();
            IsDowned = reader.ReadBoolean();
            Currency = reader.ReadInt32();
            Keys = reader.ReadInt32();
            Blanks = reader.ReadInt32();
            CurrentGunAmmo = reader.ReadInt32();
            MaxHealth = reader.ReadSingle();
            MaxArmor = reader.ReadSingle();
            MaxAmmo = reader.ReadInt32();
            InfiniteAmmo = reader.ReadBoolean();
            ActiveItemKey = reader.ReadString();
            ActiveItemId = reader.ReadInt32();
            PingMs = reader.ReadInt32();
        }
    }
}
