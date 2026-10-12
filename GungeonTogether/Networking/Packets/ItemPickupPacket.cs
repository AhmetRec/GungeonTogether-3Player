using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    public class ItemPickupPacket : INetworkPacket
    {
        public PacketType Type => PacketType.ItemPickup;
        public ulong PlayerId;
        public int PickupId;
        public string PickupKey;
        public Vector2 Position;
        public int WorldObjectId;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write(PickupId);
            writer.Write(PickupKey ?? "");
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(WorldObjectId);
        }

        public void Deserialize(BinaryReader reader)
        {
            PlayerId = reader.ReadUInt64();
            PickupId = reader.ReadInt32();
            PickupKey = reader.ReadString();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            WorldObjectId = reader.ReadInt32();
        }
    }
}
