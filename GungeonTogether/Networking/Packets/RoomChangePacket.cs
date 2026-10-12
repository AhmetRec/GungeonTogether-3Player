using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    public class RoomChangePacket : INetworkPacket
    {
        public PacketType Type => PacketType.RoomChange;
        public string RoomName;
        public ulong PlayerId;
        public Vector2 AnchorPosition;
        public bool IsRequest;

        public void Serialize(BinaryWriter w)
        {
            w.Write(RoomName ?? "");
            w.Write(PlayerId);
            w.Write(AnchorPosition.x);
            w.Write(AnchorPosition.y);
            w.Write(IsRequest);
        }

        public void Deserialize(BinaryReader r)
        {
            RoomName = r.ReadString();
            PlayerId = r.ReadUInt64();
            AnchorPosition = new Vector2(r.ReadSingle(), r.ReadSingle());
            IsRequest = r.ReadBoolean();
        }
    }
}
