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

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write(Health);
            writer.Write(Armor);
            writer.Write(IsDowned);
        }

        public void Deserialize(BinaryReader reader)
        {
            PlayerId = reader.ReadUInt64();
            Health = reader.ReadSingle();
            Armor = reader.ReadSingle();
            IsDowned = reader.ReadBoolean();
        }
    }
}
