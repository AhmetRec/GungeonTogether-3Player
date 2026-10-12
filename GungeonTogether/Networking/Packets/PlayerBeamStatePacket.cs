using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    public class PlayerBeamStatePacket : INetworkPacket
    {
        public PacketType Type => PacketType.PlayerBeamState;
        public ulong PlayerId;
        public int BeamId;
        public int GunId;
        public string GunKey;
        public string ProjectileName;
        public Vector2 Origin;
        public Vector2 Direction;
        public float DamageModifier;
        public bool Active;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(PlayerId); writer.Write(BeamId); writer.Write(GunId);
            writer.Write(GunKey ?? ""); writer.Write(ProjectileName ?? "");
            writer.Write(Origin.x); writer.Write(Origin.y);
            writer.Write(Direction.x); writer.Write(Direction.y);
            writer.Write(DamageModifier); writer.Write(Active);
        }

        public void Deserialize(BinaryReader reader)
        {
            PlayerId = reader.ReadUInt64(); BeamId = reader.ReadInt32(); GunId = reader.ReadInt32();
            GunKey = reader.ReadString(); ProjectileName = reader.ReadString();
            Origin = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Direction = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            DamageModifier = reader.ReadSingle(); Active = reader.ReadBoolean();
        }
    }
}
