using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    public class WorldStatePacket : INetworkPacket
    {
        public PacketType Type => PacketType.WorldState;

        public bool IsFoyer;
        public int FloorIndex;          // -1 if foyer
        public int RunSeed;
        public string LevelName;
        public string RoomIdentifier;   // room name or unique ID
        public Vector2 Position;
        public float Rotation;
        public int LayoutHash;          // fingerprint of the generated room layout (0 = unknown)

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(IsFoyer);
            writer.Write(FloorIndex);
            writer.Write(RunSeed);
            writer.Write(LevelName ?? "");
            writer.Write(RoomIdentifier ?? "");
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(Rotation);
            writer.Write(LayoutHash);
        }

        public void Deserialize(BinaryReader reader)
        {
            IsFoyer = reader.ReadBoolean();
            FloorIndex = reader.ReadInt32();
            RunSeed = reader.ReadInt32();
            LevelName = reader.ReadString();
            RoomIdentifier = reader.ReadString();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Rotation = reader.ReadSingle();
            LayoutHash = reader.ReadInt32();
        }
    }
}
