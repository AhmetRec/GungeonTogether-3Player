using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    public class InteractableUsedPacket : INetworkPacket
    {
        public PacketType Type => PacketType.InteractableUsed;
        public ulong PlayerId;
        public string ObjectType;
        public Vector2 Position;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write(ObjectType ?? "");
            writer.Write(Position.x);
            writer.Write(Position.y);
        }

        public void Deserialize(BinaryReader reader)
        {
            PlayerId = reader.ReadUInt64();
            ObjectType = reader.ReadString();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }
    }

    public class BreakableStatePacket : INetworkPacket
    {
        public PacketType Type => PacketType.BreakableState;
        public string ObjectType;
        public Vector2 Position;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(ObjectType ?? "");
            writer.Write(Position.x);
            writer.Write(Position.y);
        }

        public void Deserialize(BinaryReader reader)
        {
            ObjectType = reader.ReadString();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }
    }

    public class RoomStatePacket : INetworkPacket
    {
        public PacketType Type => PacketType.RoomState;
        public string RoomIdentifier;
        public bool IsSealed;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(RoomIdentifier ?? "");
            writer.Write(IsSealed);
        }

        public void Deserialize(BinaryReader reader)
        {
            RoomIdentifier = reader.ReadString();
            IsSealed = reader.ReadBoolean();
        }
    }

    public class PlayerItemUsedPacket : INetworkPacket
    {
        public PacketType Type => PacketType.PlayerItemUsed;
        public ulong PlayerId;
        public int PickupId;
        public string PickupKey;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write(PickupId);
            writer.Write(PickupKey ?? "");
        }

        public void Deserialize(BinaryReader reader)
        {
            PlayerId = reader.ReadUInt64();
            PickupId = reader.ReadInt32();
            PickupKey = reader.ReadString();
        }
    }

    public class PlayerBlankUsedPacket : INetworkPacket
    {
        public PacketType Type => PacketType.PlayerBlankUsed;
        public ulong PlayerId;
        public int RemainingBlanks;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write(RemainingBlanks);
        }

        public void Deserialize(BinaryReader reader)
        {
            PlayerId = reader.ReadUInt64();
            RemainingBlanks = reader.ReadInt32();
        }
    }
}
