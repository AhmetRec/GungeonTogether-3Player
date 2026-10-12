using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    public class LatencyPingPacket : INetworkPacket
    {
        public PacketType Type => PacketType.LatencyPing;
        public uint Sequence;
        public float SentAt;
        public bool IsReply;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(Sequence);
            writer.Write(SentAt);
            writer.Write(IsReply);
        }

        public void Deserialize(BinaryReader reader)
        {
            Sequence = reader.ReadUInt32();
            SentAt = reader.ReadSingle();
            IsReply = reader.ReadBoolean();
        }
    }

    public class GroundPickupSpawnPacket : INetworkPacket
    {
        public PacketType Type => PacketType.GroundPickupSpawn;
        public int WorldObjectId;
        public int PickupId;
        public string PickupKey;
        public Vector2 Position;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(WorldObjectId);
            writer.Write(PickupId);
            writer.Write(PickupKey ?? "");
            writer.Write(Position.x);
            writer.Write(Position.y);
        }

        public void Deserialize(BinaryReader reader)
        {
            WorldObjectId = reader.ReadInt32();
            PickupId = reader.ReadInt32();
            PickupKey = reader.ReadString();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }
    }
}
