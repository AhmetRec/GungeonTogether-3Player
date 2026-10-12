using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    public class ConnectionRequestPacket : INetworkPacket
    {
        public PacketType Type => PacketType.ConnectionRequest;

        public ulong ClientId;
        public int ProtocolVersion;
        public string ModManifest;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(ClientId);
            writer.Write(ProtocolVersion);
            writer.Write(ModManifest ?? "");
        }

        public void Deserialize(BinaryReader reader)
        {
            ClientId = reader.ReadUInt64();
            ProtocolVersion = reader.ReadInt32();
            ModManifest = reader.ReadString();
        }
    }
}
