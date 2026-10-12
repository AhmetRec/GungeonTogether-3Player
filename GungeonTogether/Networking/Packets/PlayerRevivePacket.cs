using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    public class PlayerRevivePacket : INetworkPacket
    {
        public PacketType Type => PacketType.PlayerRevive;
        public ulong PlayerId;
        public float Health;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write(Health);
        }

        public void Deserialize(BinaryReader reader)
        {
            PlayerId = reader.ReadUInt64();
            Health = reader.ReadSingle();
        }
    }
}
